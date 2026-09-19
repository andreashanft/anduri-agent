using System.Diagnostics;
using System.Text;

namespace Anduri.Agent.Discovery;

/// <summary>
/// Advertises through the system's mDNS daemon by keeping a helper process running:
/// <c>avahi-publish-service</c> on Linux, <c>dns-sd -R</c> on macOS. The daemon withdraws the service when the process
/// exits, and TXT changes are published by restarting it.
/// </summary>
public sealed partial class CommandAdvertiser : IServiceAdvertiser
{
    // A helper that can't reach its daemon exits right away; one that is still running after this has registered.
    private static readonly TimeSpan StartupProbe = TimeSpan.FromMilliseconds(1500);

    private readonly string executable;
    private readonly Func<ServiceRegistration, IEnumerable<string>> arguments;
    private readonly ILogger logger;
    private Process? process;
    private readonly StringBuilder errors = new();

    private CommandAdvertiser(string name, string executable, Func<ServiceRegistration, IEnumerable<string>> arguments, ILogger logger)
    {
        Name = name;
        this.executable = executable;
        this.arguments = arguments;
        this.logger = logger;
    }

    public string Name { get; }

    public bool IsRunning => process is { HasExited: false };

    /// <summary><c>dns-sd -R name _anduri._tcp local. port key=value …</c> (macOS mDNSResponder).</summary>
    public static CommandAdvertiser? DnsSd(ILogger logger) =>
        Executables.Find("dns-sd") is { } path
            ? new CommandAdvertiser("dns-sd", path, r => ["-R", r.InstanceName, ServiceRegistration.ServiceType, "local.", r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. r.TxtStrings], logger)
            : null;

    /// <summary><c>avahi-publish-service name _anduri._tcp port key=value …</c> (needs a running avahi-daemon).</summary>
    public static CommandAdvertiser? Avahi(ILogger logger) =>
        Executables.Find("avahi-publish-service") is { } path
            ? new CommandAdvertiser("avahi", path, r => [r.InstanceName, ServiceRegistration.ServiceType, r.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. r.TxtStrings], logger)
            : null;

    public async Task StartAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        await StopAsync();
        lock (errors)
            errors.Clear();

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments(registration))
            startInfo.ArgumentList.Add(argument);

        var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        started.OutputDataReceived += (_, e) => OnOutput(e.Data);
        started.ErrorDataReceived += (_, e) => OnOutput(e.Data, isError: true);
        try
        {
            started.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            started.Dispose();
            throw new DiscoveryUnavailableException($"{executable} couldn't be started: {ex.Message}", ex);
        }

        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;

        try
        {
            await started.WaitForExitAsync(cancellationToken).WaitAsync(StartupProbe, cancellationToken);
        }
        catch (TimeoutException)
        {
            LogStarted(logger, Name, registration.InstanceName, registration.Port);
            return;
        }

        string output;
        lock (errors)
            output = errors.ToString().Trim();
        await StopAsync();
        throw new DiscoveryUnavailableException($"{Path.GetFileName(executable)} exited: {(output.Length > 0 ? output : "no output")}");
    }

    public Task UpdateAsync(ServiceRegistration registration, CancellationToken cancellationToken) => StartAsync(registration, cancellationToken);

    private void OnOutput(string? line, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (isError)
        {
            lock (errors)
                errors.AppendLine(line);
        }
        LogOutput(logger, Name, line);
    }

    private async Task StopAsync()
    {
        if (process is not { } running)
            return;
        process = null;
        try
        {
            if (!running.HasExited)
            {
                // The daemon notices the client going away and withdraws the service.
                running.Kill();
                await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            running.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [LoggerMessage(Level = LogLevel.Information, Message = "Advertising “{Instance}” as _anduri._tcp on port {Port} via {Method}")]
    private static partial void LogStarted(ILogger logger, string method, string instance, int port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Method}: {Line}")]
    private static partial void LogOutput(ILogger logger, string method, string line);
}
