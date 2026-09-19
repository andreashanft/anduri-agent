using System.Text.Json.Serialization;
using Anduri.Agent.Sensors;

namespace Anduri.Agent.Protocol;

/// <summary>Base of every protocol message. <see cref="Type"/> is written first and read by <see cref="ProtocolJson"/>.</summary>
public abstract record ProtocolMessage
{
    public abstract string Type { get; }
}

public sealed record AgentInfo(string Id, string Name, string Version, string Os);

// Client → agent. Fields are nullable because they come from the network; the session validates them.

public sealed record HelloMessage : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Hello;
    public int? Protocol { get; init; }
    public string? DeviceId { get; init; }
    public string? Token { get; init; }
    public double? Interval { get; init; }
    public bool? History { get; init; }
}

public sealed record PairingDevice
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Model { get; init; }
}

public sealed record PairRequestMessage : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.PairRequest;
    public int? Protocol { get; init; }
    public PairingDevice? Device { get; init; }
}

public sealed record PairConfirmMessage : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.PairConfirm;
    public string? Proof { get; init; }
}

public sealed record SetIntervalMessage : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.SetInterval;
    public double? Interval { get; init; }
}

// Agent → client.

public sealed record WelcomeMessage(int Protocol, AgentInfo Agent, double Interval) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Welcome;
}

public sealed record CatalogMessage(IReadOnlyList<SensorDescriptor> Sensors) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Catalog;
}

/// <summary>Columnar history: value <c>i</c> of each series belongs to <c>Start + i × Step</c>.</summary>
public sealed record HistoryMessage(long Start, double Step, IReadOnlyDictionary<string, double?[]> Series) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.History;
}

public sealed record SnapshotMessage(double T, IReadOnlyDictionary<string, double> Values) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Snapshot;
}

public sealed record IntervalMessage(double Interval) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Interval;
}

public sealed record ErrorMessage(string Code, string Message) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.Error;
}

public sealed record PairChallengeMessage(string Nonce, int ExpiresIn, AgentInfo Agent) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.PairChallenge;
}

public sealed record PairAcceptedMessage(string Token, string Proof, AgentInfo Agent) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.PairAccepted;
}

public sealed record PairRejectedMessage(string Reason, int? AttemptsLeft = null) : ProtocolMessage
{
    [JsonPropertyName("type"), JsonPropertyOrder(-1)]
    public override string Type => MessageTypes.PairRejected;
}
