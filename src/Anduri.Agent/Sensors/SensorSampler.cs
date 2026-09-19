using Anduri.Agent.Configuration;

namespace Anduri.Agent.Sensors;

/// <summary>
/// Polls every source, merges their sensors into one catalog and publishes values and history to the <see cref="SensorHub"/>.
/// </summary>
public sealed partial class SensorSampler : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    // Fast sources (procfs, sysfs, NVML) finish well within this, so their values are from the current tick.
    // Slow ones keep reading in the background and contribute their last values.
    private static readonly TimeSpan TickReadBudget = TimeSpan.FromMilliseconds(250);

    private readonly SensorHub hub;
    private readonly IReadOnlyList<SourceRunner> runners;
    private readonly IReadOnlyDictionary<string, SensorOverride> overrides;
    private readonly TimeProvider time;
    private readonly ILogger<SensorSampler> logger;
    private readonly HashSet<string> reportedDuplicates = new(StringComparer.Ordinal);
    // Reads outlive a single tick, so they are cancelled with the sampler rather than with the tick that started them.
    private readonly CancellationTokenSource readCancellation = new();

    public SensorSampler(
        SensorHub hub,
        IEnumerable<ISensorSource> sources,
        AgentConfig config,
        TimeProvider time,
        ILogger<SensorSampler> logger)
    {
        this.hub = hub;
        this.time = time;
        this.logger = logger;
        overrides = config.Sensors;
        runners = sources.Select(source => new SourceRunner(source, time, logger)).ToList();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        LogSources(logger, string.Join(", ", runners.Select(r => r.Source.Name)));
        Backfill();

        // Sample once before the server starts accepting connections, so the first catalog has sensors in it.
        // A source slower than the limit shows up in a later tick.
        await SampleAsync(TimeSpan.FromSeconds(5), cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SampleAsync(TickReadBudget, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await readCancellation.CancelAsync();
        foreach (var runner in runners)
            await runner.DisposeAsync();
    }

    public override void Dispose()
    {
        base.Dispose();
        readCancellation.Dispose();
    }

    /// <summary>One tick: start due reads, wait for them up to <paramref name="waitLimit"/>, merge whatever is current.</summary>
    internal async Task SampleAsync(TimeSpan waitLimit, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var reads = runners.Select(r => r.StartReadIfDue(now, readCancellation.Token)).Where(t => !t.IsCompleted).ToList();
        if (reads.Count > 0)
            await Task.WhenAny(Task.WhenAll(reads), Task.Delay(waitLimit, time, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        var (catalog, values) = Merge(time.GetUtcNow());
        hub.Publish(catalog, new SensorFrame(now, values));
    }

    private (IReadOnlyList<SensorDescriptor> Catalog, IReadOnlyDictionary<string, double> Values) Merge(DateTimeOffset now)
    {
        var catalog = new List<SensorDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);

        var current = runners.Select(r => (r.Source.Name, State: r.Current(now))).ToList();

        // An explicit rename in the config beats a source's automatic claim of the same id.
        var renamedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, (descriptors, _)) in current)
        {
            foreach (var descriptor in descriptors)
            {
                if (overrides.TryGetValue(descriptor.Id, out var o) && !o.Hidden && !string.IsNullOrWhiteSpace(o.Id))
                    renamedTargets.Add(o.Id);
            }
        }

        // Otherwise earlier sources win on duplicate ids: a direct hwmon reading of the coolant beats CoolerControl's copy.
        foreach (var (sourceName, (descriptors, sourceValues)) in current)
        {
            foreach (var descriptor in descriptors)
            {
                var effective = ApplyOverride(descriptor);
                if (effective is null)
                    continue;

                var renamed = !ReferenceEquals(effective, descriptor) && effective.Id != descriptor.Id;
                if ((!renamed && renamedTargets.Contains(effective.Id)) || !ids.Add(effective.Id))
                {
                    if (reportedDuplicates.Add($"{sourceName}/{descriptor.Id}"))
                        LogDuplicate(logger, effective.Id, sourceName);
                    continue;
                }

                catalog.Add(effective);
                if (sourceValues.TryGetValue(descriptor.Id, out var value) && double.IsFinite(value))
                    values[effective.Id] = Round(value);
            }
        }

        return (catalog, values);
    }

    private SensorDescriptor? ApplyOverride(SensorDescriptor descriptor)
    {
        if (!overrides.TryGetValue(descriptor.Id, out var o))
            return descriptor;
        if (o.Hidden)
            return null;
        return descriptor with
        {
            Id = string.IsNullOrWhiteSpace(o.Id) ? descriptor.Id : o.Id,
            Name = o.Name ?? descriptor.Name,
            Label = o.Label ?? descriptor.Label,
            ShortLabel = o.ShortLabel ?? descriptor.ShortLabel,
        };
    }

    // Two decimals are plenty for every kind and keep snapshots and the 15-minute history compact.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private void Backfill()
    {
        var now = time.GetUtcNow();
        foreach (var runner in runners)
        {
            if (runner.Source is not IHistoryBackfill backfill)
                continue;
            foreach (var (timestamp, values) in backfill.Backfill(now, TimeSpan.FromSeconds(hub.History.CapacitySeconds)))
            {
                var renamed = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (id, value) in values)
                {
                    if (!overrides.TryGetValue(id, out var o))
                        renamed[id] = Round(value);
                    else if (!o.Hidden)
                        renamed[string.IsNullOrWhiteSpace(o.Id) ? id : o.Id] = Round(value);
                }
                hub.History.Record(timestamp, renamed);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sensor sources: {Sources}")]
    private static partial void LogSources(ILogger logger, string sources);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sensor {Id} from {Source} is already provided by an earlier source; ignoring it")]
    private static partial void LogDuplicate(ILogger logger, string id, string source);

    /// <summary>Runs one source: scheduling, failure logging with backoff, and its last good result.</summary>
    private sealed partial class SourceRunner(ISensorSource source, TimeProvider time, ILogger logger) : IAsyncDisposable
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan UnavailableRetryDelay = TimeSpan.FromMinutes(2);

        private readonly Lock gate = new();
        private Task? inFlight;
        private DateTimeOffset nextRead = DateTimeOffset.MinValue;
        private DateTimeOffset lastSuccess = DateTimeOffset.MinValue;
        private IReadOnlyList<SensorDescriptor> descriptors = [];
        private IReadOnlyDictionary<string, double> values = new Dictionary<string, double>();
        private bool failing;

        public ISensorSource Source => source;

        public Task StartReadIfDue(DateTimeOffset now, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (inFlight is not null)
                    return inFlight;
                if (now < nextRead)
                    return Task.CompletedTask;
                inFlight = Task.Run(() => ReadAsync(cancellationToken), CancellationToken.None);
                return inFlight;
            }
        }

        /// <summary>The last descriptors (kept while failing so the catalog doesn't flap) and values, if still fresh.</summary>
        public (IReadOnlyList<SensorDescriptor>, IReadOnlyDictionary<string, double>) Current(DateTimeOffset now)
        {
            lock (gate)
            {
                var maxAge = TimeSpan.FromSeconds(5) + 3 * source.PollInterval;
                var fresh = !failing && now - lastSuccess <= maxAge;
                return (descriptors, fresh ? values : EmptyValues);
            }
        }

        private static readonly IReadOnlyDictionary<string, double> EmptyValues = new Dictionary<string, double>();

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            var started = time.GetUtcNow();
            var nextDelay = source.PollInterval;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReadTimeout);
                var read = new Dictionary<string, double>(StringComparer.Ordinal);
                await source.ReadAsync(read, timeout.Token);
                var described = source.Describe();

                bool recovered;
                lock (gate)
                {
                    descriptors = described;
                    values = read;
                    lastSuccess = time.GetUtcNow();
                    recovered = failing;
                    failing = false;
                }

                if (recovered)
                    LogRecovered(logger, source.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                bool first;
                lock (gate)
                {
                    first = !failing;
                    failing = true;
                }

                if (ex is SensorSourceUnavailableException)
                {
                    nextDelay = UnavailableRetryDelay;
                    if (first)
                        LogUnavailable(logger, source.Name, ex.Message);
                }
                else
                {
                    nextDelay = RetryDelay;
                    if (first)
                        LogFailed(logger, ex, source.Name, RetryDelay.TotalSeconds);
                }
            }
            finally
            {
                lock (gate)
                {
                    nextRead = started + nextDelay;
                    inFlight = null;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? pending;
            lock (gate)
                pending = inFlight;
            if (pending is not null)
                await pending.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default);

            switch (source)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Sensor source {Source} isn't available: {Reason}")]
        private static partial void LogUnavailable(ILogger logger, string source, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sensor source {Source} failed; its sensors are left out and it is retried every {Seconds} s")]
        private static partial void LogFailed(ILogger logger, Exception exception, string source, double seconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sensor source {Source} works again")]
        private static partial void LogRecovered(ILogger logger, string source);
    }
}
