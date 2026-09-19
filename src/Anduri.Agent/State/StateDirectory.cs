using System.Text;

namespace Anduri.Agent.State;

/// <summary>The directory holding the agent id, certificate and paired devices.</summary>
public sealed class StateDirectory
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public StateDirectory(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }
    public string AgentFile => System.IO.Path.Combine(Path, "agent.json");
    public string CertificateFile => System.IO.Path.Combine(Path, "certificate.pfx");
    public string DevicesFile => System.IO.Path.Combine(Path, "devices.json");
    public string DevicesLockFile => System.IO.Path.Combine(Path, "devices.lock");

    /// <summary>
    /// systemd's <c>$STATE_DIRECTORY</c> (set by <c>StateDirectory=</c>), then <c>$XDG_STATE_HOME/anduri-agent</c>,
    /// then <c>~/.local/state/anduri-agent</c>.
    /// </summary>
    public static string ResolveDefault()
    {
        // systemd passes a colon-separated list when a unit declares several state directories.
        var systemd = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(systemd))
            return systemd.Split(':', StringSplitOptions.RemoveEmptyEntries)[0];

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && System.IO.Path.IsPathRooted(xdg))
            return System.IO.Path.Combine(xdg, "anduri-agent");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".local", "state", "anduri-agent");
    }

    public void EnsureExists()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path);
            return;
        }

        if (!Directory.Exists(Path))
            Directory.CreateDirectory(Path, PrivateDirectory);
    }

    /// <summary>Writes a file readable only by the agent's user, atomically replacing any previous version.</summary>
    public void WritePrivateFile(string path, ReadOnlySpan<byte> contents)
    {
        EnsureExists();
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFile;

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        // UnixCreateMode only applies to new files, so fix the mode of a leftover temp file too.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, PrivateFile);
        File.Move(temp, path, overwrite: true);
    }

    public void WritePrivateFile(string path, string contents) => WritePrivateFile(path, Encoding.UTF8.GetBytes(contents));
}
