using System.Runtime.InteropServices;
using System.Text;

namespace Anduri.Agent.Sensors.Nvidia;

/// <summary>
/// NVIDIA GPUs through NVML, which ships with the proprietary driver. No root needed.
/// The first GPU gets <c>gpu.*</c> and <c>mem.vram</c>, further GPUs <c>gpu1.*</c>, <c>gpu2.*</c> ….
/// </summary>
/// <remarks>
/// NVML has no hotspot temperature, so <c>gpu.hotspot</c> comes from NvAPI (<see cref="NvApi"/>) when the driver has it.
/// Functions are bound at runtime from the loaded library, so a machine without the driver just reports the source as unavailable.
/// </remarks>
public sealed unsafe partial class NvmlSource(bool readsHotspot = true, ILogger? logger = null) : ISensorSource, IDisposable
{
    private const int Success = 0;
    private const int ErrorGpuIsLost = 15;
    private const int TemperatureGpu = 0;
    private const int ClockGraphics = 0;
    private const int ClockMemory = 1;
    private const uint NameBufferSize = 96;
    /// <summary>NVML_DEVICE_ARCH_BLACKWELL; NVML_DEVICE_ARCH_UNKNOWN is 0xFFFFFFFF.</summary>
    private const uint ArchitectureBlackwell = 10;

    private static readonly string[] LibraryNames = OperatingSystem.IsWindows()
        ? ["nvml.dll"]
        : ["libnvidia-ml.so.1", "libnvidia-ml.so"];

    private IntPtr library;
    private Api* api;
    private List<Gpu>? gpus;
    private IReadOnlyList<SensorDescriptor> descriptors = [];
    private NvApi? nvApi;
    private readonly ILogger logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "nvidia";

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        gpus ??= Initialize();

        foreach (var gpu in gpus)
        {
            var result = ReadGpu(gpu, values);
            if (result == ErrorGpuIsLost)
            {
                // Fallen off the bus or driver reloaded: start over on the next read after the sampler's retry delay.
                Shutdown();
                throw new InvalidOperationException($"NVML lost GPU {gpu.Index} ({gpu.Name}).");
            }
        }

        return ValueTask.CompletedTask;
    }

    private List<Gpu> Initialize()
    {
        if (library == IntPtr.Zero)
        {
            foreach (var name in LibraryNames)
            {
                if (NativeLibrary.TryLoad(name, out library))
                    break;
            }

            if (library == IntPtr.Zero)
                throw new SensorSourceUnavailableException("libnvidia-ml.so.1 wasn't found (no NVIDIA proprietary driver)");
            api = BindApi(library);
        }

        var status = api->Init();
        if (status != Success)
            throw new SensorSourceUnavailableException($"nvmlInit failed: {ErrorString(status)}");

        uint count;
        Check(api->DeviceGetCount(&count), "nvmlDeviceGetCount");
        if (count == 0)
        {
            api->Shutdown();
            throw new SensorSourceUnavailableException("NVML found no GPUs");
        }

        var list = new List<Gpu>();
        var descriptorList = new List<SensorDescriptor>();
        for (uint index = 0; index < count; index++)
        {
            IntPtr device;
            if (api->DeviceGetHandleByIndex(index, &device) != Success)
                continue;

            var gpu = new Gpu((int)index, device, ReadName(device));
            list.Add(gpu);
            descriptorList.AddRange(Probe(gpu));
        }
        if (readsHotspot)
            descriptorList.AddRange(ProbeHotspots(list));

        descriptors = descriptorList;
        return list;
    }

    /// <summary>Finds out which values this GPU supports and builds its descriptors.</summary>
    private List<SensorDescriptor> Probe(Gpu gpu)
    {
        var p = gpu.Index == 0 ? "gpu" : $"gpu{gpu.Index}";
        var hardware = gpu.Name;
        var result = new List<SensorDescriptor>();
        uint value;
        Utilization utilization;
        Memory memory;

        if (api->DeviceGetUtilizationRates(gpu.Device, &utilization) == Success)
        {
            gpu.HasLoad = true;
            result.Add(SensorDescriptor.Create($"{p}.load", "GPU load", SensorKind.Load) with { Hardware = hardware, Label = "GPU" });
        }
        if (api->DeviceGetTemperature(gpu.Device, TemperatureGpu, &value) == Success)
        {
            gpu.HasTemperature = true;
            result.Add(SensorDescriptor.Create($"{p}.temp", "GPU core temperature", SensorKind.Temperature) with { Hardware = hardware, Label = "Core" });
        }
        if (api->DeviceGetPowerUsage(gpu.Device, &value) == Success)
        {
            gpu.HasPower = true;
            double? limit = api->DeviceGetEnforcedPowerLimit(gpu.Device, &value) == Success ? value / 1000.0 : null;
            result.Add(SensorDescriptor.Create($"{p}.power", "GPU board power", SensorKind.Power) with { Hardware = hardware, Label = "GPU", Maximum = limit });
        }
        if (api->DeviceGetMemoryInfo(gpu.Device, &memory) == Success)
        {
            gpu.HasMemory = true;
            var id = gpu.Index == 0 ? "mem.vram" : $"{p}.vram";
            result.Add(SensorDescriptor.Create(id, "GPU memory used", SensorKind.Memory) with
            {
                Hardware = hardware,
                Label = "VRAM",
                Capacity = Math.Round(memory.Total / BytesPerGb, 1),
            });
        }
        if (api->DeviceGetClockInfo(gpu.Device, ClockGraphics, &value) == Success)
        {
            gpu.HasCoreClock = true;
            double? maximum = api->DeviceGetMaxClockInfo(gpu.Device, ClockGraphics, &value) == Success && value > 0 ? value : null;
            result.Add(SensorDescriptor.Create($"{p}.clock", "GPU core clock", SensorKind.Frequency) with { Hardware = hardware, Label = "Core", Maximum = maximum });
        }
        if (api->DeviceGetClockInfo(gpu.Device, ClockMemory, &value) == Success)
        {
            gpu.HasMemoryClock = true;
            result.Add(SensorDescriptor.Create($"{p}.memclock", "GPU memory clock", SensorKind.Frequency) with { Hardware = hardware, Label = "Mem" });
        }
        // Passively cooled or water-blocked cards answer NOT_SUPPORTED.
        if (api->DeviceGetFanSpeed(gpu.Device, &value) == Success)
        {
            gpu.HasFan = true;
            result.Add(SensorDescriptor.Create($"{p}.fan", "GPU fan duty", SensorKind.Other, "%") with { Hardware = hardware, Label = "Fan" });
        }

        return result;
    }

    /// <summary>Loads NvAPI once and finds out how each GPU's hotspot can be read. Logs why when it can't.</summary>
    private List<SensorDescriptor> ProbeHotspots(List<Gpu> list)
    {
        var result = new List<SensorDescriptor>();
        if (nvApi is null)
        {
            nvApi = NvApi.TryLoad(out var reason);
            if (nvApi is null)
            {
                LogHotspotUnavailable(logger, "all GPUs", reason);
                return result;
            }
        }

        foreach (var gpu in list)
        {
            PciInfo pci;
            if (api->DeviceGetPciInfo == null || api->DeviceGetPciInfo(gpu.Device, &pci) != Success)
            {
                LogHotspotUnavailable(logger, gpu.Name, "NVML doesn't report its PCI bus");
                continue;
            }

            // Blackwell and anything newer NVML can't name read the register first; older GPUs the sensor array.
            uint architecture = 0;
            var prefersRegister = api->DeviceGetArchitecture != null
                && api->DeviceGetArchitecture(gpu.Device, &architecture) == Success
                && architecture >= ArchitectureBlackwell;
            if (nvApi.Prepare(pci.Bus, prefersRegister, out var detail))
            {
                gpu.PciBus = pci.Bus;
                LogHotspotSource(logger, gpu.Name, detail);
                var p = gpu.Index == 0 ? "gpu" : $"gpu{gpu.Index}";
                result.Add(SensorDescriptor.Create($"{p}.hotspot", "GPU hotspot temperature", SensorKind.Temperature) with
                {
                    Hardware = gpu.Name,
                    Label = "Hotspot",
                });
            }
            else
            {
                LogHotspotUnavailable(logger, gpu.Name, detail);
            }
        }
        return result;
    }

    private int ReadGpu(Gpu gpu, IDictionary<string, double> values)
    {
        var p = gpu.Index == 0 ? "gpu" : $"gpu{gpu.Index}";
        uint value;
        int status;

        if (gpu.HasLoad)
        {
            Utilization utilization;
            if ((status = api->DeviceGetUtilizationRates(gpu.Device, &utilization)) == Success)
                values[$"{p}.load"] = utilization.Gpu;
            else if (status == ErrorGpuIsLost)
                return status;
        }
        if (gpu.HasTemperature && api->DeviceGetTemperature(gpu.Device, TemperatureGpu, &value) == Success)
            values[$"{p}.temp"] = value;
        if (gpu.PciBus is { } bus && nvApi?.ReadHotspot(bus) is { } hotspot)
            values[$"{p}.hotspot"] = hotspot;
        if (gpu.HasPower && api->DeviceGetPowerUsage(gpu.Device, &value) == Success)
            values[$"{p}.power"] = value / 1000.0;
        if (gpu.HasMemory)
        {
            Memory memory;
            if (api->DeviceGetMemoryInfo(gpu.Device, &memory) == Success)
                values[gpu.Index == 0 ? "mem.vram" : $"{p}.vram"] = memory.Used / BytesPerGb;
        }
        if (gpu.HasCoreClock && api->DeviceGetClockInfo(gpu.Device, ClockGraphics, &value) == Success)
            values[$"{p}.clock"] = value;
        if (gpu.HasMemoryClock && api->DeviceGetClockInfo(gpu.Device, ClockMemory, &value) == Success)
            values[$"{p}.memclock"] = value;
        if (gpu.HasFan && api->DeviceGetFanSpeed(gpu.Device, &value) == Success)
            values[$"{p}.fan"] = value;
        return Success;
    }

    private const double BytesPerGb = 1024d * 1024 * 1024;

    private string ReadName(IntPtr device)
    {
        var buffer = stackalloc byte[(int)NameBufferSize];
        if (api->DeviceGetName(device, buffer, NameBufferSize) != Success)
            return "NVIDIA GPU";
        var span = new ReadOnlySpan<byte>(buffer, (int)NameBufferSize);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    private void Check(int status, string function)
    {
        if (status != Success)
            throw new InvalidOperationException($"{function} failed: {ErrorString(status)}");
    }

    private string ErrorString(int status)
    {
        if (api is null || api->ErrorString == null)
            return $"error {status}";
        var text = api->ErrorString(status);
        return text == null ? $"error {status}" : Marshal.PtrToStringUTF8((IntPtr)text) ?? $"error {status}";
    }

    private void Shutdown()
    {
        if (gpus is not null && api is not null)
            api->Shutdown();
        gpus = null;
        // A lost GPU usually means a driver reload; NvAPI starts over with NVML.
        nvApi?.Dispose();
        nvApi = null;
    }

    public void Dispose()
    {
        Shutdown();
        if (api is not null)
        {
            NativeMemory.Free(api);
            api = null;
        }
        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }
    }

    private static Api* BindApi(IntPtr library)
    {
        IntPtr Export(string name) => NativeLibrary.TryGetExport(library, name, out var address)
            ? address
            : throw new SensorSourceUnavailableException($"The NVML library has no {name}; the driver is too old");

        var bound = (Api*)NativeMemory.AllocZeroed((nuint)sizeof(Api));
        bound->Init = (delegate* unmanaged<int>)Export("nvmlInit_v2");
        bound->Shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
        bound->ErrorString = NativeLibrary.TryGetExport(library, "nvmlErrorString", out var errorString)
            ? (delegate* unmanaged<int, byte*>)errorString
            : null;
        bound->DeviceGetCount = (delegate* unmanaged<uint*, int>)Export("nvmlDeviceGetCount_v2");
        bound->DeviceGetHandleByIndex = (delegate* unmanaged<uint, IntPtr*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
        bound->DeviceGetName = (delegate* unmanaged<IntPtr, byte*, uint, int>)Export("nvmlDeviceGetName");
        bound->DeviceGetUtilizationRates = (delegate* unmanaged<IntPtr, Utilization*, int>)Export("nvmlDeviceGetUtilizationRates");
        bound->DeviceGetTemperature = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
        bound->DeviceGetPowerUsage = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage");
        bound->DeviceGetEnforcedPowerLimit = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetEnforcedPowerLimit");
        bound->DeviceGetMemoryInfo = (delegate* unmanaged<IntPtr, Memory*, int>)Export("nvmlDeviceGetMemoryInfo");
        bound->DeviceGetClockInfo = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
        bound->DeviceGetMaxClockInfo = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetMaxClockInfo");
        bound->DeviceGetFanSpeed = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetFanSpeed");
        // Only needed for the hotspot; older drivers without them just don't get one.
        bound->DeviceGetPciInfo = NativeLibrary.TryGetExport(library, "nvmlDeviceGetPciInfo_v3", out var pciInfo)
            ? (delegate* unmanaged<IntPtr, PciInfo*, int>)pciInfo
            : null;
        bound->DeviceGetArchitecture = NativeLibrary.TryGetExport(library, "nvmlDeviceGetArchitecture", out var architecture)
            ? (delegate* unmanaged<IntPtr, uint*, int>)architecture
            : null;
        return bound;
    }

    /// <summary>nvmlUtilization_t</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    /// <summary>nvmlMemory_t (bytes)</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>nvmlPciInfo_t</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        public fixed byte BusId[32];
    }

    private struct Api
    {
        public delegate* unmanaged<int> Init;
        public delegate* unmanaged<int> Shutdown;
        public delegate* unmanaged<int, byte*> ErrorString;
        public delegate* unmanaged<uint*, int> DeviceGetCount;
        public delegate* unmanaged<uint, IntPtr*, int> DeviceGetHandleByIndex;
        public delegate* unmanaged<IntPtr, byte*, uint, int> DeviceGetName;
        public delegate* unmanaged<IntPtr, Utilization*, int> DeviceGetUtilizationRates;
        public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetTemperature;
        public delegate* unmanaged<IntPtr, uint*, int> DeviceGetPowerUsage;
        public delegate* unmanaged<IntPtr, uint*, int> DeviceGetEnforcedPowerLimit;
        public delegate* unmanaged<IntPtr, Memory*, int> DeviceGetMemoryInfo;
        public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetClockInfo;
        public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetMaxClockInfo;
        public delegate* unmanaged<IntPtr, uint*, int> DeviceGetFanSpeed;
        public delegate* unmanaged<IntPtr, PciInfo*, int> DeviceGetPciInfo;
        public delegate* unmanaged<IntPtr, uint*, int> DeviceGetArchitecture;
    }

    private sealed class Gpu(int index, IntPtr device, string name)
    {
        public int Index { get; } = index;
        public IntPtr Device { get; } = device;
        public string Name { get; } = name;
        public bool HasLoad { get; set; }
        public bool HasTemperature { get; set; }
        public bool HasPower { get; set; }
        public bool HasMemory { get; set; }
        public bool HasCoreClock { get; set; }
        public bool HasMemoryClock { get; set; }
        public bool HasFan { get; set; }
        /// <summary>Set when NvAPI can read this GPU's hotspot.</summary>
        public uint? PciBus { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU hotspot temperature for {Gpu} comes from NvAPI ({Method})")]
    private static partial void LogHotspotSource(ILogger logger, string gpu, string method);

    [LoggerMessage(Level = LogLevel.Information, Message = "No GPU hotspot temperature for {Gpu}: {Reason}")]
    private static partial void LogHotspotUnavailable(ILogger logger, string gpu, string reason);
}
