using System.Net.WebSockets;
using System.Security.Authentication;
using Anduri.Agent.Configuration;
using Anduri.Agent.Discovery;
using Anduri.Agent.Pairing;
using Anduri.Agent.Sensors;
using Anduri.Agent.State;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Anduri.Agent.Server;

/// <summary>Effective settings for <c>anduri-agent run</c>, after merging config file and flags.</summary>
public sealed record AgentRunOptions
{
    public const int DefaultPort = 48123;

    public required string Name { get; init; }
    public int Port { get; init; } = DefaultPort;
    public required StateDirectory State { get; init; }
    public AgentConfig Config { get; init; } = new();
    /// <summary>The config file that was loaded, or null when the defaults are used.</summary>
    public string? ConfigPath { get; init; }
    public bool Simulate { get; init; }
    public string Discovery { get; init; } = "auto";
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>The host name without domain, e.g. "RIG-01".</summary>
    public static string DefaultName()
    {
        var name = Environment.MachineName.Split('.')[0];
        return string.IsNullOrWhiteSpace(name) ? "Anduri agent" : name;
    }
}

/// <summary>Builds the agent: Kestrel with TLS and WebSockets, sampler, pairing and discovery.</summary>
public static partial class AgentHost
{
    public static WebApplication Build(AgentRunOptions options, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "anduri-agent",
            ContentRootPath = AppContext.BaseDirectory,
        });

        ConfigureLogging(builder.Logging, options.MinimumLogLevel);

        options.State.EnsureExists();
        var identity = AgentIdentity.LoadOrCreate(options.State, options.Name);
        var certificate = new CertificateStore(options.State).LoadOrCreate(options.Name);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 100;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.ListenAnyIP(options.Port, listen =>
            {
                // WebSockets over HTTP/2 need extended CONNECT, which URLSessionWebSocketTask doesn't use.
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate.Certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate,
                });
            });
        });
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(10));

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(options.Config);
        services.AddSingleton(options.State);
        services.AddSingleton(identity);
        services.AddSingleton(certificate);
        services.AddSingleton<DeviceStore>();
        services.AddSingleton<PairingManager>();
        services.AddSingleton<IPairingCodeDisplay, LogPairingCodeDisplay>();
        services.AddSingleton<SensorHub>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton(sp => SensorSourceFactory.Create(
            options.Config, options.Simulate, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new SensorSampler(
            sp.GetRequiredService<SensorHub>(),
            sp.GetRequiredService<SensorSources>().Sources,
            options.Config,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SensorSampler>>()));
        services.AddHostedService(sp => sp.GetRequiredService<SensorSampler>());
        services.AddHostedService<DiscoveryService>();
        services.AddTransient<AgentSession>();

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            // Detects iPads that went to sleep without closing the connection.
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(45),
        });
        app.Run(HandleRequestAsync);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Anduri.Agent");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            LogListening(logger, identity.Name, options.Port, certificate.FingerprintHex, options.State.Path);
            // Under systemd "~" is the state directory and home directories are hidden, so say which file counted.
            if (options.ConfigPath is { } configPath)
                LogConfigFile(logger, configPath);
            else
                LogNoConfigFile(logger, string.Join(" or ", AgentConfigLoader.DefaultPaths()));
        });
        app.Lifetime.ApplicationStopping.Register(() =>
            LogStopping(logger, app.Services.GetRequiredService<SessionRegistry>().Count));
        return app;
    }

    /// <summary>The iPad connects to <c>/</c>; <c>/v1</c> is the versioned alias from the protocol.</summary>
    private static async Task HandleRequestAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is not ("/" or "" or "/v1" or "/v1/"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            context.Response.Headers.Upgrade = "websocket";
            await context.Response.WriteAsync("Anduri agent: connect with a WebSocket (protocol v1).\n", context.RequestAborted);
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        var session = context.RequestServices.GetRequiredService<AgentSession>();
        var remote = context.Connection.RemoteIpAddress is { } ip ? $"{(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)}:{context.Connection.RemotePort}" : "?";
        await session.RunAsync(webSocket, remote, context.RequestAborted);
    }

    private static void ConfigureLogging(ILoggingBuilder logging, LogLevel minimum)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(minimum);
        // Kestrel's own connection noise isn't useful for someone running the agent.
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        // Under systemd, journald adds timestamps and understands <N> priority prefixes.
        if (Environment.GetEnvironmentVariable("JOURNAL_STREAM") is { Length: > 0 })
        {
            logging.AddSystemdConsole();
        }
        else
        {
            logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping; closing {Connections} connection(s) with 1001")]
    private static partial void LogStopping(ILogger logger, int connections);

    [LoggerMessage(Level = LogLevel.Information, Message = "Config file: {Path}")]
    private static partial void LogConfigFile(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "No config file, using defaults (looked for {Paths})")]
    private static partial void LogNoConfigFile(ILogger logger, string paths);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anduri agent “{Name}” listening on wss://*:{Port}/ — certificate fingerprint {Fingerprint}, state in {StateDirectory}")]
    private static partial void LogListening(ILogger logger, string name, int port, string fingerprint, string stateDirectory);
}
