using System.Text.Json.Serialization;

namespace Anduri.Agent.Sensors;

/// <summary>Sensor kinds from the protocol's "Kinds and units" table.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorKind>))]
public enum SensorKind
{
    [JsonStringEnumMemberName("temperature")] Temperature,
    [JsonStringEnumMemberName("load")] Load,
    [JsonStringEnumMemberName("power")] Power,
    [JsonStringEnumMemberName("rpm")] Rpm,
    [JsonStringEnumMemberName("flow")] Flow,
    [JsonStringEnumMemberName("memory")] Memory,
    [JsonStringEnumMemberName("frequency")] Frequency,
    [JsonStringEnumMemberName("storage")] Storage,
    [JsonStringEnumMemberName("network")] Network,
    [JsonStringEnumMemberName("voltage")] Voltage,
    [JsonStringEnumMemberName("other")] Other,
}

/// <summary>One entry of the <c>catalog</c> message.</summary>
/// <remarks>Record equality is used to detect catalog changes, so keep every member a value-like type.</remarks>
public sealed record SensorDescriptor(string Id, string Name, SensorKind Kind, string Unit)
{
    public string? Hardware { get; init; }
    public string? Label { get; init; }
    public string? ShortLabel { get; init; }
    public double? Capacity { get; init; }
    public double? Maximum { get; init; }
    public string? Detail { get; init; }

    /// <summary>Creates a descriptor with the protocol's canonical unit for <paramref name="kind"/>.</summary>
    public static SensorDescriptor Create(string id, string name, SensorKind kind, string? unit = null) =>
        new(id, name, kind, unit ?? DefaultUnit(kind));

    public static string DefaultUnit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Power => "W",
        SensorKind.Rpm => "rpm",
        SensorKind.Flow => "l/h",
        SensorKind.Memory or SensorKind.Storage => "GB",
        SensorKind.Frequency => "MHz",
        SensorKind.Network => "B/s",
        SensorKind.Voltage => "V",
        _ => "",
    };
}
