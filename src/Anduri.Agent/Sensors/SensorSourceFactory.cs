using Anduri.Agent.Configuration;
using Anduri.Agent.Sensors.CoolerControl;
using Anduri.Agent.Sensors.Linux;
using Anduri.Agent.Sensors.Nvidia;
using Anduri.Agent.Sensors.Simulated;

namespace Anduri.Agent.Sensors;

/// <summary>The sources the sampler polls, in priority order (earlier sources win when two report the same id).</summary>
public sealed record SensorSources(IReadOnlyList<ISensorSource> Sources);

public static partial class SensorSourceFactory
{
    public static SensorSources Create(AgentConfig config, bool simulate, TimeProvider time, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("Anduri.Agent.Sensors");
        if (simulate)
            return new SensorSources([new SimulatedRigSource(time)]);

        if (!OperatingSystem.IsLinux())
        {
            // A LibreHardwareMonitor source for Windows would plug in here.
            LogNoPlatformSources(logger, AgentIdentityOs());
            return new SensorSources([new SimulatedRigSource(time)]);
        }

        var paths = string.IsNullOrWhiteSpace(config.HostRoot) ? HostPaths.Host : new HostPaths(config.HostRoot);
        var enabled = config.Sources;
        var sources = new List<ISensorSource>();

        if (enabled.Cpu)
            sources.Add(new CpuSource(paths, time, loggers.CreateLogger<CpuSource>()));
        if (enabled.Memory)
            sources.Add(new MemorySource(paths));
        if (enabled.Nvidia)
            sources.Add(new NvmlSource(enabled.NvApi, loggers.CreateLogger<NvmlSource>()));
        // Aquacomputer before the generic hwmon and CoolerControl sources, so it claims the loop.* ids.
        if (enabled.Aquacomputer)
            sources.Add(new AquacomputerSource(paths, time));
        if (enabled.Hwmon)
            sources.Add(new HwmonSource(paths, time));

        CoolerControlSource? coolerControl = null;
        if (enabled.CoolerControl)
        {
            coolerControl = new CoolerControlSource(config.CoolerControl, time, loggers.CreateLogger<CoolerControlSource>());
            sources.Add(coolerControl);
        }
        if (enabled.Liquidctl)
            sources.Add(new LiquidctlSource(config.Liquidctl, coolerControl));
        if (enabled.Drives)
            sources.Add(new DriveSource(paths, config.Drives, time));
        if (enabled.Network)
            sources.Add(new NetworkSource(paths, time, config.Network.Interface));

        return new SensorSources(sources);
    }

    private static string AgentIdentityOs() => State.AgentIdentity.CurrentOs;

    [LoggerMessage(Level = LogLevel.Warning, Message = "There are no hardware sensor sources for {Os} yet; streaming the simulated rig instead")]
    private static partial void LogNoPlatformSources(ILogger logger, string os);
}
