using Anduri.Agent.Configuration;
using Anduri.Agent.Protocol;
using Anduri.Agent.Sensors;
using Anduri.Agent.Sensors.Simulated;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anduri.Agent.Tests;

public class HistoryBufferTests
{
    private static DateTimeOffset At(double unixSeconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));

    [Fact]
    public void SeriesAreColumnarWithNullsForMissingSeconds()
    {
        var history = new HistoryBuffer();
        history.Record(At(1758039100.2), new Dictionary<string, double> { ["cpu.temp"] = 41.2, ["cpu.load"] = 3.1 });
        history.Record(At(1758039101.4), new Dictionary<string, double> { ["cpu.temp"] = 41.5 });
        history.Record(At(1758039103.0), new Dictionary<string, double> { ["cpu.temp"] = 42.0, ["cpu.load"] = 3.3 });

        var message = history.ToMessage();

        Assert.Equal(1758039100, message.Start);
        Assert.Equal(1, message.Step);
        Assert.Equal([41.2, 41.5, null, 42.0], message.Series["cpu.temp"]);
        Assert.Equal([3.1, null, null, 3.3], message.Series["cpu.load"]);
        JsonAssert.SemanticallyEqual(
            """{"type":"history","start":1758039100,"step":1,"series":{"cpu.temp":[41.2,41.5,null,42.0],"cpu.load":[3.1,null,null,3.3]}}""",
            ProtocolJson.Serialize(message));
    }

    [Fact]
    public void LaterSampleInSameSecondWins()
    {
        var history = new HistoryBuffer();
        history.Record(At(100.1), new Dictionary<string, double> { ["a"] = 1 });
        history.Record(At(100.6), new Dictionary<string, double> { ["a"] = 2 });

        Assert.Equal([2.0], history.ToMessage().Series["a"]);
    }

    [Fact]
    public void HistoryIsBoundedToFifteenMinutes()
    {
        var history = new HistoryBuffer();
        for (var second = 0; second < 1000; second++)
            history.Record(At(5000 + second), new Dictionary<string, double> { ["a"] = second });

        var message = history.ToMessage();

        Assert.Equal(900, HistoryBuffer.DefaultCapacitySeconds);
        Assert.Equal(5000 + 1000 - 900, message.Start);
        Assert.Equal(900, message.Series["a"].Length);
        Assert.Equal(100, message.Series["a"][0]);
        Assert.Equal(999, message.Series["a"][^1]);
    }

    [Fact]
    public void GapsLongerThanTheWindowForgetOldSamples()
    {
        var history = new HistoryBuffer(capacitySeconds: 10);
        history.Record(At(100), new Dictionary<string, double> { ["old"] = 1, ["both"] = 1 });
        history.Record(At(150), new Dictionary<string, double> { ["both"] = 2 });

        var message = history.ToMessage();

        Assert.Equal(150, message.Start);
        Assert.Equal([2.0], message.Series["both"]);
        Assert.False(message.Series.ContainsKey("old"));
    }

    [Fact]
    public void AllSeriesHaveSameLengthAndFilterApplies()
    {
        var history = new HistoryBuffer();
        history.Record(At(10), new Dictionary<string, double> { ["a"] = 1 });
        history.Record(At(20), new Dictionary<string, double> { ["b"] = 1, ["c"] = 5 });

        var message = history.ToMessage(["a", "b"]);

        Assert.Equal(["a", "b"], message.Series.Keys.Order());
        Assert.All(message.Series.Values, series => Assert.Equal(11, series.Length));
    }

    [Fact]
    public void SamplesOlderThanWindowAreIgnored()
    {
        var history = new HistoryBuffer(capacitySeconds: 10);
        history.Record(At(100), new Dictionary<string, double> { ["a"] = 1 });
        history.Record(At(50), new Dictionary<string, double> { ["a"] = 99 });

        Assert.Equal([1.0], history.ToMessage().Series["a"]);
    }
}

public class SensorSamplerTests
{
    private sealed class FakeSource(string name, IReadOnlyList<SensorDescriptor> descriptors, IReadOnlyDictionary<string, double> values) : ISensorSource
    {
        public string Name => name;
        public int Reads { get; private set; }
        public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

        public ValueTask ReadAsync(IDictionary<string, double> into, CancellationToken cancellationToken)
        {
            Reads++;
            foreach (var (id, value) in values)
                into[id] = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BrokenSource : ISensorSource
    {
        public string Name => "broken";
        public int Reads { get; private set; }
        public IReadOnlyList<SensorDescriptor> Describe() => [SensorDescriptor.Create("broken.x", "X", SensorKind.Other)];

        public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
        {
            Reads++;
            throw new IOException("device went away");
        }
    }

    private static SensorDescriptor D(string id) => SensorDescriptor.Create(id, id, SensorKind.Temperature);

    [Fact]
    public async Task FailingSourceDoesNotBreakOthersAndIsRetriedLater()
    {
        var hub = new SensorHub();
        var broken = new BrokenSource();
        var good = new FakeSource("good", [D("cpu.temp")], new Dictionary<string, double> { ["cpu.temp"] = 41.234 });
        var sampler = new SensorSampler(hub, [broken, good], new AgentConfig(), TimeProvider.System, NullLogger<SensorSampler>.Instance);

        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(["cpu.temp"], hub.Catalog.Select(d => d.Id));
        Assert.Equal(41.23, hub.Latest!.Values["cpu.temp"]);
        Assert.Equal(1, broken.Reads); // backed off, not hammered every tick
        Assert.Equal(2, good.Reads);
    }

    [Fact]
    public async Task EarlierSourceWinsDuplicateIdsAndOverridesApply()
    {
        var hub = new SensorHub();
        var aqua = new FakeSource("aquacomputer", [D("loop.coolant"), D("temp.octo.1")], new Dictionary<string, double> { ["loop.coolant"] = 28, ["temp.octo.1"] = 27 });
        var cc = new FakeSource("coolercontrol", [D("loop.coolant"), D("cc.kraken.liquid")], new Dictionary<string, double> { ["loop.coolant"] = 99, ["cc.kraken.liquid"] = 30 });
        var config = new AgentConfig
        {
            Sensors = new Dictionary<string, SensorOverride>
            {
                ["cc.kraken.liquid"] = new() { Hidden = true },
                ["temp.octo.1"] = new() { Id = "loop.ambient", Label = "Air" },
            },
        };
        var sampler = new SensorSampler(hub, [aqua, cc], config, TimeProvider.System, NullLogger<SensorSampler>.Instance);

        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(["loop.coolant", "loop.ambient"], hub.Catalog.Select(d => d.Id));
        Assert.Equal(28, hub.Latest!.Values["loop.coolant"]);
        Assert.Equal(27, hub.Latest.Values["loop.ambient"]);
        Assert.Equal("Air", hub.Catalog[1].Label);
    }

    [Fact]
    public async Task ExplicitRenameBeatsAutomaticWellKnownId()
    {
        var hub = new SensorHub();
        var aqua = new FakeSource("aquacomputer", [D("loop.coolant")], new Dictionary<string, double> { ["loop.coolant"] = 28 });
        var hwmon = new FakeSource("hwmon", [D("temp.nct6799.3")], new Dictionary<string, double> { ["temp.nct6799.3"] = 31 });
        var config = new AgentConfig { Sensors = new() { ["temp.nct6799.3"] = new() { Id = "loop.coolant" } } };
        var sampler = new SensorSampler(hub, [aqua, hwmon], config, TimeProvider.System, NullLogger<SensorSampler>.Instance);

        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(31, Assert.Single(hub.Latest!.Values).Value);
    }

    [Fact]
    public async Task CatalogChangeRaisesEventOnlyWhenSensorsChange()
    {
        var hub = new SensorHub();
        var changes = 0;
        hub.CatalogChanged += _ => changes++;
        var sampler = new SensorSampler(hub, [new FakeSource("a", [D("x")], new Dictionary<string, double> { ["x"] = 1 })], new AgentConfig(), TimeProvider.System, NullLogger<SensorSampler>.Instance);

        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await sampler.SampleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task SimulatedRigMirrorsAppMock()
    {
        var source = new SimulatedRigSource(TimeProvider.System);
        var values = new Dictionary<string, double>();
        await source.ReadAsync(values, CancellationToken.None);
        var catalog = source.Describe().ToDictionary(d => d.Id);

        Assert.Equal(26, catalog.Count);
        Assert.Equal("ml", catalog["reservoir.leakshield.filled"].Unit);
        Assert.InRange(values["reservoir.leakshield.filled"], 209, 215);
        Assert.Equal(catalog.Keys.Order(), values.Keys.Order());
        Assert.Equal("Ryzen 9 7950X3D", catalog["cpu.load"].Hardware);
        Assert.Equal("16 cores · 32 threads", catalog["cpu.load"].Detail);
        Assert.Equal("Fan 280", catalog["fan.rad280"].ShortLabel);
        Assert.Equal(2520, catalog["gpu.clock"].Maximum);
        Assert.Equal(4000, catalog["drive.e"].Capacity);
        Assert.InRange(values["cpu.load"], 0.5, 15);
        Assert.InRange(values["cpu.temp"], 35, 47);
        Assert.InRange(values["gpu.temp"], 26, 36);
        Assert.InRange(values["loop.coolant"], 26, 30);
        Assert.InRange(values["loop.flow"], 75, 84);
        Assert.InRange(values["loop.pump"], 2350, 2470);
        Assert.InRange(values["fan.rad280"], 650, 900);

        var backfill = source.Backfill(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)).ToList();
        Assert.Equal(900, backfill.Count);
        Assert.All(backfill, entry => Assert.InRange(entry.Values["loop.coolant"], 24, 33));
    }
}
