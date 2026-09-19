namespace Anduri.Agent;

public static class Executables
{
    /// <summary>Resolves a command name against <c>PATH</c> (plus the usual system directories), or checks an explicit path.</summary>
    public static string? Find(string nameOrPath)
    {
        if (nameOrPath.Contains('/'))
            return File.Exists(nameOrPath) ? nameOrPath : null;

        // systemd services get a minimal PATH, so the standard locations are always searched too.
        var searchPath = $"{Environment.GetEnvironmentVariable("PATH")}{Path.PathSeparator}/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        return searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, nameOrPath))
            .FirstOrDefault(File.Exists);
    }
}
