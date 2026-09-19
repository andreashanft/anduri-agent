namespace Anduri.Agent.Sensors.Linux;

/// <summary>A hwmon channel mapped to a sensor.</summary>
/// <param name="Convert">Raw sysfs integer to the sensor's unit; <c>null</c> marks an invalid reading.</param>
/// <param name="IgnoreZeroUntilSeen">
/// Leave the sensor out until it reads non-zero once. Unconnected fan headers read 0 rpm; a connected fan that
/// stops later (zero-rpm mode) stays in the catalog.
/// </param>
public sealed record MappedChannel(HwmonChannel Channel, SensorDescriptor Descriptor, Func<long, double?> Convert, bool IgnoreZeroUntilSeen = false);

/// <summary>
/// Shared scanning for hwmon-based sources. Channels join the catalog once they read successfully and stay while
/// their chip exists, so a sensor that briefly returns ENODATA doesn't make the catalog flap.
/// </summary>
public abstract class HwmonSourceBase(HostPaths paths, TimeProvider time) : ISensorSource
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private IReadOnlyList<MappedChannel> mapped = [];
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private DateTimeOffset nextScan = DateTimeOffset.MinValue;
    private IReadOnlyList<SensorDescriptor> descriptors = [];

    public abstract string Name { get; }

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    protected abstract IReadOnlyList<MappedChannel> Map(IReadOnlyList<HwmonChip> chips);

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rescanned = false;
        if (now >= nextScan)
        {
            nextScan = now + RescanInterval;
            mapped = Map(HwmonScanner.Scan(paths));
            active.IntersectWith(mapped.Select(m => m.Descriptor.Id));
            rescanned = true;
        }

        var activeBefore = active.Count;
        foreach (var channel in mapped)
        {
            if (channel.Channel.ReadRaw() is not { } raw || channel.Convert(raw) is not { } value || !double.IsFinite(value))
                continue;

            var id = channel.Descriptor.Id;
            if (!active.Contains(id))
            {
                if (channel.IgnoreZeroUntilSeen && value == 0)
                    continue;
                active.Add(id);
            }

            values[id] = value;
        }

        if (rescanned || active.Count != activeBefore)
            descriptors = mapped.Where(m => active.Contains(m.Descriptor.Id)).Select(m => m.Descriptor).ToList();
        return ValueTask.CompletedTask;
    }

    protected static double? MilliToUnit(long raw) => raw / 1000.0;
}
