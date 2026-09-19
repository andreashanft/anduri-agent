using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anduri.Agent.Configuration;

/// <summary>The JSON config file. Every setting is optional; command-line flags override it.</summary>
/// <remarks>
/// Settable rather than init-only: the JSON source generator assigns every init-only property when it constructs
/// the object, which would replace the defaults below with false/null for keys missing from the file.
/// </remarks>
public sealed record AgentConfig
{
    /// <summary>Display name shown on the iPad. Defaults to the host name.</summary>
    public string? Name { get; set; }

    public int? Port { get; set; }

    public string? StateDir { get; set; }

    /// <summary><c>auto</c>, <c>avahi</c>, <c>dns-sd</c>, <c>managed</c> or <c>off</c>.</summary>
    public string Discovery { get; set; } = "auto";

    /// <summary>Use the simulated water-cooled rig instead of real sensors.</summary>
    public bool Simulate { get; set; }

    public SourcesConfig Sources { get; set; } = new();
    public CoolerControlConfig CoolerControl { get; set; } = new();
    public LiquidctlConfig Liquidctl { get; set; } = new();
    public DrivesConfig Drives { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();

    /// <summary>Per-sensor overrides keyed by sensor id, e.g. naming motherboard fan headers.</summary>
    public Dictionary<string, SensorOverride> Sensors { get; set; } = [];

    /// <summary>Prefix for <c>/proc</c> and <c>/sys</c>. Only useful for testing against a copied sysfs tree.</summary>
    public string? HostRoot { get; set; }
}

public sealed record SourcesConfig
{
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Nvidia { get; set; } = true;
    /// <summary>The GPU hotspot through NvAPI, an undocumented NVIDIA interface. Only used when <see cref="Nvidia"/> is on.</summary>
    public bool NvApi { get; set; } = true;
    public bool Aquacomputer { get; set; } = true;
    public bool Hwmon { get; set; } = true;
    public bool CoolerControl { get; set; } = true;
    public bool Liquidctl { get; set; } = true;
    public bool Drives { get; set; } = true;
    public bool Network { get; set; } = true;
}

public sealed record CoolerControlConfig
{
    /// <summary>coolercontrold listens on loopback port 11987; plain HTTP is allowed from loopback.</summary>
    public string Url { get; set; } = "http://127.0.0.1:11987";

    /// <summary>A read-only access token (<c>cc_…</c>) created in CoolerControl under Access Protection. Preferred.</summary>
    public string? Token { get; set; }

    public string Username { get; set; } = "CCAdmin";

    /// <summary>The CoolerControl password, used for a session login when no token is set.</summary>
    public string? Password { get; set; }

    /// <summary>Also report CPU, GPU and hwmon devices, which the agent otherwise reads directly.</summary>
    public bool IncludeAllDevices { get; set; }

    /// <summary>Accept coolercontrold's self-signed certificate for https URLs.</summary>
    public bool AllowSelfSignedCertificate { get; set; } = true;
}

public sealed record LiquidctlConfig
{
    public string Path { get; set; } = "liquidctl";

    /// <summary>Seconds between <c>liquidctl --json status</c> calls. Only used while CoolerControl isn't available.</summary>
    public double Interval { get; set; } = 5;
}

public sealed record DrivesConfig
{
    /// <summary>If not empty, only these mount points are reported.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Mount points to skip. A trailing <c>/*</c> also skips everything below.</summary>
    public List<string> Exclude { get; set; } = ["/boot", "/boot/efi", "/efi"];
}

public sealed record NetworkConfig
{
    /// <summary>Interface for <c>net.down</c>/<c>net.up</c>. Defaults to the interface of the default route.</summary>
    public string? Interface { get; set; }
}

public sealed record SensorOverride
{
    /// <summary>
    /// Reports the sensor under another id, e.g. an Octo's <c>temp.octo.1</c> as the well-known <c>loop.coolant</c>.
    /// </summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Label { get; set; }
    public string? ShortLabel { get; set; }
    public bool Hidden { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

public static class AgentConfigLoader
{
    /// <summary>
    /// Loads <paramref name="explicitPath"/>, or the first existing default location, or returns the defaults.
    /// </summary>
    public static (AgentConfig Config, string? Path) Load(string? explicitPath)
    {
        var path = explicitPath ?? DefaultPaths().FirstOrDefault(File.Exists);
        if (path is null)
            return (new AgentConfig(), null);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file {path} doesn't exist.", path);

        try
        {
            // Unknown keys are errors so a typo like "coolercontrol" doesn't silently do nothing.
            var config = JsonSerializer.Deserialize(File.ReadAllBytes(path), ConfigJsonContext.Default.AgentConfig);
            return (config ?? new AgentConfig(), path);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Config file {path} is invalid: {ex.Message}", ex);
        }
    }

    public static IEnumerable<string> DefaultPaths()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return System.IO.Path.Combine(configHome, "anduri-agent", "config.json");
        yield return "/etc/anduri-agent/config.json";
    }
}
