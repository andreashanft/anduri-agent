using System.Globalization;

namespace Anduri.Agent.Sensors.Linux;

/// <summary><c>net.down</c>/<c>net.up</c> in bytes per second for the default-route interface.</summary>
public sealed class NetworkSource(HostPaths paths, TimeProvider time, string? configuredInterface = null) : ISensorSource
{
    private string? currentInterface;
    private (ulong Rx, ulong Tx, DateTimeOffset At)? previous;
    private IReadOnlyList<SensorDescriptor> descriptors = [];

    public string Name => "network";

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        var name = configuredInterface ?? FindDefaultInterface();
        if (name is null)
        {
            descriptors = [];
            previous = null;
            return ValueTask.CompletedTask;
        }

        if (name != currentInterface)
        {
            // A different interface has unrelated counters.
            currentInterface = name;
            previous = null;
            descriptors = CreateDescriptors(name, LinkDetail(name));
        }

        var statistics = paths.Sys("class", "net", name, "statistics");
        var rx = SysFs.TryReadULong(Path.Combine(statistics, "rx_bytes"));
        var tx = SysFs.TryReadULong(Path.Combine(statistics, "tx_bytes"));
        var now = time.GetUtcNow();
        if (rx is null || tx is null)
        {
            previous = null;
            return ValueTask.CompletedTask;
        }

        if (previous is { } last && now > last.At)
        {
            var seconds = (now - last.At).TotalSeconds;
            // Counters reset when a driver reloads; skip that interval rather than report a huge negative jump.
            if (rx >= last.Rx && tx >= last.Tx)
            {
                values["net.down"] = (rx.Value - last.Rx) / seconds;
                values["net.up"] = (tx.Value - last.Tx) / seconds;
            }
        }

        previous = (rx.Value, tx.Value, now);
        return ValueTask.CompletedTask;
    }

    /// <summary>The interface of the IPv4 default route with the lowest metric, from <c>/proc/net/route</c>.</summary>
    internal string? FindDefaultInterface()
    {
        if (SysFs.TryReadText(paths.Proc("net", "route")) is { } routes && ParseDefaultRoute(routes) is { } name)
            return name;

        // IPv6-only hosts: fall back to the first interface that is up and isn't loopback.
        var root = paths.Sys("class", "net");
        if (!Directory.Exists(root))
            return null;
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(n => n != "lo" && SysFs.TryReadText(Path.Combine(root, n, "operstate")) == "up");
    }

    internal static string? ParseDefaultRoute(string text)
    {
        string? best = null;
        var bestMetric = long.MaxValue;
        foreach (var line in text.Split('\n').Skip(1))
        {
            // Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT
            var columns = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 8 || columns[1] != "00000000" || columns[7] != "00000000")
                continue;
            if (!int.TryParse(columns[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) || (flags & 0x1) == 0)
                continue;
            var metric = long.TryParse(columns[6], CultureInfo.InvariantCulture, out var m) ? m : long.MaxValue - 1;
            if (metric < bestMetric)
            {
                best = columns[0];
                bestMetric = metric;
            }
        }

        return best;
    }

    internal string? LinkDetail(string name)
    {
        var directory = paths.Sys("class", "net", name);
        // Reading "speed" fails with EINVAL for Wi-Fi and for links that are down.
        if (SysFs.TryReadLong(Path.Combine(directory, "speed")) is { } mbps and > 0)
            return FormatSpeed(mbps);
        return Directory.Exists(Path.Combine(directory, "wireless")) ? "Wi-Fi" : null;
    }

    /// <summary>1000 → "1 GbE", 2500 → "2.5 GbE", 100 → "100 Mb/s".</summary>
    internal static string FormatSpeed(long mbps) => mbps >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{mbps / 1000.0:0.##} GbE")
        : string.Create(CultureInfo.InvariantCulture, $"{mbps} Mb/s");

    private static IReadOnlyList<SensorDescriptor> CreateDescriptors(string interfaceName, string? detail) =>
    [
        SensorDescriptor.Create("net.down", "Download", SensorKind.Network) with { Hardware = interfaceName, Label = "Down", Detail = detail },
        SensorDescriptor.Create("net.up", "Upload", SensorKind.Network) with { Hardware = interfaceName, Label = "Up", Detail = detail },
    ];
}
