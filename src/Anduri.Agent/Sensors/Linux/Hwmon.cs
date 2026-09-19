using System.Text.RegularExpressions;

namespace Anduri.Agent.Sensors.Linux;

/// <summary>One <c>/sys/class/hwmon/hwmonN</c> device.</summary>
/// <param name="Name">The driver's chip name from the <c>name</c> attribute, e.g. "nct6799" or "octo".</param>
/// <param name="Instance">
/// Id segment that stays stable across reboots even though hwmonN numbers don't: the sanitized chip name,
/// with "_2", "_3" … for further chips of the same name (ordered by device path).
/// </param>
public sealed record HwmonChip(string Name, string Instance, string Directory, IReadOnlyList<HwmonChannel> Channels)
{
    public IEnumerable<HwmonChannel> OfType(string type) => Channels.Where(c => c.Type == type);

    /// <summary>A model name some drivers expose on the parent device, e.g. NVMe drives.</summary>
    public string? DeviceModel => SysFs.TryReadText(Path.Combine(Directory, "device", "model")) is { Length: > 0 } model ? model : null;
}

/// <summary>A channel such as <c>temp1</c> or <c>fan9</c>.</summary>
public sealed record HwmonChannel(string Type, int Index, string InputPath, string? Label)
{
    public string Key => $"{Type}{Index}";

    public long? ReadRaw() => SysFs.TryReadLong(InputPath);
}

public static partial class HwmonScanner
{
    /// <summary>Lists hwmon devices with their readable channel inputs.</summary>
    public static IReadOnlyList<HwmonChip> Scan(HostPaths paths)
    {
        var root = paths.Sys("class", "hwmon");
        if (!System.IO.Directory.Exists(root))
            return [];

        var found = new List<(string Name, string Directory, string SortKey)>();
        foreach (var entry in System.IO.Directory.EnumerateDirectories(root, "hwmon*"))
        {
            // Modern drivers put attributes in hwmonN itself, some older ones in hwmonN/device.
            var attributes = File.Exists(Path.Combine(entry, "name")) ? entry : Path.Combine(entry, "device");
            if (SysFs.TryReadText(Path.Combine(attributes, "name")) is not { Length: > 0 } name)
                continue;
            found.Add((name, attributes, DeviceSortKey(entry)));
        }

        var chips = new List<HwmonChip>();
        foreach (var group in found.GroupBy(f => f.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordinal = 0;
            foreach (var (name, directory, _) in group.OrderBy(f => f.SortKey, StringComparer.Ordinal))
            {
                ordinal++;
                var instance = SensorIds.Slug(name) + (ordinal > 1 ? $"_{ordinal}" : "");
                chips.Add(new HwmonChip(name, instance, directory, ScanChannels(directory)));
            }
        }

        return chips;
    }

    private static List<HwmonChannel> ScanChannels(string directory)
    {
        var channels = new List<HwmonChannel>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*_input"))
        {
            var match = InputPattern().Match(Path.GetFileName(file));
            if (!match.Success)
                continue;
            var type = match.Groups[1].Value;
            var index = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var label = SysFs.TryReadText(Path.Combine(directory, $"{type}{index}_label"));
            channels.Add(new HwmonChannel(type, index, file, string.IsNullOrWhiteSpace(label) ? null : label));
        }

        return channels.OrderBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Index).ToList();
    }

    // hwmonN numbering follows probe order, which can change between boots; the device path doesn't.
    private static string DeviceSortKey(string hwmonDirectory)
    {
        try
        {
            var target = new DirectoryInfo(hwmonDirectory).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? hwmonDirectory;
        }
        catch (IOException)
        {
            return hwmonDirectory;
        }
    }

    [GeneratedRegex(@"^(temp|fan|in|power|curr|humidity)(\d+)_input$")]
    private static partial Regex InputPattern();

    /// <summary>Human-readable chip names for the <c>hardware</c> field.</summary>
    public static string PrettyChipName(string name)
    {
        if (name.StartsWith("nct", StringComparison.Ordinal))
            return $"Nuvoton {name.ToUpperInvariant()}";
        if (name.StartsWith("it87", StringComparison.Ordinal) || (name.StartsWith("it8", StringComparison.Ordinal) && name.Length == 6))
            return $"ITE {name.ToUpperInvariant()}";
        if (name.StartsWith("f71", StringComparison.Ordinal))
            return $"Fintek {name.ToUpperInvariant()}";
        return name switch
        {
            "k10temp" or "zenpower" => "AMD CPU",
            "coretemp" => "Intel CPU",
            "nvme" => "NVMe drive",
            "amdgpu" => "AMD GPU",
            "acpitz" => "ACPI thermal zone",
            "spd5118" or "jc42" => "Memory module",
            "asus_ec_sensors" => "ASUS embedded controller",
            "asus_wmi_sensors" => "ASUS WMI sensors",
            "gigabyte_wmi" => "Gigabyte WMI sensors",
            "corsaircpro" => "Corsair Commander Pro",
            "nzxt_smart2" => "NZXT fan controller",
            "nzxtkraken3" => "NZXT Kraken",
            _ => name,
        };
    }
}
