namespace Anduri.Agent.Sensors;

/// <summary>
/// A group of sensors read the same way (procfs, one hwmon driver, NVML, a daemon API …).
/// </summary>
/// <remarks>
/// The sampler calls <see cref="ReadAsync"/> and then <see cref="Describe"/>, never concurrently for the same source.
/// Sources may implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
/// </remarks>
public interface ISensorSource
{
    /// <summary>Short name for logs, e.g. "nvidia".</summary>
    string Name { get; }

    /// <summary>Minimum time between reads. Slow sources (external processes) use several seconds.</summary>
    TimeSpan PollInterval => TimeSpan.Zero;

    /// <summary>
    /// The sensors this source currently provides. Called after every successful read, so a source can add or
    /// remove sensors (a drive is mounted, a USB controller is plugged in) by returning a different list.
    /// </summary>
    IReadOnlyList<SensorDescriptor> Describe();

    /// <summary>
    /// Reads current values, keyed by sensor id. Sensors that can't be read right now are left out.
    /// Throws when the source as a whole doesn't work; the sampler logs that once and retries later.
    /// </summary>
    ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken);
}

/// <summary>A source that can pre-fill the history buffer, so charts aren't empty right after start.</summary>
public interface IHistoryBackfill
{
    IEnumerable<(DateTimeOffset Timestamp, IReadOnlyDictionary<string, double> Values)> Backfill(DateTimeOffset now, TimeSpan duration);
}

/// <summary>Thrown when a source's hardware, driver or daemon isn't present. Logged at information level, not as a failure.</summary>
public sealed class SensorSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
