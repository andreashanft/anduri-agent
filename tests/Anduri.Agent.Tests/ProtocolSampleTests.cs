using Anduri.Agent.Protocol;
using Anduri.Agent.Sensors;

namespace Anduri.Agent.Tests;

public class ProtocolSampleTests
{
    public static TheoryData<string, Type> Samples => new()
    {
        { "client/hello.json", typeof(HelloMessage) },
        { "client/pair-request.json", typeof(PairRequestMessage) },
        { "client/pair-confirm.json", typeof(PairConfirmMessage) },
        { "client/set-interval.json", typeof(SetIntervalMessage) },
        { "agent/welcome.json", typeof(WelcomeMessage) },
        { "agent/catalog.json", typeof(CatalogMessage) },
        { "agent/history.json", typeof(HistoryMessage) },
        { "agent/snapshot.json", typeof(SnapshotMessage) },
        { "agent/interval.json", typeof(IntervalMessage) },
        { "agent/error.json", typeof(ErrorMessage) },
        { "agent/pair-challenge.json", typeof(PairChallengeMessage) },
        { "agent/pair-accepted.json", typeof(PairAcceptedMessage) },
        { "agent/pair-rejected.json", typeof(PairRejectedMessage) },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleRoundTripsThroughMessageType(string sample, Type expectedType)
    {
        var json = File.ReadAllText(Path.Combine(Repository.ProtocolDirectory, "samples", sample));

        var message = ProtocolJson.Deserialize(json);

        Assert.NotNull(message);
        Assert.IsType(expectedType, message);
        JsonAssert.SemanticallyEqual(json, ProtocolJson.Serialize(message));
    }

    [Fact]
    public void EverySampleFileIsCovered()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Repository.ProtocolDirectory, "samples"), "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(Repository.ProtocolDirectory, "samples"), f).Replace('\\', '/'))
            .Order()
            .ToList();
        var covered = Samples.Select(row => (string)row[0]).Order().ToList();

        Assert.Equal(files, covered);
    }

    [Fact]
    public void CatalogSampleKeepsOptionalFieldsAndKinds()
    {
        var json = File.ReadAllText(Path.Combine(Repository.ProtocolDirectory, "samples", "agent", "catalog.json"));
        var catalog = Assert.IsType<CatalogMessage>(ProtocolJson.Deserialize(json));

        var fan = catalog.Sensors.Single(s => s.Id == "fan.nct6799.2");
        Assert.Equal(SensorKind.Rpm, fan.Kind);
        Assert.Equal("CPU", fan.ShortLabel);
        Assert.Equal(2520, catalog.Sensors.Single(s => s.Id == "gpu.clock").Maximum);
        Assert.Equal(31.2, catalog.Sensors.Single(s => s.Id == "mem.ram").Capacity);
    }

    [Fact]
    public void HistoryNullsSurviveRoundTrip()
    {
        var history = new HistoryMessage(1758039100, 1, new Dictionary<string, double?[]> { ["cpu.temp"] = [41.2, null, 42] });

        var json = ProtocolJson.Serialize(history);

        Assert.Contains("[41.2,null,42]", json);
    }

    [Fact]
    public void NullOptionalDescriptorFieldsAreOmitted()
    {
        var catalog = new CatalogMessage([SensorDescriptor.Create("net.up", "Upload", SensorKind.Network) with { Label = "Up" }]);

        var json = ProtocolJson.Serialize(catalog);

        Assert.Equal("""{"type":"catalog","sensors":[{"id":"net.up","name":"Upload","kind":"network","unit":"B/s","label":"Up"}]}""", json);
    }

    [Fact]
    public void UnitsAreWrittenAsPlainUtf8()
    {
        var json = ProtocolJson.Serialize(new CatalogMessage([SensorDescriptor.Create("cpu.temp", "CPU", SensorKind.Temperature)]));

        Assert.Contains("\"unit\":\"°C\"", json);
    }

    [Fact]
    public void UnknownTypesAreIgnoredAndUnknownFieldsTolerated()
    {
        Assert.Null(ProtocolJson.Deserialize("""{"type":"future.thing","x":1}"""));

        var hello = Assert.IsType<HelloMessage>(ProtocolJson.Deserialize("""{"type":"hello","protocol":2,"deviceId":"a","token":"b","newField":{"nested":true}}"""));
        Assert.Equal(2, hello.Protocol);
        Assert.Null(hello.Interval);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"protocol":1}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"type":"hello","protocol":"one"}""")]
    [InlineData("""{"type":"setInterval","interval":"fast"}""")]
    public void MalformedFramesThrowFormatException(string json)
    {
        Assert.Throws<ProtocolFormatException>(() => ProtocolJson.Deserialize(json));
    }
}
