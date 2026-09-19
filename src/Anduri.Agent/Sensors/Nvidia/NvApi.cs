using System.Runtime.InteropServices;
using System.Text;

namespace Anduri.Agent.Sensors.Nvidia;

/// <summary>
/// The GPU hotspot (junction) temperature through NvAPI, which NVML doesn't report. NvAPI ships with the proprietary
/// driver as <c>libnvidia-api.so.1</c>.
/// </summary>
/// <remarks>
/// NvAPI is undocumented on Linux: the interface ids and struct layouts come from LACT's reverse engineering
/// (github.com/ilya-zlobintsev/LACT, MIT), which CoolerControl 5 also uses. Up to Ada the hotspot is slot 9 of the
/// thermal sensor array. Blackwell (RTX 50) stopped filling that slot, so there it's read from the aggregated hotspot
/// register instead, falling back to the array if the register can't be read. Both hold Q8.8 fixed-point °C.
/// </remarks>
internal sealed unsafe class NvApi : IDisposable
{
    private const uint QueryInitialize = 0x0150E828;
    private const uint QueryUnload = 0xD22BDD7E;
    private const uint QueryEnumPhysicalGpus = 0xE5AC921F;
    private const uint QueryGetBusId = 0x1BE0B8E5;
    private const uint QueryGetThermals = 0x65FE3AAD;
    private const uint QueryRegisterOp = 0x2EB3C140;
    private const uint QueryGetErrorMessage = 0x6C2D048C;

    private const int MaxPhysicalGpus = 64;
    private const int ShortStringMax = 64;
    internal const int ThermalValueCount = 40;
    internal const int HotspotIndex = 9;

    internal const uint HotspotRegister = 0x00AD_0AA0;
    internal const int RegisterOpCount = 256;
    /// <summary>Read | 32 bit | global.</summary>
    internal const ushort RegisterReadFlags = 1 | 4 | 16;

    private static readonly string[] LibraryNames = ["libnvidia-api.so.1", "libnvidia-api.so"];

    private IntPtr library;
    private readonly delegate* unmanaged<uint, void*> queryInterface;
    private readonly delegate* unmanaged<IntPtr, Thermals*, int> getThermals;
    private readonly delegate* unmanaged<IntPtr, byte*, int> registerOp;
    private readonly Dictionary<uint, Gpu> gpus = [];

    private NvApi(IntPtr library, delegate* unmanaged<uint, void*> queryInterface)
    {
        this.library = library;
        this.queryInterface = queryInterface;
        getThermals = (delegate* unmanaged<IntPtr, Thermals*, int>)queryInterface(QueryGetThermals);
        registerOp = (delegate* unmanaged<IntPtr, byte*, int>)queryInterface(QueryRegisterOp);
    }

    /// <summary>Loads and initializes NvAPI, or returns null with the reason.</summary>
    public static NvApi? TryLoad(out string reason)
    {
        IntPtr handle = IntPtr.Zero;
        foreach (var name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, out handle))
                break;
        }
        if (handle == IntPtr.Zero)
        {
            reason = "libnvidia-api.so.1 wasn't found (it comes with NVIDIA's proprietary driver)";
            return null;
        }
        if (!NativeLibrary.TryGetExport(handle, "nvapi_QueryInterface", out var query))
        {
            NativeLibrary.Free(handle);
            reason = "libnvidia-api.so.1 has no nvapi_QueryInterface";
            return null;
        }

        var api = new NvApi(handle, (delegate* unmanaged<uint, void*>)query);
        var initialize = (delegate* unmanaged<int>)api.queryInterface(QueryInitialize);
        var status = initialize == null ? -1 : initialize();
        if (status != 0)
        {
            reason = initialize == null ? "NvAPI has no Initialize" : $"NvAPI Initialize failed: {api.ErrorMessage(status)}";
            api.Dispose();
            return null;
        }
        if (!api.EnumerateGpus(out reason))
        {
            api.Dispose();
            return null;
        }

        reason = "";
        return api;
    }

    /// <summary>
    /// Works out how to read the hotspot of the GPU on PCI bus <paramref name="bus"/>. Returns false with the reason
    /// if it can't be read at all.
    /// </summary>
    public bool Prepare(uint bus, bool prefersRegister, out string detail)
    {
        if (!gpus.TryGetValue(bus, out var gpu))
        {
            detail = $"NvAPI doesn't list a GPU on PCI bus {bus}";
            return false;
        }

        if (prefersRegister && registerOp != null)
        {
            gpu.Request = (byte*)NativeMemory.AllocZeroed((nuint)RegisterRequestSize);
            WriteRegisterRequest(gpu.Request, HotspotRegister);
            if (ReadRegister(gpu, out var registerStatus) is not null)
            {
                gpu.UsesRegister = true;
                detail = "hotspot register";
                return true;
            }
            NativeMemory.Free(gpu.Request);
            gpu.Request = null;
            detail = $"hotspot register unreadable ({ErrorMessage(registerStatus)}), ";
        }
        else
        {
            detail = "";
        }

        if (getThermals == null)
        {
            detail += "NvAPI has no thermal sensor call";
            return false;
        }
        if (ThermalMask(gpu.Handle, out var status) is not { } mask)
        {
            detail += $"thermal sensors unavailable ({ErrorMessage(status)})";
            return false;
        }
        gpu.Mask = mask;
        if (ReadThermals(gpu, out status) is null)
        {
            detail += status == 0 ? "no hotspot in the thermal sensors" : $"thermal sensors unreadable ({ErrorMessage(status)})";
            return false;
        }
        detail += "thermal sensors";
        return true;
    }

    /// <summary>Hotspot temperature in °C, or null if this read failed or the GPU wasn't prepared.</summary>
    public double? ReadHotspot(uint bus)
    {
        if (!gpus.TryGetValue(bus, out var gpu))
            return null;
        return gpu.UsesRegister ? ReadRegister(gpu, out _) : gpu.Mask is null ? null : ReadThermals(gpu, out _);
    }

    private bool EnumerateGpus(out string reason)
    {
        var enumerate = (delegate* unmanaged<IntPtr*, uint*, int>)queryInterface(QueryEnumPhysicalGpus);
        var getBusId = (delegate* unmanaged<IntPtr, uint*, int>)queryInterface(QueryGetBusId);
        if (enumerate == null || getBusId == null)
        {
            reason = "NvAPI has no EnumPhysicalGPUs/GetBusId";
            return false;
        }

        var handles = stackalloc IntPtr[MaxPhysicalGpus];
        uint count;
        var status = enumerate(handles, &count);
        if (status != 0)
        {
            reason = $"NvAPI EnumPhysicalGPUs failed: {ErrorMessage(status)}";
            return false;
        }

        for (var index = 0; index < Math.Min((int)count, MaxPhysicalGpus); index++)
        {
            uint bus;
            if (getBusId(handles[index], &bus) == 0)
                gpus[bus] = new Gpu(handles[index]);
        }
        reason = gpus.Count == 0 ? "NvAPI found no GPUs" : "";
        return gpus.Count > 0;
    }

    /// <summary>
    /// Asking for sensors a GPU doesn't have fails, so probe one bit at a time; the mask is every bit below the first
    /// that fails.
    /// </summary>
    private int? ThermalMask(IntPtr handle, out int status)
    {
        var thermals = new Thermals(1);
        status = getThermals(handle, &thermals);
        if (status != 0)
            return null;
        for (var bit = 1; bit < 31; bit++)
        {
            thermals = new Thermals(1 << bit);
            if (getThermals(handle, &thermals) != 0)
                return (1 << bit) - 1;
        }
        return int.MaxValue;
    }

    private double? ReadThermals(Gpu gpu, out int status)
    {
        var thermals = new Thermals(gpu.Mask ?? 0);
        status = getThermals(gpu.Handle, &thermals);
        return status == 0 ? Celsius(thermals.Values[HotspotIndex]) : null;
    }

    private double? ReadRegister(Gpu gpu, out int status)
    {
        status = registerOp(gpu.Handle, gpu.Request);
        if (status != 0)
            return null;
        var op = (RegisterOp*)(gpu.Request + RegisterHeaderSize);
        return Celsius((int)(op->Value & 0xFFFF));
    }

    private string ErrorMessage(int status)
    {
        var getMessage = (delegate* unmanaged<int, byte*, int>)queryInterface(QueryGetErrorMessage);
        if (getMessage == null)
            return $"status {status}";
        var text = stackalloc byte[ShortStringMax];
        if (getMessage(status, text) != 0)
            return $"status {status}";
        var span = new ReadOnlySpan<byte>(text, ShortStringMax);
        var end = span.IndexOf((byte)0);
        return $"{Encoding.UTF8.GetString(end < 0 ? span : span[..end])} ({status})";
    }

    /// <summary>Q8.8 fixed point to whole °C. Zero (no sensor) and 255 (saturated) aren't temperatures.</summary>
    internal static double? Celsius(int raw)
    {
        var celsius = raw / 256;
        return celsius is > 0 and < 255 ? celsius : null;
    }

    /// <summary>NvAPI checks a request's layout by the size and revision packed into its version field.</summary>
    internal static uint Version(int size, int revision) => (uint)size | ((uint)revision << 16);

    internal const int RegisterHeaderSize = 8;
    internal static readonly int RegisterRequestSize = RegisterHeaderSize + RegisterOpCount * sizeof(RegisterOp);

    /// <summary>A batch of 256 register ops holding a single 32-bit read of <paramref name="offset"/>.</summary>
    internal static void WriteRegisterRequest(byte* request, uint offset)
    {
        new Span<byte>(request, RegisterRequestSize).Clear();
        *(uint*)request = Version(RegisterRequestSize, 1);
        *(uint*)(request + 4) = 1;
        var op = (RegisterOp*)(request + RegisterHeaderSize);
        op->Flags = RegisterReadFlags;
        op->Offset = offset;
    }

    public void Dispose()
    {
        foreach (var gpu in gpus.Values)
        {
            if (gpu.Request != null)
                NativeMemory.Free(gpu.Request);
            gpu.Request = null;
        }
        gpus.Clear();
        if (library == IntPtr.Zero)
            return;
        var unload = (delegate* unmanaged<int>)queryInterface(QueryUnload);
        if (unload != null)
            unload();
        NativeLibrary.Free(library);
        library = IntPtr.Zero;
    }

    /// <summary>NV_GPU_THERMAL_SENSORS (revision 2): version, sensor mask, 40 readings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Thermals
    {
        public uint Version;
        public int Mask;
        public fixed int Values[ThermalValueCount];

        public Thermals(int mask)
        {
            Version = NvApi.Version(sizeof(Thermals), 2);
            Mask = mask;
        }
    }

    /// <summary>One entry of a register-op batch.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisterOp
    {
        public ushort Flags;
        public ushort Status;
        public uint Offset;
        public ulong WriteMask;
        public ulong Value;
    }

    private sealed class Gpu(IntPtr handle)
    {
        public IntPtr Handle { get; } = handle;
        public int? Mask { get; set; }
        public bool UsesRegister { get; set; }
        public byte* Request { get; set; }
    }
}
