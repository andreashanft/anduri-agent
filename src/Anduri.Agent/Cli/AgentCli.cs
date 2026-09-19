using System.Globalization;
using Anduri.Agent.Configuration;
using Anduri.Agent.Server;
using Anduri.Agent.State;

namespace Anduri.Agent.Cli;

/// <summary>The <c>anduri-agent</c> commands.</summary>
public static class AgentCli
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "simulate", "help", "version", "verbose", "yes" };

    public const string Usage = """
        Anduri agent: streams this PC's sensors to the Anduri iPad app.

        Usage:
          anduri-agent run [options]          Run the agent (the service)
          anduri-agent devices list           List paired iPads
          anduri-agent devices revoke <id>    Unpair an iPad (full id or a unique prefix)
          anduri-agent cert show              Show the certificate fingerprint
          anduri-agent cert regenerate        Create a new certificate (every iPad must pair again)
          anduri-agent --version

        Options:
          --port <port>         WebSocket port (default 48123)
          --name <name>         Name shown on the iPad (default: host name)
          --simulate            Stream a simulated water-cooled rig instead of real sensors
          --config <path>       Config file (default: ~/.config/anduri-agent/config.json,
                                then /etc/anduri-agent/config.json; the systemd service
                                only sees /etc/anduri-agent/config.json)
          --state-dir <path>    Where the agent id, certificate and paired devices are kept
                                (default: $STATE_DIRECTORY, $XDG_STATE_HOME/anduri-agent
                                or ~/.local/state/anduri-agent)
          --discovery <mode>    auto, avahi, dns-sd, managed or off (default auto)
          --verbose             Debug logging
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = CommandLine.Parse(args, Switches);
            if (command.Has("version"))
            {
                output.WriteLine($"anduri-agent {AgentIdentity.CurrentVersion} (protocol 1)");
                return 0;
            }

            if (command.Has("help") || command.Positional.Count == 0 || command.Positional[0] == "help")
            {
                output.WriteLine(Usage);
                return command.Positional.Count == 0 && !command.Has("help") ? 2 : 0;
            }

            return command.Positional switch
            {
                ["run"] => await RunAgentAsync(command, cancellationToken),
                ["devices", "list"] => ListDevices(command, output),
                ["devices", "revoke", var id] => RevokeDevice(command, id, output, error),
                ["cert", "show"] => ShowCertificate(command, output),
                ["cert", "regenerate"] => RegenerateCertificate(command, output),
                _ => throw new UsageException($"Unknown command '{string.Join(' ', command.Positional)}'. Run 'anduri-agent --help'."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            error.WriteLine($"anduri-agent: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAgentAsync(CommandLine command, CancellationToken cancellationToken)
    {
        var (config, configPath) = LoadConfig(command);
        var simulate = command.Has("simulate") || config.Simulate;
        var options = new AgentRunOptions
        {
            Name = command.Get("name") ?? config.Name ?? AgentRunOptions.DefaultName(),
            Port = command.GetInt("port", 1, 65535) ?? config.Port ?? AgentRunOptions.DefaultPort,
            State = new StateDirectory(ResolveStateDir(command, config)),
            Config = config,
            ConfigPath = configPath,
            Simulate = simulate,
            Discovery = command.Get("discovery") ?? config.Discovery,
            MinimumLogLevel = command.Has("verbose") ? LogLevel.Debug : LogLevel.Information,
        };
        command.EnsureAllConsumed();

        if (options.Discovery.ToLowerInvariant() is not ("auto" or "avahi" or "dns-sd" or "managed" or "off"))
            throw new UsageException("--discovery must be auto, avahi, dns-sd, managed or off.");

        await using var app = AgentHost.Build(options);
        await app.RunAsync(cancellationToken);
        return 0;
    }

    private static int ListDevices(CommandLine command, TextWriter output)
    {
        var state = OpenState(command);
        command.EnsureAllConsumed();

        var devices = new DeviceStore(state, TimeProvider.System).List();
        if (devices.Count == 0)
        {
            output.WriteLine($"No paired devices (state directory {state.Path}).");
            return 0;
        }

        output.WriteLine($"{"ID",-36}  {"NAME",-24}  {"PAIRED",-16}  LAST SEEN");
        foreach (var device in devices.OrderBy(d => d.PairedAt))
        {
            output.WriteLine($"{device.Id,-36}  {Truncate(device.Name, 24),-24}  {FormatDate(device.PairedAt),-16}  {(device.LastSeen is { } seen ? FormatDate(seen) : "never")}");
        }
        return 0;
    }

    private static int RevokeDevice(CommandLine command, string id, TextWriter output, TextWriter error)
    {
        var state = OpenState(command);
        command.EnsureAllConsumed();

        var removed = new DeviceStore(state, TimeProvider.System).Revoke(id);
        if (removed is null)
        {
            error.WriteLine($"No paired device matches '{id}'. Run 'anduri-agent devices list'.");
            return 1;
        }

        output.WriteLine($"Revoked “{removed.Name}” ({removed.Id}). Its next connection is refused with 'unauthorized'.");
        return 0;
    }

    private static int ShowCertificate(CommandLine command, TextWriter output)
    {
        var state = OpenState(command);
        command.EnsureAllConsumed();

        var store = new CertificateStore(state);
        if (!store.Exists)
        {
            output.WriteLine($"No certificate yet in {state.Path}; the agent creates one on its first run.");
            return 1;
        }

        var certificate = store.Load();
        using (certificate.Certificate)
        {
            output.WriteLine($"Fingerprint (SHA-256): {certificate.FingerprintHex}");
            output.WriteLine($"Short (TXT fp):        {certificate.ShortFingerprint}");
            output.WriteLine($"Subject:               {certificate.Certificate.Subject}");
            output.WriteLine($"Valid until:           {certificate.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}");
            output.WriteLine($"File:                  {state.CertificateFile}");
        }
        return 0;
    }

    private static int RegenerateCertificate(CommandLine command, TextWriter output)
    {
        var (config, _) = LoadConfig(command);
        var state = new StateDirectory(ResolveStateDir(command, config));
        var name = command.Get("name") ?? config.Name ?? AgentRunOptions.DefaultName();
        command.EnsureAllConsumed();

        var certificate = new CertificateStore(state).Regenerate(name);
        using (certificate.Certificate)
        {
            output.WriteLine($"New certificate: {certificate.FingerprintHex}");
        }
        output.WriteLine("Restart the agent to use it. Every paired iPad will refuse the new certificate until it is paired again.");
        return 0;
    }

    private static StateDirectory OpenState(CommandLine command)
    {
        var (config, _) = LoadConfig(command);
        return new StateDirectory(ResolveStateDir(command, config));
    }

    private static (AgentConfig Config, string? Path) LoadConfig(CommandLine command) => AgentConfigLoader.Load(command.Get("config"));

    private static string ResolveStateDir(CommandLine command, AgentConfig config) =>
        command.Get("state-dir") ?? config.StateDir ?? StateDirectory.ResolveDefault();

    private static string FormatDate(DateTimeOffset date) => date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
