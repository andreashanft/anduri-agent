using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Anduri.Agent.Configuration;
using Anduri.Agent.Sensors.Linux;

namespace Anduri.Agent.Sensors.CoolerControl;

/// <summary>
/// Sensors from the CoolerControl daemon (coolercontrold) REST API as <c>cc.&lt;device&gt;.&lt;channel&gt;</c>,
/// plus <c>loop.coolant</c>, <c>loop.flow</c> and <c>loop.pump</c> for channels that clearly are those.
/// </summary>
/// <remarks>
/// Since CoolerControl 4.0 every data endpoint needs authentication: either a bearer access token
/// (<c>cc_…</c>, created under Access Protection; read-only is enough) or a session cookie from
/// <c>POST /login</c> with HTTP Basic credentials for the user <c>CCAdmin</c>. Older daemons need none.
/// </remarks>
public sealed partial class CoolerControlSource : ISensorSource, IDisposable
{
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromMinutes(1);

    // By default only devices the agent can't read itself; CPU, GPU and hwmon devices would duplicate other sources.
    private static readonly HashSet<string> DefaultDeviceTypes = new(StringComparer.OrdinalIgnoreCase) { "Liquidctl", "CustomSensors", "ServicePlugin" };

    private readonly CoolerControlConfig config;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly HttpClient http;
    private Dictionary<string, (CcDevice Device, string Slug)> devices = new(StringComparer.Ordinal);
    private DateTimeOffset nextDeviceRefresh = DateTimeOffset.MinValue;
    private bool useLegacyStatusPost;
    private bool loggedIn;
    // The session cookie from POST /login, sent explicitly so it doesn't depend on the handler's cookie container.
    private string? sessionCookie;
    private IReadOnlyList<SensorDescriptor> descriptors = [];
    private volatile bool working;

    public CoolerControlSource(CoolerControlConfig config, TimeProvider time, ILogger logger, HttpMessageHandler? handler = null)
    {
        this.config = config;
        this.time = time;
        this.logger = logger;
        http = new HttpClient(handler ?? CreateHandler(config), disposeHandler: true)
        {
            BaseAddress = new Uri(config.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("anduri-agent", "1.0"));
        if (!string.IsNullOrWhiteSpace(config.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token.Trim());
    }

    public string Name => "coolercontrol";

    /// <summary>coolercontrold samples once per second by default.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    /// <summary>Whether the last read succeeded. The liquidctl fallback stays quiet while this is true.</summary>
    public bool IsWorking => working;

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public async ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            if (now >= nextDeviceRefresh)
            {
                await RefreshDevicesAsync(cancellationToken);
                nextDeviceRefresh = now + DeviceRefreshInterval;
            }

            var status = await GetStatusAsync(cancellationToken);
            if (status.Devices?.Any(d => !devices.ContainsKey(d.Uid)) == true)
                nextDeviceRefresh = DateTimeOffset.MinValue;

            var sensors = MapStatus(status, devices, config.IncludeAllDevices);
            foreach (var sensor in sensors)
                values[sensor.Descriptor.Id] = sensor.Value;

            var described = sensors.Select(s => s.Descriptor).ToList();
            if (!descriptors.SequenceEqual(described))
                descriptors = described;
            working = true;
        }
        catch
        {
            working = false;
            throw;
        }
    }

    internal sealed record MappedValue(SensorDescriptor Descriptor, double Value);

    /// <summary>Maps the latest status sample of every included device to sensors.</summary>
    internal static IReadOnlyList<MappedValue> MapStatus(
        CcStatusResponse status,
        IReadOnlyDictionary<string, (CcDevice Device, string Slug)> devices,
        bool includeAllDevices)
    {
        var result = new List<MappedValue>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        void Emit(SensorDescriptor descriptor, double value)
        {
            if (taken.Add(descriptor.Id))
                result.Add(new MappedValue(descriptor, value));
        }

        foreach (var deviceStatus in status.Devices ?? [])
        {
            if (!devices.TryGetValue(deviceStatus.Uid, out var entry))
                continue;
            var (device, slug) = entry;
            if (!includeAllDevices && !DefaultDeviceTypes.Contains(device.Type ?? ""))
                continue;
            if (deviceStatus.StatusHistory is not { Count: > 0 } history)
                continue;

            var latest = history[^1];
            var hardware = device.Name ?? device.Type ?? "CoolerControl device";

            foreach (var temp in latest.Temps ?? [])
            {
                if (temp.Temp is not { } value)
                    continue;
                var label = device.Info?.Temps?.GetValueOrDefault(temp.Name)?.Label ?? temp.Name;
                var descriptor = SensorDescriptor.Create($"cc.{slug}.{SensorIds.Slug(temp.Name)}", $"{hardware} {label}", SensorKind.Temperature) with
                {
                    Hardware = hardware,
                    Label = label,
                };
                if (IsCoolant(temp.Name, label))
                    Emit(SensorDescriptor.Create("loop.coolant", "Coolant temperature", SensorKind.Temperature) with { Hardware = hardware, Label = "Water" }, value);
                Emit(descriptor, value);
            }

            foreach (var channel in latest.Channels ?? [])
            {
                var label = device.Info?.Channels?.GetValueOrDefault(channel.Name)?.Label ?? channel.Name;
                var baseId = $"cc.{slug}.{SensorIds.Slug(channel.Name)}";
                var isFlow = channel.Name.Equals("flow", StringComparison.OrdinalIgnoreCase);
                var isPump = channel.Name.Equals("pump", StringComparison.OrdinalIgnoreCase) || label.Equals("pump", StringComparison.OrdinalIgnoreCase);
                var primaryTaken = false;

                string NextId(string suffix)
                {
                    if (primaryTaken)
                        return $"{baseId}.{suffix}";
                    primaryTaken = true;
                    return baseId;
                }

                if (channel.Rpm is { } rpm)
                {
                    if (isFlow)
                    {
                        // CoolerControl passes liquidctl's Aquacomputer flow sensor through the rpm field as l/h.
                        Emit(SensorDescriptor.Create("loop.flow", "Coolant flow", SensorKind.Flow) with { Hardware = hardware, Label = "Flow" }, rpm);
                        Emit(SensorDescriptor.Create(NextId("flow"), $"{hardware} flow", SensorKind.Flow) with { Hardware = hardware, Label = "Flow" }, rpm);
                    }
                    else
                    {
                        if (isPump)
                            Emit(SensorDescriptor.Create("loop.pump", "Pump", SensorKind.Rpm) with { Hardware = hardware, Label = "Pump" }, rpm);
                        Emit(SensorDescriptor.Create(NextId("rpm"), $"{hardware} {label}", SensorKind.Rpm) with { Hardware = hardware, Label = label }, rpm);
                    }
                }

                if (channel.Duty is { } duty)
                {
                    // "CPU Load" and "GPU Load" channels carry utilisation in the duty field.
                    var isLoad = channel.Name.EndsWith("Load", StringComparison.OrdinalIgnoreCase);
                    var descriptor = isLoad
                        ? SensorDescriptor.Create(NextId("duty"), $"{hardware} {label}", SensorKind.Load) with { Hardware = hardware, Label = label }
                        : SensorDescriptor.Create(NextId("duty"), $"{hardware} {label} duty", SensorKind.Other, "%") with { Hardware = hardware, Label = label };
                    Emit(descriptor, duty);
                }

                if (channel.Watts is { } watts)
                    Emit(SensorDescriptor.Create(NextId("watts"), $"{hardware} {label} power", SensorKind.Power) with { Hardware = hardware, Label = label }, watts);

                if (channel.Freq is { } freq)
                    Emit(SensorDescriptor.Create(NextId("freq"), $"{hardware} {label} frequency", SensorKind.Frequency) with { Hardware = hardware, Label = label }, freq);
            }
        }

        return result;
    }

    internal static Dictionary<string, (CcDevice Device, string Slug)> IndexDevices(IEnumerable<CcDevice> list)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, (CcDevice, string)>(StringComparer.Ordinal);
        foreach (var device in list)
        {
            var slug = SensorIds.Unique(SensorIds.Slug(device.Name ?? $"{device.Type}{device.TypeIndex}"), slugs);
            result[device.Uid] = (device, slug);
        }

        return result;
    }

    private static bool IsCoolant(string name, string label) =>
        name is "liquid" or "water" ||
        label.Contains("coolant", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("liquid", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("water", StringComparison.OrdinalIgnoreCase);

    private async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "devices"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync(CoolerControlJsonContext.Default.CcDevicesResponse, cancellationToken);
        devices = IndexDevices(body?.Devices ?? []);
    }

    private async Task<CcStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!useLegacyStatusPost)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "status"), cancellationToken);
            // CoolerControl 2.x only has POST /status.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                useLegacyStatusPost = true;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync(CoolerControlJsonContext.Default.CcStatusResponse, cancellationToken) ?? new CcStatusResponse([]);
            }
        }

        using var legacy = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "status") { Content = JsonContent.Create(new CcStatusRequest(false), CoolerControlJsonContext.Default.CcStatusRequest) },
            cancellationToken);
        legacy.EnsureSuccessStatusCode();
        return await legacy.Content.ReadFromJsonAsync(CoolerControlJsonContext.Default.CcStatusResponse, cancellationToken) ?? new CcStatusResponse([]);
    }

    /// <summary>Sends a request, logging in with the password once when the daemon asks for authentication.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = WithSession(createRequest());
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SensorSourceUnavailableException($"coolercontrold isn't reachable at {config.Url} ({ex.Message})", ex);
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;
        response.Dispose();

        if (!string.IsNullOrWhiteSpace(config.Token))
            throw new SensorSourceUnavailableException("coolercontrold rejected the access token in coolerControl.token");
        if (string.IsNullOrEmpty(config.Password))
            throw new SensorSourceUnavailableException("coolercontrold requires authentication; set coolerControl.token (a read-only access token) or coolerControl.password");

        // A 401 after a successful login means the session expired or the password changed: log in once more.
        loggedIn = false;
        await LoginAsync(cancellationToken);
        using var retry = WithSession(createRequest());
        return await http.SendAsync(retry, cancellationToken);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "login");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SensorSourceUnavailableException("coolercontrold rejected the username or password in the config");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("coolercontrold is throttling logins after failed attempts");
        response.EnsureSuccessStatusCode();
        sessionCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0].Trim()).FirstOrDefault(c => c.StartsWith("cc=", StringComparison.Ordinal))
            : null;
        if (sessionCookie is null)
            throw new InvalidOperationException("coolercontrold accepted the login but sent no session cookie");
        if (!loggedIn)
            LogLoggedIn(logger, config.Url);
        loggedIn = true;
    }

    private HttpRequestMessage WithSession(HttpRequestMessage request)
    {
        if (sessionCookie is not null)
            request.Headers.Add("Cookie", sessionCookie);
        return request;
    }

    private static SocketsHttpHandler CreateHandler(CoolerControlConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        // coolercontrold 4.0+ serves HTTPS with its own self-signed certificate. It normally listens on
        // loopback only, where there is nobody in the middle to protect against.
        if (config.AllowSelfSignedCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    public void Dispose() => http.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in to coolercontrold at {Url}")]
    private static partial void LogLoggedIn(ILogger logger, string url);
}
