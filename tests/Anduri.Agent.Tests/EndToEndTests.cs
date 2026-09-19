using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anduri.Agent.Pairing;
using Anduri.Agent.Server;
using Anduri.Agent.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Anduri.Agent.Tests;

public class EndToEndTests
{
    private const string DeviceId = "6F9619FF-8B86-D011-B42D-00C04FC964FF";

    [Fact]
    public async Task PairThenHelloStreamsWelcomeCatalogAndSnapshots()
    {
        await using var agent = await TestAgent.StartAsync();
        await using var client = await TestClient.ConnectAsync(agent.Port, "/");

        // The fingerprint the client saw during TLS is the one in the agent's state.
        Assert.Equal(agent.Certificate.Fingerprint, client.ServerFingerprint);

        await client.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = DeviceId, name = "Homer’s iPad", model = "iPad" } });
        var challenge = await client.ReceiveAsync("pair.challenge");
        var nonce = Convert.FromBase64String(challenge.GetProperty("nonce").GetString()!);
        Assert.Equal(32, nonce.Length);
        Assert.Equal(120, challenge.GetProperty("expiresIn").GetInt32());
        Assert.Equal("RIG-TEST", challenge.GetProperty("agent").GetProperty("name").GetString());

        var notice = Assert.Single(agent.Display.Notices);
        Assert.Equal("Homer’s iPad", notice.Device.Name);
        var code = notice.Code;
        var fingerprint = client.ServerFingerprint!;

        // A wrong code first.
        var wrongCode = ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        await client.SendAsync(new { type = "pair.confirm", proof = Convert.ToBase64String(PairingCrypto.ComputeClientProof(wrongCode, nonce, fingerprint, DeviceId)) });
        var rejected = await client.ReceiveAsync("pair.rejected");
        Assert.Equal("wrong_code", rejected.GetProperty("reason").GetString());
        Assert.Equal(4, rejected.GetProperty("attemptsLeft").GetInt32());

        await client.SendAsync(new { type = "pair.confirm", proof = Convert.ToBase64String(PairingCrypto.ComputeClientProof(code, nonce, fingerprint, DeviceId)) });
        var accepted = await client.ReceiveAsync("pair.accepted");
        var token = accepted.GetProperty("token").GetString()!;
        var agentProof = Convert.FromBase64String(accepted.GetProperty("proof").GetString()!);
        Assert.Equal(PairingCrypto.ComputeAgentProof(code, nonce, fingerprint, DeviceId), agentProof);
        Assert.Equal(43, token.Length);
        Assert.Equal(agent.Identity.Id, accepted.GetProperty("agent").GetProperty("id").GetString());

        // Only the hash of the token is stored.
        var devicesJson = await File.ReadAllTextAsync(agent.State.DevicesFile);
        Assert.DoesNotContain(token, devicesJson);
        Assert.Contains(PairingCrypto.HashToken(token)!, devicesJson);

        // The same connection continues with hello.
        await client.SendAsync(new { type = "hello", protocol = 1, deviceId = DeviceId, token, interval = 1, history = false });
        var welcome = await client.ReceiveAsync("welcome");
        Assert.Equal(1, welcome.GetProperty("protocol").GetInt32());
        Assert.Equal(1, welcome.GetProperty("interval").GetDouble());
        Assert.Equal("RIG-TEST", welcome.GetProperty("agent").GetProperty("name").GetString());
        Assert.Equal(AgentIdentity.CurrentOs, welcome.GetProperty("agent").GetProperty("os").GetString());

        var catalog = await client.ReceiveAsync("catalog");
        var ids = catalog.GetProperty("sensors").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("loop.coolant", ids);
        Assert.Equal(26, ids.Count);

        var snapshot = await client.ReceiveAsync("snapshot");
        Assert.True(snapshot.GetProperty("values").TryGetProperty("cpu.temp", out _));
        var t = snapshot.GetProperty("t").GetDouble();
        Assert.InRange(t, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);

        // …and keeps streaming.
        var next = await client.ReceiveAsync("snapshot", TimeSpan.FromSeconds(4));
        Assert.True(next.GetProperty("t").GetDouble() >= t);
    }

    [Fact]
    public async Task ReconnectWithTokenGetsHistoryAndHonoursSetInterval()
    {
        await using var agent = await TestAgent.StartAsync();
        var token = await agent.PairAsync(DeviceId);

        await using var client = await TestClient.ConnectAsync(agent.Port, "/v1");
        await client.SendAsync(new { type = "some.future.message", payload = 42 }); // ignored
        await client.SendAsync(new { type = "hello", protocol = 1, deviceId = DeviceId, token, interval = 0.1, history = true, extra = "ignored" });

        var welcome = await client.ReceiveAsync("welcome");
        Assert.Equal(0.5, welcome.GetProperty("interval").GetDouble());
        var catalog = await client.ReceiveAsync("catalog");
        var catalogIds = catalog.GetProperty("sensors").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToHashSet();

        var history = await client.ReceiveAsync("history");
        Assert.Equal(1, history.GetProperty("step").GetDouble());
        var series = history.GetProperty("series").EnumerateObject().ToList();
        Assert.NotEmpty(series);
        Assert.All(series, s => Assert.Contains(s.Name, catalogIds));
        var lengths = series.Select(s => s.Value.GetArrayLength()).Distinct().ToList();
        Assert.Single(lengths);
        Assert.InRange(lengths[0], 890, 900); // simulated backfill fills the 15-minute window
        var start = history.GetProperty("start").GetInt64();
        Assert.InRange(start, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 905, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 880);

        await client.ReceiveAsync("snapshot");

        await client.SendAsync(new { type = "setInterval", interval = 60 });
        var clamped = await client.ReceiveAsync("interval", skip: "snapshot");
        Assert.Equal(10, clamped.GetProperty("interval").GetDouble());

        await client.SendAsync(new { type = "setInterval", interval = 2 });
        var interval = await client.ReceiveAsync("interval", skip: "snapshot");
        Assert.Equal(2, interval.GetProperty("interval").GetDouble());

        // Snapshots now arrive about every 2 s.
        await client.ReceiveAsync("snapshot");
        var before = DateTime.UtcNow;
        await client.ReceiveAsync("snapshot", TimeSpan.FromSeconds(5));
        Assert.InRange((DateTime.UtcNow - before).TotalSeconds, 1.5, 3.5);
    }

    [Fact]
    public async Task WrongTokenIsUnauthorizedWithClose4401()
    {
        await using var agent = await TestAgent.StartAsync();
        var token = await agent.PairAsync(DeviceId);
        var wrongToken = PairingCrypto.GenerateToken();
        Assert.NotEqual(token, wrongToken);

        await using var client = await TestClient.ConnectAsync(agent.Port, "/");
        await client.SendAsync(new { type = "hello", protocol = 1, deviceId = DeviceId, token = wrongToken, interval = 1, history = false });

        var error = await client.ReceiveAsync("error");
        Assert.Equal("unauthorized", error.GetProperty("code").GetString());
        Assert.Equal("This device isn't paired with RIG-TEST.", error.GetProperty("message").GetString());
        Assert.Equal(4401, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task RevokedDeviceIsUnauthorized()
    {
        await using var agent = await TestAgent.StartAsync();
        var token = await agent.PairAsync(DeviceId);

        Assert.NotNull(new DeviceStore(agent.State, TimeProvider.System).Revoke(DeviceId[..8]));

        await using var client = await TestClient.ConnectAsync(agent.Port, "/");
        await client.SendAsync(new { type = "hello", protocol = 1, deviceId = DeviceId, token, interval = 1, history = false });
        Assert.Equal("unauthorized", (await client.ReceiveAsync("error")).GetProperty("code").GetString());
        Assert.Equal(4401, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task SecondPairingRequestIsBusy()
    {
        await using var agent = await TestAgent.StartAsync();
        await using var first = await TestClient.ConnectAsync(agent.Port, "/");
        await first.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = "A", name = "First", model = "iPad" } });
        await first.ReceiveAsync("pair.challenge");

        await using var second = await TestClient.ConnectAsync(agent.Port, "/");
        await second.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = "B", name = "Second", model = "iPad" } });

        var rejected = await second.ReceiveAsync("pair.rejected");
        Assert.Equal("busy", rejected.GetProperty("reason").GetString());
        Assert.False(rejected.TryGetProperty("attemptsLeft", out _));
        Assert.Equal(4409, await second.ReceiveCloseAsync());

        // Closing the first connection frees the window.
        await first.CloseAsync();
        await using var third = await TestClient.ConnectAsync(agent.Port, "/");
        await third.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = "C", name = "Third", model = "iPad" } });
        await third.ReceiveAsync("pair.challenge", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FiveWrongCodesCloseWith4429()
    {
        await using var agent = await TestAgent.StartAsync();
        await using var client = await TestClient.ConnectAsync(agent.Port, "/");
        await client.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = DeviceId, name = "iPad", model = "iPad" } });
        await client.ReceiveAsync("pair.challenge");

        var badProof = Convert.ToBase64String(new byte[32]);
        for (var attemptsLeft = 4; attemptsLeft >= 1; attemptsLeft--)
        {
            await client.SendAsync(new { type = "pair.confirm", proof = badProof });
            Assert.Equal(attemptsLeft, (await client.ReceiveAsync("pair.rejected")).GetProperty("attemptsLeft").GetInt32());
        }

        await client.SendAsync(new { type = "pair.confirm", proof = badProof });
        Assert.Equal("too_many_attempts", (await client.ReceiveAsync("pair.rejected")).GetProperty("reason").GetString());
        Assert.Equal(4429, await client.ReceiveCloseAsync());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"type":"setInterval","interval":2}""")]
    [InlineData("""{"type":"pair.confirm","proof":"AAAA"}""")]
    public async Task MalformedOrUnexpectedMessagesAreBadRequest(string frame)
    {
        await using var agent = await TestAgent.StartAsync();
        await using var client = await TestClient.ConnectAsync(agent.Port, "/");

        await client.SendRawAsync(frame);

        Assert.Equal("bad_request", (await client.ReceiveAsync("error")).GetProperty("code").GetString());
        Assert.Equal(4400, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task OldProtocolIsUnsupported()
    {
        await using var agent = await TestAgent.StartAsync();
        await using var client = await TestClient.ConnectAsync(agent.Port, "/");

        await client.SendAsync(new { type = "hello", protocol = 0, deviceId = DeviceId, token = "x", interval = 1, history = false });

        Assert.Equal("unsupported_protocol", (await client.ReceiveAsync("error")).GetProperty("code").GetString());
        Assert.Equal(4400, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task NoHelloWithinTenSecondsTimesOutWith4408()
    {
        await using var agent = await TestAgent.StartAsync();
        await using var client = await TestClient.ConnectAsync(agent.Port, "/");
        var connected = DateTime.UtcNow;

        var error = await client.ReceiveAsync("error", TimeSpan.FromSeconds(15));

        Assert.Equal("timeout", error.GetProperty("code").GetString());
        Assert.InRange((DateTime.UtcNow - connected).TotalSeconds, 9, 12);
        Assert.Equal(4408, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task PlainHttpsRequestIsToldToUpgrade()
    {
        await using var agent = await TestAgent.StartAsync();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler);

        using var root = await http.GetAsync(new Uri($"https://127.0.0.1:{agent.Port}/"));
        using var other = await http.GetAsync(new Uri($"https://127.0.0.1:{agent.Port}/metrics"));

        Assert.Equal(HttpStatusCode.UpgradeRequired, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task ShutdownClosesSessionsWith1001()
    {
        var agent = await TestAgent.StartAsync();
        try
        {
            var token = await agent.PairAsync(DeviceId);
            await using var client = await TestClient.ConnectAsync(agent.Port, "/");
            await client.SendAsync(new { type = "hello", protocol = 1, deviceId = DeviceId, token, interval = 1, history = false });
            await client.ReceiveAsync("welcome");

            var stopping = agent.App.StopAsync();
            Assert.Equal(1001, await client.ReceiveCloseAsync(skipMessages: true));
            await stopping;
        }
        finally
        {
            await agent.DisposeAsync();
        }
    }

    private sealed class RecordingDisplay : IPairingCodeDisplay
    {
        private readonly List<PairingCodeNotice> notices = [];

        public IReadOnlyList<PairingCodeNotice> Notices
        {
            get
            {
                lock (notices)
                    return notices.ToList();
            }
        }

        public void ShowCode(PairingCodeNotice notice)
        {
            lock (notices)
                notices.Add(notice);
        }

        public void PairingEnded(PairingDeviceInfo device, PairingOutcome outcome)
        {
        }
    }

    private sealed class TestAgent : IAsyncDisposable
    {
        private readonly TempTree tree;
        private bool disposed;

        private TestAgent(TempTree tree, WebApplication app, int port, RecordingDisplay display)
        {
            this.tree = tree;
            App = app;
            Port = port;
            Display = display;
        }

        public WebApplication App { get; }
        public int Port { get; }
        public RecordingDisplay Display { get; }
        public StateDirectory State => App.Services.GetRequiredService<StateDirectory>();
        public AgentCertificate Certificate => App.Services.GetRequiredService<AgentCertificate>();
        public AgentIdentity Identity => App.Services.GetRequiredService<AgentIdentity>();

        public static async Task<TestAgent> StartAsync()
        {
            var tree = new TempTree();
            var display = new RecordingDisplay();
            var port = FreePort();
            var options = new AgentRunOptions
            {
                Name = "RIG-TEST",
                Port = port,
                State = new StateDirectory(Path.Combine(tree.Root, "state")),
                Simulate = true,
                Discovery = "off",
                MinimumLogLevel = LogLevel.Warning,
            };
            var app = AgentHost.Build(options, services =>
            {
                services.RemoveAll<IPairingCodeDisplay>();
                services.AddSingleton<IPairingCodeDisplay>(display);
            });
            await app.StartAsync();
            return new TestAgent(tree, app, port, display);
        }

        /// <summary>Pairs a device on its own connection and returns the token.</summary>
        public async Task<string> PairAsync(string deviceId)
        {
            await using var client = await TestClient.ConnectAsync(Port, "/");
            await client.SendAsync(new { type = "pair.request", protocol = 1, device = new { id = deviceId, name = "Test iPad", model = "iPad" } });
            var nonce = Convert.FromBase64String((await client.ReceiveAsync("pair.challenge")).GetProperty("nonce").GetString()!);
            var code = Display.Notices[^1].Code;
            await client.SendAsync(new { type = "pair.confirm", proof = Convert.ToBase64String(PairingCrypto.ComputeClientProof(code, nonce, client.ServerFingerprint!, deviceId)) });
            var token = (await client.ReceiveAsync("pair.accepted")).GetProperty("token").GetString()!;
            await client.CloseAsync();
            return token;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            disposed = true;
            await App.StopAsync();
            await App.DisposeAsync();
            tree.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class TestClient : IAsyncDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
        private readonly ClientWebSocket socket = new();

        public byte[]? ServerFingerprint { get; private set; }

        public static async Task<TestClient> ConnectAsync(int port, string path)
        {
            var client = new TestClient();
            // Like the app before pairing: accept any certificate but record its fingerprint.
            client.socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                client.ServerFingerprint = SHA256.HashData(certificate!.GetRawCertData());
                return true;
            };
            using var timeout = new CancellationTokenSource(DefaultTimeout);
            await client.socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{path}"), timeout.Token);
            return client;
        }

        public Task SendAsync(object message) => SendRawAsync(JsonSerializer.Serialize(message));

        public async Task SendRawAsync(string json)
        {
            using var timeout = new CancellationTokenSource(DefaultTimeout);
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
        }

        /// <summary>Receives the next message, which must have <paramref name="type"/> (messages of type <paramref name="skip"/> are skipped).</summary>
        public async Task<JsonElement> ReceiveAsync(string type, TimeSpan? timeout = null, string? skip = null)
        {
            using var cancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);
            while (true)
            {
                var message = await ReceiveMessageAsync(cancellation.Token)
                    ?? throw new InvalidOperationException($"Connection closed with {socket.CloseStatus} while waiting for {type}.");
                var actual = message.GetProperty("type").GetString();
                if (actual == skip && actual != type)
                    continue;
                Assert.True(actual == type, $"Expected {type}, got {message}");
                return message;
            }
        }

        /// <summary>Waits for the agent's close frame and returns its status code.</summary>
        public async Task<int?> ReceiveCloseAsync(bool skipMessages = false)
        {
            using var cancellation = new CancellationTokenSource(DefaultTimeout);
            while (await ReceiveMessageAsync(cancellation.Token) is { } message)
            {
                if (!skipMessages)
                    Assert.Fail($"Expected close, got {message}");
            }

            return (int?)socket.CloseStatus;
        }

        public async Task CloseAsync()
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(DefaultTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }

        private async Task<JsonElement?> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1 << 16];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                    return null;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    using var document = JsonDocument.Parse(stream.ToArray());
                    return document.RootElement.Clone();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await CloseAsync();
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            socket.Dispose();
        }
    }
}
