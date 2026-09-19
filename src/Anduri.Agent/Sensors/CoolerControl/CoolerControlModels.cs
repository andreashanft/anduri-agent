using System.Text.Json.Serialization;

namespace Anduri.Agent.Sensors.CoolerControl;

// Shapes of coolercontrold's REST API (unchanged from 2.x to 5.0). Only the fields the agent uses.

internal sealed record CcDevicesResponse(List<CcDevice>? Devices);

internal sealed record CcDevice(string Uid, string? Name, string? Type, int TypeIndex, CcDeviceInfo? Info);

internal sealed record CcDeviceInfo(Dictionary<string, CcChannelInfo>? Channels, Dictionary<string, CcTempInfo>? Temps);

internal sealed record CcChannelInfo(string? Label);

internal sealed record CcTempInfo(string? Label, int? Number);

internal sealed record CcStatusResponse(List<CcDeviceStatus>? Devices);

internal sealed record CcDeviceStatus(string Uid, string? Type, int TypeIndex, List<CcStatus>? StatusHistory);

internal sealed record CcStatus(string? Timestamp, List<CcTempStatus>? Temps, List<CcChannelStatus>? Channels);

internal sealed record CcTempStatus(string Name, double? Temp);

internal sealed record CcChannelStatus(string Name, double? Rpm, double? Duty, double? Freq, double? Watts);

internal sealed record CcStatusRequest(bool All);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CcDevicesResponse))]
[JsonSerializable(typeof(CcStatusResponse))]
[JsonSerializable(typeof(CcStatusRequest))]
internal sealed partial class CoolerControlJsonContext : JsonSerializerContext;

// liquidctl --json status: [{"description": "...", "status": [{"key": "Liquid temperature", "value": 30.1, "unit": "°C"}]}]

internal sealed record LiquidctlDevice(string? Description, string? Bus, string? Address, List<LiquidctlStatusItem>? Status);

internal sealed record LiquidctlStatusItem(string Key, System.Text.Json.JsonElement Value, string? Unit);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<LiquidctlDevice>))]
internal sealed partial class LiquidctlJsonContext : JsonSerializerContext;
