using Anduri.Agent.Sensors;
using Anduri.Agent.Server;
using Anduri.Agent.State;

namespace Anduri.Agent.Discovery;

/// <summary>
/// Advertises the agent once the server listens, keeps the <c>sensors</c> TXT value current, and restarts
/// the advertisement if its helper process dies.
/// </summary>
public sealed partial class DiscoveryService(
    AgentRunOptions options,
    AgentIdentity identity,
    AgentCertificate certificate,
    SensorHub hub,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    // Process-based advertisers briefly withdraw the service on every TXT change, so updates are rate-limited.
    private static readonly TimeSpan MinimumUpdateGap = TimeSpan.FromSeconds(30);

    private readonly ILogger logger = loggers.CreateLogger<DiscoveryService>();
    private IServiceAdvertiser? advertiser;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = options.Discovery.Trim().ToLowerInvariant();
        if (mode == "off")
        {
            LogDisabled(logger, options.Port);
            return;
        }

        try
        {
            await WaitForStartAsync(stoppingToken);
            var published = Registration();
            advertiser = await StartAnyAsync(mode, published, stoppingToken);
            if (advertiser is null)
                return;

            var lastUpdate = DateTimeOffset.UtcNow;
            var restartDelay = TimeSpan.FromSeconds(5);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);
                var current = Registration();

                if (!advertiser.IsRunning)
                {
                    LogStopped(logger, advertiser.Name, restartDelay.TotalSeconds);
                    await Task.Delay(restartDelay, stoppingToken);
                    try
                    {
                        await advertiser.StartAsync(current, stoppingToken);
                        published = current;
                        restartDelay = TimeSpan.FromSeconds(5);
                    }
                    catch (DiscoveryUnavailableException ex)
                    {
                        LogRestartFailed(logger, advertiser.Name, ex.Message);
                        restartDelay = TimeSpan.FromTicks(Math.Min(restartDelay.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
                    }
                    continue;
                }

                if (current["sensors"] != published["sensors"] && DateTimeOffset.UtcNow - lastUpdate >= MinimumUpdateGap)
                {
                    await advertiser.UpdateAsync(current, stoppingToken);
                    published = current;
                    lastUpdate = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (DiscoveryUnavailableException ex)
        {
            LogUnavailable(logger, ex.Message, options.Port);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (advertiser is not null)
            await advertiser.DisposeAsync();
    }

    private ServiceRegistration Registration() => ServiceRegistration.Create(identity, certificate, options.Port, hub.Catalog.Count);

    private async Task<IServiceAdvertiser?> StartAnyAsync(string mode, ServiceRegistration registration, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var candidate in Candidates(mode))
        {
            if (candidate is null)
                continue;
            try
            {
                await candidate.StartAsync(registration, cancellationToken);
                return candidate;
            }
            catch (DiscoveryUnavailableException ex)
            {
                failures.Add($"{candidate.Name}: {ex.Message}");
                LogCandidateFailed(logger, candidate.Name, ex.Message);
                await candidate.DisposeAsync();
            }
        }

        LogUnavailable(logger, failures.Count > 0 ? string.Join("; ", failures) : $"no advertiser for mode '{mode}'", options.Port);
        return null;
    }

    private IEnumerable<IServiceAdvertiser?> Candidates(string mode)
    {
        var commandLogger = loggers.CreateLogger<CommandAdvertiser>();
        var managedLogger = loggers.CreateLogger<ManagedMdnsAdvertiser>();
        switch (mode)
        {
            case "avahi":
                yield return CommandAdvertiser.Avahi(commandLogger);
                break;
            case "dns-sd":
                yield return CommandAdvertiser.DnsSd(commandLogger);
                break;
            case "managed":
                yield return new ManagedMdnsAdvertiser(managedLogger);
                break;
            default:
                if (OperatingSystem.IsMacOS())
                {
                    yield return CommandAdvertiser.DnsSd(commandLogger);
                }
                else if (OperatingSystem.IsLinux() && AvahiDaemonRunning())
                {
                    // Avahi owns UDP 5353 on most desktops, so going through it avoids two responders fighting.
                    yield return CommandAdvertiser.Avahi(commandLogger);
                }
                yield return new ManagedMdnsAdvertiser(managedLogger);
                break;
        }
    }

    private static bool AvahiDaemonRunning() =>
        File.Exists("/run/avahi-daemon/socket") || File.Exists("/var/run/avahi-daemon/socket");

    private async Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovery is off; add this PC on the iPad by IP address and port {Port}")]
    private static partial void LogDisabled(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery isn't available ({Reason}). The iPad won't find this PC automatically; add it by IP address and port {Port}")]
    private static partial void LogUnavailable(ILogger logger, string reason, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovery via {Method} isn't available: {Reason}")]
    private static partial void LogCandidateFailed(ILogger logger, string method, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery via {Method} stopped; restarting in {Seconds} s")]
    private static partial void LogStopped(ILogger logger, string method, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restarting discovery via {Method} failed: {Reason}")]
    private static partial void LogRestartFailed(ILogger logger, string method, string reason);
}
