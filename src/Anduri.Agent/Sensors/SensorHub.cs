namespace Anduri.Agent.Sensors;

/// <summary>Values read in one sampler tick.</summary>
public sealed record SensorFrame(DateTimeOffset Timestamp, IReadOnlyDictionary<string, double> Values)
{
    /// <summary>Unix time in seconds with millisecond precision, for the snapshot's <c>t</c>.</summary>
    public double UnixSeconds => Math.Round(Timestamp.ToUnixTimeMilliseconds() / 1000.0, 3);
}

/// <summary>The latest catalog, values and history, shared by the sampler, the sessions and discovery.</summary>
public sealed class SensorHub
{
    private volatile IReadOnlyList<SensorDescriptor> catalog = [];
    private volatile SensorFrame? latest;
    private readonly TaskCompletionSource firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<SensorDescriptor> Catalog => catalog;
    public SensorFrame? Latest => latest;
    public HistoryBuffer History { get; } = new();

    /// <summary>Completes after the first sampler tick, so the first <c>catalog</c> isn't empty.</summary>
    public Task FirstSample => firstSample.Task;

    /// <summary>Raised after <see cref="Catalog"/> changed. Handlers must not block.</summary>
    public event Action<IReadOnlyList<SensorDescriptor>>? CatalogChanged;

    public void Publish(IReadOnlyList<SensorDescriptor> newCatalog, SensorFrame frame)
    {
        var changed = !catalog.SequenceEqual(newCatalog);
        // Catalog before event: a session that reads Catalog while becoming ready must see the new list.
        if (changed)
            catalog = newCatalog;
        latest = frame;
        History.Record(frame.Timestamp, frame.Values);
        firstSample.TrySetResult();

        if (changed)
            CatalogChanged?.Invoke(newCatalog);
    }
}
