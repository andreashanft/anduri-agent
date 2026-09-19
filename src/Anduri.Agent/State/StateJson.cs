using System.Text.Json.Serialization;

namespace Anduri.Agent.State;

internal sealed record AgentStateFile(string Id);

internal sealed record DevicesFile(List<PairedDevice> Devices);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AgentStateFile))]
[JsonSerializable(typeof(DevicesFile))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
