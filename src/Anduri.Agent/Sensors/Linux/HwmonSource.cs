namespace Anduri.Agent.Sensors.Linux;

/// <summary>
/// Fans and temperatures of every other hwmon chip, mostly motherboard Super I/O chips (nct67xx, it87 …),
/// as <c>fan.&lt;chip&gt;.&lt;n&gt;</c> and <c>temp.&lt;chip&gt;.&lt;n&gt;</c>.
/// </summary>
public sealed class HwmonSource(HostPaths paths, TimeProvider time) : HwmonSourceBase(paths, time)
{
    // Read by dedicated sources, or noise on a desktop (ACPI zones often report a constant, batteries, Wi-Fi chips).
    private static readonly HashSet<string> SkippedChips = new(StringComparer.Ordinal)
    {
        "k10temp", "zenpower", "coretemp", "acpitz", "nvidia",
    };

    private static readonly string[] SkippedChipPrefixes = ["iwlwifi", "ucsi_source_psy", "hidpp_battery", "BAT", "ADP", "AC"];

    public override string Name => "hwmon";

    protected override IReadOnlyList<MappedChannel> Map(IReadOnlyList<HwmonChip> chips) => MapChips(chips);

    internal static IReadOnlyList<MappedChannel> MapChips(IReadOnlyList<HwmonChip> chips)
    {
        var result = new List<MappedChannel>();
        foreach (var chip in chips)
        {
            if (AquacomputerSource.Devices.ContainsKey(chip.Name) || SkippedChips.Contains(chip.Name) ||
                SkippedChipPrefixes.Any(prefix => chip.Name.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            var hardware = chip.DeviceModel ?? HwmonScanner.PrettyChipName(chip.Name);
            foreach (var channel in chip.Channels)
            {
                switch (channel.Type)
                {
                    case "fan":
                    {
                        var label = channel.Label ?? $"Fan {channel.Index}";
                        var descriptor = SensorDescriptor.Create($"fan.{chip.Instance}.{channel.Index}", label, SensorKind.Rpm) with
                        {
                            Hardware = hardware,
                            Label = label,
                        };
                        // Unconnected headers read 0; implausible values come from floating tach inputs.
                        result.Add(new MappedChannel(channel, descriptor, raw => raw is >= 0 and < 30_000 ? raw : null, IgnoreZeroUntilSeen: true));
                        break;
                    }
                    case "temp":
                    {
                        var label = channel.Label ?? $"Temp {channel.Index}";
                        var name = channel.Label is null ? $"{hardware} temperature {channel.Index}" : $"{label} temperature";
                        var descriptor = SensorDescriptor.Create($"temp.{chip.Instance}.{channel.Index}", name, SensorKind.Temperature) with
                        {
                            Hardware = hardware,
                            Label = label,
                        };
                        // Unconnected thermistor inputs typically read 0, -128, 127 or 255 °C.
                        result.Add(new MappedChannel(channel, descriptor, raw => raw is > -40_000 and < 125_000 ? raw / 1000.0 : null, IgnoreZeroUntilSeen: true));
                        break;
                    }
                }
            }
        }

        return result;
    }
}
