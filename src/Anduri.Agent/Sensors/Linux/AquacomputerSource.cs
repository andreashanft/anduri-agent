using System.Text.RegularExpressions;

namespace Anduri.Agent.Sensors.Linux;

/// <summary>
/// Aquacomputer devices through the kernel's <c>aquacomputer_d5next</c> hwmon driver.
/// </summary>
/// <remarks>
/// Labels and units follow drivers/hwmon/aquacomputer_d5next.c: temperatures in m°C, flow on <c>fanN_input</c> in dL/h
/// (label ends in "[dL/h]"), power in µW, voltages in mV. Unconnected temperature sensors fail with ENODATA.
/// </remarks>
public sealed partial class AquacomputerSource(HostPaths paths, TimeProvider time) : HwmonSourceBase(paths, time)
{
    /// <summary>hwmon <c>name</c> values of the driver and the product names.</summary>
    public static readonly IReadOnlyDictionary<string, string> Devices = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["d5next"] = "Aquacomputer D5 Next",
        ["octo"] = "Aquacomputer Octo",
        ["quadro"] = "Aquacomputer Quadro",
        ["farbwerk"] = "Aquacomputer Farbwerk",
        ["farbwerk360"] = "Aquacomputer Farbwerk 360",
        ["highflownext"] = "Aquacomputer High Flow Next",
        ["highflow"] = "Aquacomputer High Flow",
        ["leakshield"] = "Aquacomputer Leakshield",
        ["aquaero"] = "Aquacomputer Aquaero",
        ["aquastreamxt"] = "Aquacomputer Aquastream XT",
        ["aquastreamultimate"] = "Aquacomputer Aquastream Ultimate",
        ["poweradjust3"] = "Aquacomputer Poweradjust 3",
    };

    // highflownext power1 without an external sensor: -ENODATA stored in a u32, read back as 4294967235 µW.
    private const long DriverErrorSentinel = 4_294_967_000;

    public override string Name => "aquacomputer";

    protected override IReadOnlyList<MappedChannel> Map(IReadOnlyList<HwmonChip> chips) => MapChips(chips);

    internal static IReadOnlyList<MappedChannel> MapChips(IReadOnlyList<HwmonChip> chips)
    {
        var result = new List<MappedChannel>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var aquaChips = chips.Where(c => Devices.ContainsKey(c.Name)).ToList();

        // Fan ids are fan.aqua.<n> for the first device with fans, fan.aqua2.<n> for the next …
        // Fan controllers (numbered "Fan N speed") go first, so an Octo keeps fan.aqua.* next to a D5 Next's single fan header.
        var fanGroups = aquaChips
            .Where(c => c.Channels.Any(ch => ch.Type == "fan" && FanSpeed().IsMatch(ch.Label ?? "")))
            .OrderBy(c => c.Channels.Any(ch => ch.Type == "fan" && ch.Label is { } l && FanSpeed().Match(l).Groups["n"].Success) ? 0 : 1)
            .Select((chip, index) => (chip.Instance, Group: index == 0 ? "aqua" : $"aqua{index + 1}"))
            .ToDictionary(g => g.Instance, g => g.Group, StringComparer.Ordinal);

        foreach (var chip in aquaChips)
        {
            var hardware = Devices[chip.Name];
            var prefix = chip.Instance;
            var fanGroup = fanGroups.GetValueOrDefault(chip.Instance, "aqua");

            // Takes the well-known id if no earlier channel has it, otherwise the device-specific fallback.
            string Claim(string wellKnown, string fallback) =>
                taken.Add(wellKnown) ? wellKnown : SensorIds.Unique(fallback, taken);

            void Add(HwmonChannel channel, string id, string name, SensorKind kind, Func<long, double?> convert,
                string? label = null, string? unit = null, bool ignoreZero = false) =>
                result.Add(new MappedChannel(channel, SensorDescriptor.Create(id, name, kind, unit) with { Hardware = hardware, Label = label }, convert, ignoreZero));

            foreach (var channel in chip.Channels)
            {
                var label = channel.Label ?? channel.Key;
                switch (channel.Type)
                {
                    case "temp":
                        MapTemperature(channel, label);
                        break;
                    case "fan":
                        MapFan(channel, label);
                        break;
                    case "power" when label is "Pump power" or "Dissipated power":
                        var powerSlug = label == "Pump power" ? "pump" : "dissipated";
                        Add(channel, SensorIds.Unique($"power.{prefix}.{powerSlug}", taken), label, SensorKind.Power,
                            raw => raw >= DriverErrorSentinel ? null : raw / 1_000_000.0, label: label == "Pump power" ? "Pump" : "Dissipated");
                        break;
                    case "in" when label.StartsWith('+'):
                        // "+12V voltage" → voltage.d5next.12v, label "+12V"
                        var rail = label.Replace(" voltage", "", StringComparison.Ordinal);
                        Add(channel, SensorIds.Unique($"voltage.{prefix}.{SensorIds.Slug(rail)}", taken), $"{hardware} {rail}",
                            SensorKind.Voltage, MilliToUnit, label: rail);
                        break;
                }
            }

            void MapTemperature(HwmonChannel channel, string label)
            {
                if (label == "Coolant temp")
                {
                    Add(channel, Claim("loop.coolant", $"temp.{prefix}.coolant"), "Coolant temperature", SensorKind.Temperature, MilliToUnit, label: "Water");
                }
                else if (label is "External sensor" or "External temp")
                {
                    Add(channel, Claim("loop.ambient", $"temp.{prefix}.external"), "Ambient temperature", SensorKind.Temperature, MilliToUnit, label: "Air");
                }
                else if (NumberedSensor().Match(label) is { Success: true } numbered)
                {
                    var kind = numbered.Groups["kind"].Value;
                    var segment = (kind.StartsWith("Calc", StringComparison.Ordinal) ? "calc" : kind.StartsWith("Virtual", StringComparison.Ordinal) ? "virtual" : "")
                        + numbered.Groups["n"].Value;
                    Add(channel, SensorIds.Unique($"temp.{prefix}.{segment}", taken), $"{hardware} {label.ToLowerInvariant()}",
                        SensorKind.Temperature, MilliToUnit, label: label);
                }
                else
                {
                    Add(channel, SensorIds.Unique($"temp.{prefix}.{SensorIds.Slug(label)}", taken), $"{hardware} {label}",
                        SensorKind.Temperature, MilliToUnit, label: label);
                }
            }

            void MapFan(HwmonChannel channel, string label)
            {
                // Values the user typed into aquasuite, not measurements.
                if (label.StartsWith("User-Provided", StringComparison.Ordinal))
                    return;

                if (label.EndsWith("[dL/h]", StringComparison.Ordinal))
                {
                    Add(channel, Claim("loop.flow", $"flow.{prefix}.{channel.Index}"), "Coolant flow", SensorKind.Flow, raw => raw / 10.0, label: "Flow");
                }
                else if (label.EndsWith("[L/h]", StringComparison.OrdinalIgnoreCase))
                {
                    Add(channel, Claim("loop.flow", $"flow.{prefix}.{channel.Index}"), "Coolant flow", SensorKind.Flow, raw => raw, label: "Flow");
                }
                else if (label == "Pump speed")
                {
                    Add(channel, Claim("loop.pump", $"pump.{prefix}"), "Pump", SensorKind.Rpm, raw => raw, label: "Pump");
                }
                else if (FanSpeed().Match(label) is { Success: true } fan)
                {
                    var n = fan.Groups["n"].Success ? fan.Groups["n"].Value : "1";
                    Add(channel, SensorIds.Unique($"fan.{fanGroup}.{n}", taken), $"{hardware} fan {n}", SensorKind.Rpm, raw => raw,
                        label: $"Fan {n}", ignoreZero: true);
                }
                else if (label == "Pressure [ubar]")
                {
                    Add(channel, SensorIds.Unique($"pressure.{prefix}", taken), "Loop pressure", SensorKind.Other, raw => raw / 1000.0, label: "Pressure", unit: "mbar");
                }
                else if (label == "Pressure [mbar]")
                {
                    Add(channel, SensorIds.Unique($"pressure.{prefix}", taken), "Loop pressure", SensorKind.Other, raw => raw, label: "Pressure", unit: "mbar");
                }
                else if (label is "Reservoir Volume [ml]" or "Reservoir Filled [ml]")
                {
                    var filled = label.Contains("Filled", StringComparison.Ordinal);
                    Add(channel, SensorIds.Unique($"reservoir.{prefix}.{(filled ? "filled" : "volume")}", taken),
                        filled ? "Reservoir fill level" : "Reservoir volume", SensorKind.Other, raw => raw, label: filled ? "Filled" : "Volume", unit: "ml");
                }
                else if (label == "Water quality [%]")
                {
                    Add(channel, SensorIds.Unique($"quality.{prefix}", taken), "Water quality", SensorKind.Other, raw => raw, label: "Quality", unit: "%");
                }
                else if (label == "Conductivity [nS/cm]")
                {
                    Add(channel, SensorIds.Unique($"conductivity.{prefix}", taken), "Coolant conductivity", SensorKind.Other, raw => raw / 1000.0,
                        label: "Conductivity", unit: "µS/cm");
                }
            }
        }

        return result;
    }

    // "Sensor 3", "Virtual sensor 12", "Calc. virtual sensor 2"
    [GeneratedRegex(@"^(?<kind>Sensor|Virtual sensor|Calc\. virtual sensor) (?<n>\d+)$")]
    private static partial Regex NumberedSensor();

    // "Fan speed" (D5 Next, Aquastream) or "Fan 3 speed" (Octo, Quadro, Aquaero)
    [GeneratedRegex(@"^Fan(?: (?<n>\d+))? speed$")]
    private static partial Regex FanSpeed();
}
