using System.Text.Json;
using Anduri.Agent.Protocol;

namespace Anduri.Agent.State;

/// <summary>Who this agent is: the stable id plus the name, version and OS sent in <c>welcome</c> and the TXT record.</summary>
public sealed record AgentIdentity(string Id, string Name, string Version, string Os)
{
    public AgentInfo ToInfo() => new(Id, Name, Version, Os);

    public static string CurrentVersion { get; } = ReadVersion();

    public static string CurrentOs { get; } =
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        "linux";

    /// <summary>Loads the agent id from the state directory, creating a new UUID on first start.</summary>
    public static AgentIdentity LoadOrCreate(StateDirectory state, string name)
    {
        var id = TryLoadId(state.AgentFile);
        if (id is null)
        {
            id = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var json = JsonSerializer.SerializeToUtf8Bytes(new AgentStateFile(id), StateJsonContext.Default.AgentStateFile);
            state.WritePrivateFile(state.AgentFile, json);
        }

        return new AgentIdentity(id, name, CurrentVersion, CurrentOs);
    }

    private static string? TryLoadId(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), StateJsonContext.Default.AgentStateFile);
            return Guid.TryParse(file?.Id, out _) ? file!.Id : null;
        }
        catch (JsonException ex)
        {
            // Silently minting a new id would make every paired iPad treat this PC as a new one.
            throw new InvalidOperationException($"{path} is corrupt. Fix or delete it (deleting gives the agent a new id).", ex);
        }
    }

    private static string ReadVersion()
    {
        var version = typeof(AgentIdentity).Assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
