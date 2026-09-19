using System.Globalization;

namespace Anduri.Agent.Sensors.Linux;

/// <summary><c>mem.ram</c> from <c>/proc/meminfo</c>: used = MemTotal − MemAvailable.</summary>
public sealed class MemorySource(HostPaths paths) : ISensorSource
{
    // "GB" in the protocol is 2^30 bytes, matching what the app's sample shows for a 32 GB machine (31.2).
    internal const double BytesPerGb = 1024d * 1024 * 1024;

    private IReadOnlyList<SensorDescriptor> descriptors = [];

    public string Name => "memory";

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        var text = SysFs.TryReadText(paths.Proc("meminfo")) ?? throw new SensorSourceUnavailableException("/proc/meminfo isn't readable");
        if (ParseMeminfo(text) is not { } memory)
            throw new InvalidDataException("/proc/meminfo has no MemTotal/MemAvailable");

        values["mem.ram"] = memory.UsedGb;
        var capacity = Math.Round(memory.TotalGb, 1);
        if (descriptors.Count == 0 || descriptors[0].Capacity != capacity)
        {
            descriptors =
            [
                SensorDescriptor.Create("mem.ram", "System memory used", SensorKind.Memory) with { Label = "System", Capacity = capacity },
            ];
        }

        return ValueTask.CompletedTask;
    }

    internal readonly record struct MemoryUsage(double TotalGb, double UsedGb);

    internal static MemoryUsage? ParseMeminfo(string text)
    {
        long? total = null, available = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = ParseKb(line);
        }

        if (total is not { } t || available is not { } a)
            return null;
        return new MemoryUsage(t * 1024 / BytesPerGb, (t - a) * 1024 / BytesPerGb);
    }

    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }
}
