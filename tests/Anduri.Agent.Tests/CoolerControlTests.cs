using System.Net;
using System.Text;
using System.Text.Json;
using Anduri.Agent.Configuration;
using Anduri.Agent.Sensors;
using Anduri.Agent.Sensors.CoolerControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anduri.Agent.Tests;

public class CoolerControlTests
{
    // Trimmed from coolercontrold 5.0 responses.
    private const string DevicesJson = """
        {"devices":[
          {"name":"NZXT Kraken X","type":"Liquidctl","type_index":1,"uid":"a1b2",
           "lc_info":{"driver_type":"KrakenX3","firmware_version":"2.1.0","unknown_asetek":false},
           "info":{"channels":{"pump":{"label":null,"speed_options":{"min_duty":20,"max_duty":100,"fixed_enabled":true,"extension":null},"lighting_modes":[],"lcd_modes":[],"lcd_info":null}},
                   "temps":{"liquid":{"label":"Liquid","number":1}},"temp_min":20,"temp_max":60}},
          {"name":"nct6799","type":"Hwmon","type_index":1,"uid":"c3d4",
           "info":{"channels":{"fan2":{"label":"CPU fan"}},"temps":{"temp1":{"label":"SYSTIN","number":1}}}},
          {"name":"AMD Ryzen 9 7950X3D","type":"CPU","type_index":1,"uid":"e5f6","info":{"channels":{},"temps":{}}}
        ]}
        """;

    private const string StatusJson = """
        {"devices":[
          {"type":"Liquidctl","type_index":1,"uid":"a1b2","status_history":[
            {"timestamp":"2026-09-16T21:15:33.123+02:00","temps":[{"name":"liquid","temp":30.9}],"channels":[{"name":"pump","rpm":2300,"duty":58.0}]},
            {"timestamp":"2026-09-16T21:15:34.123+02:00","temps":[{"name":"liquid","temp":31.2}],"channels":[{"name":"flow","rpm":142},{"name":"pump","rpm":2350,"duty":60.0},{"name":"fan","rpm":800}]}]},
          {"type":"Hwmon","type_index":1,"uid":"c3d4","status_history":[
            {"timestamp":"2026-09-16T21:15:34.123+02:00","temps":[{"name":"temp1","temp":33.0}],"channels":[{"name":"fan2","rpm":700}]}]},
          {"type":"CPU","type_index":1,"uid":"e5f6","status_history":[
            {"timestamp":"2026-09-16T21:15:34.123+02:00","temps":[{"name":"CPU Temp","temp":44.0}],"channels":[{"name":"CPU Load","duty":7.5}]}]}
        ]}
        """;

    private static Dictionary<string, (CcDevice Device, string Slug)> Devices() =>
        CoolerControlSource.IndexDevices(JsonSerializer.Deserialize(DevicesJson, CoolerControlJsonContext.Default.CcDevicesResponse)!.Devices!);

    private static CcStatusResponse Status() => JsonSerializer.Deserialize(StatusJson, CoolerControlJsonContext.Default.CcStatusResponse)!;

    [Fact]
    public void LiquidctlDeviceMapsLatestSampleToCcAndLoopIds()
    {
        var mapped = CoolerControlSource.MapStatus(Status(), Devices(), includeAllDevices: false).ToDictionary(m => m.Descriptor.Id);

        Assert.Equal(
            ["cc.nzxt_kraken_x.fan", "cc.nzxt_kraken_x.flow", "cc.nzxt_kraken_x.liquid", "cc.nzxt_kraken_x.pump", "cc.nzxt_kraken_x.pump.duty", "loop.coolant", "loop.flow", "loop.pump"],
            mapped.Keys.Order());
        Assert.Equal(31.2, mapped["loop.coolant"].Value);
        Assert.Equal(31.2, mapped["cc.nzxt_kraken_x.liquid"].Value);
        Assert.Equal("Liquid", mapped["cc.nzxt_kraken_x.liquid"].Descriptor.Label);
        Assert.Equal(2350, mapped["loop.pump"].Value);
        Assert.Equal(60, mapped["cc.nzxt_kraken_x.pump.duty"].Value);
        Assert.Equal("%", mapped["cc.nzxt_kraken_x.pump.duty"].Descriptor.Unit);
        Assert.Equal(142, mapped["loop.flow"].Value);
        Assert.Equal(SensorKind.Flow, mapped["cc.nzxt_kraken_x.flow"].Descriptor.Kind);
        Assert.Equal("NZXT Kraken X", mapped["loop.pump"].Descriptor.Hardware);
    }

    [Fact]
    public void AllDevicesIncludeHwmonAndCpuLoad()
    {
        var mapped = CoolerControlSource.MapStatus(Status(), Devices(), includeAllDevices: true).ToDictionary(m => m.Descriptor.Id);

        Assert.Equal(700, mapped["cc.nct6799.fan2"].Value);
        Assert.Equal("CPU fan", mapped["cc.nct6799.fan2"].Descriptor.Label);
        Assert.Equal(SensorKind.Load, mapped["cc.amd_ryzen_9_7950x3d.cpu_load"].Descriptor.Kind);
        Assert.Equal(7.5, mapped["cc.amd_ryzen_9_7950x3d.cpu_load"].Value);
    }

    [Fact]
    public async Task PasswordLoginIsUsedAfterUnauthorized()
    {
        var handler = new FakeDaemon(requireAuth: true);
        var config = new CoolerControlConfig { Url = "http://127.0.0.1:11987", Password = "secret" };
        using var source = new CoolerControlSource(config, TimeProvider.System, NullLogger.Instance, handler);
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);

        Assert.True(source.IsWorking);
        Assert.Equal(31.2, values["loop.coolant"]);
        var login = handler.Requests.Single(r => r.Path == "/login");
        Assert.Equal("POST", login.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("CCAdmin:secret")), login.Authorization);
        Assert.Contains(handler.Requests, r => r.Path == "/status" && r.Cookie == "cc=session");
    }

    [Fact]
    public async Task BearerTokenIsSentWithEveryRequest()
    {
        var handler = new FakeDaemon(requireAuth: true);
        var config = new CoolerControlConfig { Token = "cc_0123456789abcdef0123456789abcdef" };
        using var source = new CoolerControlSource(config, TimeProvider.System, NullLogger.Instance, handler);

        await source.ReadAsync(new Dictionary<string, double>(), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Path == "/login");
        Assert.All(handler.Requests, r => Assert.Equal("Bearer cc_0123456789abcdef0123456789abcdef", r.Authorization));
    }

    [Fact]
    public async Task MissingCredentialsMakeSourceUnavailable()
    {
        using var source = new CoolerControlSource(new CoolerControlConfig(), TimeProvider.System, NullLogger.Instance, new FakeDaemon(requireAuth: true));

        var error = await Assert.ThrowsAsync<SensorSourceUnavailableException>(() => source.ReadAsync(new Dictionary<string, double>(), CancellationToken.None).AsTask());

        Assert.Contains("token", error.Message);
        Assert.False(source.IsWorking);
    }

    [Fact]
    public async Task LegacyDaemonFallsBackToPostStatus()
    {
        var handler = new FakeDaemon(requireAuth: false, legacy: true);
        using var source = new CoolerControlSource(new CoolerControlConfig(), TimeProvider.System, NullLogger.Instance, handler);
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);

        Assert.Equal(2350, values["loop.pump"]);
        Assert.Contains(handler.Requests, r => r.Path == "/status" && r.Method == "POST");
    }

    [Fact]
    public void LiquidctlJsonStatusMaps()
    {
        const string json = """
            [{"bus":"hid","address":"/dev/hidraw3","description":"NZXT Kraken X (X53, X63 or X73)",
              "status":[{"key":"Liquid temperature","value":30.1,"unit":"°C"},{"key":"Pump speed","value":2000,"unit":"rpm"},
                        {"key":"Pump duty","value":60,"unit":"%"},{"key":"Firmware version","value":"6.2.0","unit":""}]},
             {"bus":"hid","address":"/dev/hidraw5","description":"Aquacomputer Octo",
              "status":[{"key":"Flow sensor","value":792,"unit":"dL/h"},{"key":"Fan 1 speed","value":812,"unit":"rpm"}]}]
            """;
        var devices = JsonSerializer.Deserialize(json, LiquidctlJsonContext.Default.ListLiquidctlDevice)!;

        var mapped = LiquidctlSource.Map(devices).ToDictionary(m => m.Descriptor.Id);

        Assert.Equal(30.1, mapped["loop.coolant"].Value);
        Assert.Equal(2000, mapped["loop.pump"].Value);
        Assert.Equal(79.2, mapped["loop.flow"].Value, precision: 6);
        Assert.Equal(60, mapped["lc.nzxt_kraken_x.pump_duty"].Value);
        Assert.Equal("Liquid", mapped["lc.nzxt_kraken_x.liquid_temperature"].Descriptor.Label);
        Assert.Equal(812, mapped["lc.aquacomputer_octo.fan_1_speed"].Value);
        Assert.DoesNotContain(mapped.Keys, id => id.Contains("firmware"));
    }

    private sealed record SeenRequest(string Method, string Path, string? Authorization, string? Cookie);

    /// <summary>Just enough of coolercontrold for the client: auth, /devices and /status.</summary>
    private sealed class FakeDaemon(bool requireAuth, bool legacy = false) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var authorization = request.Headers.Authorization?.ToString();
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
            Requests.Add(new SeenRequest(request.Method.Method, path, authorization, cookie));

            if (path == "/login")
            {
                var ok = authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("CCAdmin:secret"));
                var login = new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
                if (ok)
                    login.Headers.Add("Set-Cookie", "cc=session; Path=/; HttpOnly");
                return Task.FromResult(login);
            }

            var authorized = !requireAuth || authorization == "Bearer cc_0123456789abcdef0123456789abcdef" || cookie == "cc=session";
            if (!authorized)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":"Invalid credentials"}"""));

            return Task.FromResult((request.Method.Method, path) switch
            {
                ("GET", "/devices") => Json(HttpStatusCode.OK, DevicesJson),
                ("GET", "/status") when legacy => new HttpResponseMessage(HttpStatusCode.NotFound),
                ("GET", "/status") or ("POST", "/status") => Json(HttpStatusCode.OK, StatusJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
