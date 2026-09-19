using System.Net.WebSockets;
using System.Threading.Channels;
using Anduri.Agent.Pairing;
using Anduri.Agent.Protocol;
using Anduri.Agent.Sensors;
using Anduri.Agent.State;

namespace Anduri.Agent.Server;

/// <summary>
/// One WebSocket connection. Everything that changes session state (frames, timers, catalog changes, shutdown)
/// goes through a single inbox and is handled one event at a time, so there are no locks and sends never overlap.
/// </summary>
public sealed partial class AgentSession
{
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long an unauthenticated connection may stay open after its pairing window expired.</summary>
    public static readonly TimeSpan ExpiredWindowGrace = TimeSpan.FromSeconds(60);

    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxDeviceNameLength = 64;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentIdentity identity;
    private readonly AgentCertificate certificate;
    private readonly SensorHub hub;
    private readonly PairingManager pairing;
    private readonly DeviceStore devices;
    private readonly SessionRegistry registry;
    private readonly TimeProvider time;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<AgentSession> logger;
    private readonly Channel<SessionEvent> inbox = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

    private WebSocket socket = null!;
    private CancellationToken aborted;
    private Task receiveLoop = Task.CompletedTask;
    private string remote = "?";

    private bool streaming;
    private string? deviceName;
    private PairingDeviceInfo? pairingDevice;
    private double interval = SnapshotInterval.Default;

    private ITimer? deadlineTimer;
    private int deadlineGeneration;
    private ITimer? snapshotTimer;
    private int snapshotPending;

    public AgentSession(
        AgentIdentity identity,
        AgentCertificate certificate,
        SensorHub hub,
        PairingManager pairing,
        DeviceStore devices,
        SessionRegistry registry,
        TimeProvider time,
        IHostApplicationLifetime lifetime,
        ILogger<AgentSession> logger)
    {
        this.identity = identity;
        this.certificate = certificate;
        this.hub = hub;
        this.pairing = pairing;
        this.devices = devices;
        this.registry = registry;
        this.time = time;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    private abstract record SessionEvent;
    private sealed record FrameReceived(byte[]? Payload, bool Binary, bool TooLarge) : SessionEvent;
    private sealed record Disconnected : SessionEvent;
    private sealed record DeadlinePassed(int Generation) : SessionEvent;
    private sealed record SnapshotDue : SessionEvent;
    private sealed record CatalogChanged : SessionEvent;
    private sealed record ShuttingDown : SessionEvent;

    /// <summary>Called by the registry from the sampler thread.</summary>
    internal void NotifyCatalogChanged() => inbox.Writer.TryWrite(new CatalogChanged());

    public async Task RunAsync(WebSocket webSocket, string remoteAddress, CancellationToken requestAborted)
    {
        socket = webSocket;
        aborted = requestAborted;
        remote = remoteAddress;
        LogConnected(logger, remote);

        registry.Add(this);
        using var stopping = lifetime.ApplicationStopping.Register(() => inbox.Writer.TryWrite(new ShuttingDown()));
        receiveLoop = ReceiveLoopAsync();
        ArmDeadline(HelloTimeout);

        try
        {
            await foreach (var sessionEvent in inbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (!await HandleAsync(sessionEvent))
                    break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // The connection broke while sending; nothing left to tell the client.
        }
        finally
        {
            inbox.Writer.TryComplete();
            deadlineTimer?.Dispose();
            snapshotTimer?.Dispose();
            pairing.Release(this);
            registry.Remove(this);

            if (!receiveLoop.IsCompleted)
                socket.Abort();
            await receiveLoop;
            LogDisconnected(logger, remote, deviceName ?? "unauthenticated", socket.CloseStatus is { } status ? (int)status : null);
        }
    }

    private async Task<bool> HandleAsync(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case FrameReceived frame:
                return await HandleFrameAsync(frame);

            case Disconnected:
                // The client closed first: complete the handshake.
                if (socket.State == WebSocketState.CloseReceived)
                    await CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "");
                return false;

            case DeadlinePassed deadline when deadline.Generation == deadlineGeneration && !streaming:
                return await FailAsync(ErrorCodes.Timeout, "No hello or pair.request in time.");

            case SnapshotDue:
                Interlocked.Exchange(ref snapshotPending, 0);
                if (streaming)
                    await SendSnapshotAsync();
                return true;

            case CatalogChanged when streaming:
                await SendAsync(new CatalogMessage(hub.Catalog));
                return true;

            case ShuttingDown:
                return await CloseAsync(CloseCodes.GoingAway, "Agent is shutting down");

            default:
                return true;
        }
    }

    private async Task<bool> HandleFrameAsync(FrameReceived frame)
    {
        if (frame.TooLarge)
            return await FailAsync(ErrorCodes.BadRequest, $"Messages are limited to {MaxMessageBytes / 1024} KB.");
        if (frame.Binary)
            return await FailAsync(ErrorCodes.BadRequest, "Messages must be text frames.");

        ProtocolMessage? message;
        try
        {
            message = ProtocolJson.Deserialize(frame.Payload ?? []);
        }
        catch (ProtocolFormatException ex)
        {
            return await FailAsync(ErrorCodes.BadRequest, ex.Message);
        }

        return message switch
        {
            HelloMessage hello => await OnHelloAsync(hello),
            PairRequestMessage request => await OnPairRequestAsync(request),
            PairConfirmMessage confirm => await OnPairConfirmAsync(confirm),
            SetIntervalMessage setInterval => await OnSetIntervalAsync(setInterval),
            // Unknown types, and agent → client types a client has no reason to send, are ignored.
            _ => true,
        };
    }

    private async Task<bool> OnHelloAsync(HelloMessage hello)
    {
        if (streaming)
            return await FailAsync(ErrorCodes.BadRequest, "hello was already received on this connection.");
        if (hello.Protocol is not { } protocol)
            return await FailAsync(ErrorCodes.BadRequest, "hello needs a protocol version.");
        if (protocol < ProtocolVersion.Minimum)
            return await FailAsync(ErrorCodes.UnsupportedProtocol, $"This agent speaks protocol {ProtocolVersion.Minimum} to {ProtocolVersion.Current}.");
        if (string.IsNullOrEmpty(hello.DeviceId) || string.IsNullOrEmpty(hello.Token))
            return await FailAsync(ErrorCodes.BadRequest, "hello needs deviceId and token.");

        var device = devices.Authenticate(hello.DeviceId, hello.Token);
        if (device is null)
        {
            LogUnauthorized(logger, remote, Sanitize(hello.DeviceId));
            return await FailAsync(ErrorCodes.Unauthorized, $"This device isn't paired with {identity.Name}.");
        }

        pairing.Release(this);
        pairingDevice = null;
        DisarmDeadline();
        streaming = true;
        deviceName = device.Name;
        interval = SnapshotInterval.Clamp(hello.Interval);
        LogAuthenticated(logger, remote, device.Name, interval);

        // hub.Catalog is read after streaming is set, so a catalog change racing with this hello is re-sent, never lost.
        var catalog = hub.Catalog;
        await SendAsync(new WelcomeMessage(ProtocolVersion.Current, identity.ToInfo(), interval));
        await SendAsync(new CatalogMessage(catalog));
        if (hello.History == true)
            await SendAsync(hub.History.ToMessage(catalog.Select(sensor => sensor.Id)));
        await SendSnapshotAsync();

        var period = TimeSpan.FromSeconds(interval);
        snapshotTimer = time.CreateTimer(_ => RequestSnapshot(), null, period, period);
        return true;
    }

    private async Task<bool> OnPairRequestAsync(PairRequestMessage request)
    {
        if (streaming)
            return await FailAsync(ErrorCodes.BadRequest, "This connection is already authenticated.");
        if (request.Protocol is { } protocol && protocol < ProtocolVersion.Minimum)
            return await FailAsync(ErrorCodes.UnsupportedProtocol, $"This agent speaks protocol {ProtocolVersion.Minimum} to {ProtocolVersion.Current}.");
        if (string.IsNullOrWhiteSpace(request.Device?.Id))
            return await FailAsync(ErrorCodes.BadRequest, "pair.request needs device.id.");

        var name = Sanitize(request.Device.Name);
        var device = new PairingDeviceInfo(
            request.Device.Id,
            string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name,
            request.Device.Model is { } model ? Sanitize(model) : null);

        var result = pairing.Open(this, device);
        if (result.Status == PairingOpenStatus.Busy)
        {
            LogPairingBusy(logger, remote, device.Name);
            await SendAsync(new PairRejectedMessage(PairingReasons.Busy));
            return await CloseAsync(CloseCodes.Busy, ErrorCodes.Busy);
        }

        pairingDevice = device;
        ArmDeadline(result.ExpiresIn + ExpiredWindowGrace);
        await SendAsync(new PairChallengeMessage(Convert.ToBase64String(result.Nonce), (int)result.ExpiresIn.TotalSeconds, identity.ToInfo()));
        return true;
    }

    private async Task<bool> OnPairConfirmAsync(PairConfirmMessage confirm)
    {
        if (streaming)
            return await FailAsync(ErrorCodes.BadRequest, "This connection is already authenticated.");
        if (pairingDevice is null)
            return await FailAsync(ErrorCodes.BadRequest, "pair.confirm needs a pair.request first.");

        var proof = new byte[64];
        if (confirm.Proof is null || !Convert.TryFromBase64String(confirm.Proof, proof, out var proofLength))
            return await FailAsync(ErrorCodes.BadRequest, "proof isn't valid Base64.");

        var result = pairing.Confirm(this, proof.AsSpan(0, proofLength), certificate.Fingerprint);
        switch (result.Status)
        {
            case PairingConfirmStatus.Accepted:
            {
                var device = result.Device!;
                var token = PairingCrypto.GenerateToken();
                devices.Add(new PairedDevice(device.Id, device.Name, device.Model, PairingCrypto.HashToken(token)!, time.GetUtcNow(), null));
                pairingDevice = null;
                ArmDeadline(HelloTimeout);
                await SendAsync(new PairAcceptedMessage(token, Convert.ToBase64String(result.AgentProof!), identity.ToInfo()));
                return true;
            }
            case PairingConfirmStatus.WrongCode:
                await SendAsync(new PairRejectedMessage(PairingReasons.WrongCode, result.AttemptsLeft));
                return true;
            case PairingConfirmStatus.TooManyAttempts:
                await SendAsync(new PairRejectedMessage(PairingReasons.TooManyAttempts));
                return await CloseAsync(CloseCodes.TooManyAttempts, ErrorCodes.TooManyAttempts);
            default:
                await SendAsync(new PairRejectedMessage(PairingReasons.Expired));
                return true;
        }
    }

    private async Task<bool> OnSetIntervalAsync(SetIntervalMessage setInterval)
    {
        if (!streaming)
            return await FailAsync(ErrorCodes.BadRequest, "setInterval needs a hello first.");
        if (setInterval.Interval is null)
            return await FailAsync(ErrorCodes.BadRequest, "setInterval needs an interval.");

        interval = SnapshotInterval.Clamp(setInterval.Interval);
        var period = TimeSpan.FromSeconds(interval);
        snapshotTimer?.Change(period, period);
        await SendAsync(new IntervalMessage(interval));
        return true;
    }

    private void RequestSnapshot()
    {
        // Coalesce: a client that can't keep up gets fewer snapshots instead of a growing queue.
        if (Interlocked.Exchange(ref snapshotPending, 1) == 0)
            inbox.Writer.TryWrite(new SnapshotDue());
    }

    private async Task SendSnapshotAsync()
    {
        if (hub.Latest is { } frame)
            await SendAsync(new SnapshotMessage(frame.UnixSeconds, frame.Values));
    }

    private async Task SendAsync(ProtocolMessage message)
    {
        var bytes = ProtocolJson.SerializeToUtf8(message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(SendTimeout);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
    }

    /// <summary>Sends <c>error</c> and closes with the code the protocol assigns to it.</summary>
    private async Task<bool> FailAsync(string code, string message)
    {
        LogFailure(logger, remote, code, message);
        await SendAsync(new ErrorMessage(code, message));
        return await CloseAsync(CloseCodes.ForError(code), code);
    }

    private async Task<bool> CloseAsync(int code, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await CloseOutputAsync((WebSocketCloseStatus)code, reason);

        // Give the client a moment to answer with its close frame, so it sees a clean close with our code.
        try
        {
            await receiveLoop.WaitAsync(CloseHandshakeTimeout, time);
        }
        catch (TimeoutException)
        {
            socket.Abort();
        }

        return false;
    }

    private async Task CloseOutputAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            timeout.CancelAfter(CloseHandshakeTimeout);
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        var tooLarge = false;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), aborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (!tooLarge)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        tooLarge = true;
                        message.SetLength(0);
                    }
                }

                if (result.EndOfMessage)
                {
                    inbox.Writer.TryWrite(new FrameReceived(tooLarge ? null : message.ToArray(), result.MessageType == WebSocketMessageType.Binary, tooLarge));
                    message.SetLength(0);
                    tooLarge = false;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            inbox.Writer.TryWrite(new Disconnected());
        }
    }

    private void ArmDeadline(TimeSpan dueIn)
    {
        var generation = Interlocked.Increment(ref deadlineGeneration);
        deadlineTimer?.Dispose();
        deadlineTimer = time.CreateTimer(_ => inbox.Writer.TryWrite(new DeadlinePassed(generation)), null, dueIn, Timeout.InfiniteTimeSpan);
    }

    private void DisarmDeadline()
    {
        Interlocked.Increment(ref deadlineGeneration);
        deadlineTimer?.Dispose();
        deadlineTimer = null;
    }

    /// <summary>Device names end up in the log; drop control characters so they can't forge log lines.</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= MaxDeviceNameLength ? clean : clean[..MaxDeviceNameLength];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connection from {Remote}")]
    private static partial void LogConnected(ILogger logger, string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "“{DeviceName}” connected from {Remote} (snapshot interval {Interval} s)")]
    private static partial void LogAuthenticated(ILogger logger, string remote, string deviceName, double interval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected unknown device or token from {Remote} (device id {DeviceId})")]
    private static partial void LogUnauthorized(ILogger logger, string remote, string deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing request from “{DeviceName}” at {Remote} rejected: another device is pairing")]
    private static partial void LogPairingBusy(ILogger logger, string remote, string deviceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closing connection from {Remote}: {Code} ({Message})")]
    private static partial void LogFailure(ILogger logger, string remote, string code, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connection from {Remote} ({DeviceName}) closed with {CloseStatus}")]
    private static partial void LogDisconnected(ILogger logger, string remote, string deviceName, int? closeStatus);
}
