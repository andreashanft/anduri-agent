using System.Globalization;
using System.Text;

namespace Anduri.Agent.Sensors.Linux;

/// <summary>Where procfs and sysfs live. Tests point this at a fixture tree.</summary>
public sealed record HostPaths(string Root)
{
    public static HostPaths Host { get; } = new("/");

    public string Proc(params string[] parts) => Path.Combine([Root, "proc", .. parts]);

    public string Sys(params string[] parts) => Path.Combine([Root, "sys", .. parts]);
}

/// <summary>Tolerant readers for procfs/sysfs attributes, which can vanish or fail (ENODATA, EACCES) at any time.</summary>
public static class SysFs
{
    public static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static long? TryReadLong(string path) =>
        TryReadText(path) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static ulong? TryReadULong(string path) =>
        TryReadText(path) is { } text && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

public static class SensorIds
{
    /// <summary>
    /// Turns free text into one id segment: lowercase ASCII letters and digits, other runs become "_".
    /// "Radiator 280" → "radiator_280", "/mnt/Games" → "mnt_games".
    /// </summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSeparator = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
                pendingSeparator = false;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                pendingSeparator = true;
            }
        }

        return builder.Length == 0 ? "unnamed" : builder.ToString();
    }

    /// <summary>Returns <paramref name="id"/>, or <c>id_2</c>, <c>id_3</c> … if it is already taken.</summary>
    public static string Unique(string id, ISet<string> taken)
    {
        var candidate = id;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{id}_{n}";
        return candidate;
    }
}
