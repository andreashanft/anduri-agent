using System.Text.RegularExpressions;

namespace Anduri.Agent.Sensors.Linux;

public enum RaplStatus
{
    /// <summary>A power value was computed.</summary>
    Ok,
    /// <summary>Counters were read, but a second sample is needed for a rate.</summary>
    Warming,
    /// <summary>No package zones in <c>/sys/class/powercap</c>.</summary>
    Unavailable,
    /// <summary>
    /// <c>energy_uj</c> exists but is root-only, as on every kernel since the 2020 PLATYPUS side-channel fix.
    /// </summary>
    PermissionDenied,
}

/// <summary>
/// CPU package power from the powercap RAPL energy counters (Intel, and AMD Zen via the same driver).
/// Power is the energy delta over time, summed over all packages.
/// </summary>
public sealed partial class RaplReader(HostPaths paths)
{
    private static readonly TimeSpan PermissionRetry = TimeSpan.FromMinutes(10);

    private List<Zone>? zones;
    private DateTimeOffset retryAt = DateTimeOffset.MinValue;
    private RaplStatus lastStatus = RaplStatus.Unavailable;

    public RaplStatus Read(DateTimeOffset now, out double? watts)
    {
        watts = null;
        if (zones is null)
        {
            if (now < retryAt)
                return lastStatus;
            zones = FindZones();
            if (zones.Count == 0)
            {
                zones = null;
                retryAt = now + PermissionRetry;
                return lastStatus = RaplStatus.Unavailable;
            }
        }

        double total = 0;
        var complete = true;
        foreach (var zone in zones)
        {
            ulong energy;
            try
            {
                var text = File.ReadAllText(zone.EnergyPath).Trim();
                if (!ulong.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out energy))
                {
                    complete = false;
                    continue;
                }
            }
            catch (UnauthorizedAccessException)
            {
                zones = null;
                retryAt = now + PermissionRetry;
                return lastStatus = RaplStatus.PermissionDenied;
            }
            catch (IOException)
            {
                complete = false;
                continue;
            }

            if (zone.LastEnergy is { } previousEnergy && zone.LastTime is { } previousTime && now > previousTime)
            {
                var joules = EnergyDelta(previousEnergy, energy, zone.MaxRange) / 1_000_000.0;
                total += joules / (now - previousTime).TotalSeconds;
            }
            else
            {
                complete = false;
            }

            zone.LastEnergy = energy;
            zone.LastTime = now;
        }

        if (!complete)
            return lastStatus = RaplStatus.Warming;
        watts = total;
        return lastStatus = RaplStatus.Ok;
    }

    /// <summary>Microjoules consumed between two counter readings; the counter wraps at <paramref name="maxRange"/>.</summary>
    internal static ulong EnergyDelta(ulong previous, ulong current, ulong maxRange) =>
        current >= previous ? current - previous : maxRange - previous + current;

    private List<Zone> FindZones()
    {
        var root = paths.Sys("class", "powercap");
        if (!Directory.Exists(root))
            return [];

        var result = new List<Zone>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            // Top-level zones only ("intel-rapl:0"); "intel-rapl:0:0" are sub-zones (core, uncore, dram) of a package.
            if (!PackageZone().IsMatch(Path.GetFileName(directory)))
                continue;
            if (SysFs.TryReadText(Path.Combine(directory, "name")) is not { } name || !name.StartsWith("package", StringComparison.Ordinal))
                continue;

            var energy = Path.Combine(directory, "energy_uj");
            if (!File.Exists(energy))
                continue;
            var maxRange = SysFs.TryReadULong(Path.Combine(directory, "max_energy_range_uj")) ?? ulong.MaxValue;
            result.Add(new Zone(energy, maxRange));
        }

        return result;
    }

    [GeneratedRegex(@"^intel-rapl:\d+$")]
    private static partial Regex PackageZone();

    private sealed class Zone(string energyPath, ulong maxRange)
    {
        public string EnergyPath { get; } = energyPath;
        public ulong MaxRange { get; } = maxRange;
        public ulong? LastEnergy { get; set; }
        public DateTimeOffset? LastTime { get; set; }
    }
}
