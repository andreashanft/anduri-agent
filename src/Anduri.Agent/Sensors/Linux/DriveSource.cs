using System.Text;
using System.Text.RegularExpressions;
using Anduri.Agent.Configuration;

namespace Anduri.Agent.Sensors.Linux;

/// <summary>A mounted filesystem from <c>/proc/self/mounts</c>.</summary>
public sealed record MountEntry(string Device, string MountPoint, string FileSystem);

/// <summary>Total and free bytes of a mounted filesystem.</summary>
public readonly record struct FileSystemSpace(long TotalBytes, long FreeBytes);

/// <summary>Used space of real mounted filesystems as <c>drive.&lt;slug&gt;</c>.</summary>
public sealed partial class DriveSource : ISensorSource
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);

    // Whitelist rather than blacklist: new pseudo filesystems appear regularly, disk filesystems rarely.
    // Network filesystems are left out on purpose, because statvfs on a dead NFS server blocks.
    private static readonly HashSet<string> DiskFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "bcachefs", "zfs", "f2fs", "jfs", "reiserfs", "nilfs2",
        "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "hfsplus", "apfs",
    };

    private static readonly string[] SkippedPrefixes = ["/snap/", "/var/lib/snapd/", "/var/lib/docker/", "/var/lib/containers/", "/run/", "/proc/", "/sys/", "/dev/"];

    private readonly HostPaths paths;
    private readonly DrivesConfig config;
    private readonly TimeProvider time;
    private readonly Func<string, FileSystemSpace?> getSpace;
    private IReadOnlyList<(MountEntry Mount, SensorDescriptor Descriptor)> drives = [];
    private DateTimeOffset nextScan = DateTimeOffset.MinValue;

    /// <param name="getSpace">Reads the space of a mount point; tests pass a fake because statvfs can't be faked on a fixture tree.</param>
    public DriveSource(HostPaths paths, DrivesConfig config, TimeProvider time, Func<string, FileSystemSpace?>? getSpace = null)
    {
        this.paths = paths;
        this.config = config;
        this.time = time;
        this.getSpace = getSpace ?? ReadSpace;
    }

    public string Name => "drives";

    public IReadOnlyList<SensorDescriptor> Describe() => drives.Select(d => d.Descriptor).ToList();

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now >= nextScan)
        {
            nextScan = now + RescanInterval;
            var mounts = SysFs.TryReadText(paths.Proc("self", "mounts")) ?? SysFs.TryReadText(paths.Proc("mounts"))
                ?? throw new SensorSourceUnavailableException("/proc/self/mounts isn't readable");
            drives = BuildDrives(FilterMounts(ParseMounts(mounts), config));
        }

        foreach (var (mount, descriptor) in drives)
        {
            if (getSpace(mount.MountPoint) is { TotalBytes: > 0 } space)
                values[descriptor.Id] = (space.TotalBytes - space.FreeBytes) / MemorySource.BytesPerGb;
        }

        return ValueTask.CompletedTask;
    }

    internal static IReadOnlyList<MountEntry> ParseMounts(string text)
    {
        var result = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 3)
                continue;
            result.Add(new MountEntry(Unescape(fields[0]), Unescape(fields[1]), fields[2]));
        }

        return result;
    }

    /// <summary>Keeps real disk filesystems, applies include/exclude, and drops repeated mounts of the same device.</summary>
    internal static IReadOnlyList<MountEntry> FilterMounts(IReadOnlyList<MountEntry> mounts, DrivesConfig config)
    {
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MountEntry>();

        // Shortest mount point first, so a btrfs root wins over its subvolumes mounted at /home or /.snapshots.
        foreach (var mount in mounts.OrderBy(m => m.MountPoint.Length).ThenBy(m => m.MountPoint, StringComparer.Ordinal))
        {
            if (!DiskFileSystems.Contains(mount.FileSystem))
                continue;
            if (config.Include.Count > 0)
            {
                if (!config.Include.Any(pattern => MatchesPattern(mount.MountPoint, pattern)))
                    continue;
            }
            else if (SkippedPrefixes.Any(prefix => (mount.MountPoint + "/").StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (config.Exclude.Any(pattern => MatchesPattern(mount.MountPoint, pattern)))
                continue;

            // ZFS datasets share a pool but each has its own usage, so they aren't deduplicated.
            if (mount.FileSystem != "zfs" && !seenDevices.Add(mount.Device))
                continue;

            result.Add(mount);
        }

        return result.OrderBy(m => m.MountPoint == "/" ? "" : m.MountPoint, StringComparer.Ordinal).ToList();
    }

    private IReadOnlyList<(MountEntry, SensorDescriptor)> BuildDrives(IReadOnlyList<MountEntry> mounts)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(MountEntry, SensorDescriptor)>();
        foreach (var mount in mounts)
        {
            var id = SensorIds.Unique($"drive.{MountSlug(mount.MountPoint)}", taken);
            var capacity = getSpace(mount.MountPoint) is { TotalBytes: > 0 } space
                ? Math.Round(space.TotalBytes / MemorySource.BytesPerGb, 1)
                : (double?)null;
            var descriptor = SensorDescriptor.Create(id, DriveName(mount.MountPoint), SensorKind.Storage) with
            {
                Label = mount.MountPoint,
                Capacity = capacity,
                Hardware = DiskModel(mount.Device),
            };
            result.Add((mount, descriptor));
        }

        return result;
    }

    /// <summary>"/" → "root", "/home" → "home", "/mnt/Games" → "mnt_games".</summary>
    internal static string MountSlug(string mountPoint) => mountPoint == "/" ? "root" : SensorIds.Slug(mountPoint);

    internal static string DriveName(string mountPoint)
    {
        if (mountPoint == "/")
            return "System";
        var last = mountPoint.TrimEnd('/').Split('/').Last();
        return last.Length == 0 ? mountPoint : char.ToUpperInvariant(last[0]) + last[1..];
    }

    private string? DiskModel(string device)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal))
            return null;
        var name = PartitionSuffix().Replace(device["/dev/".Length..], "");
        return SysFs.TryReadText(paths.Sys("class", "block", name, "device", "model")) is { Length: > 0 } model ? model : null;
    }

    private static bool MatchesPattern(string mountPoint, string pattern) =>
        pattern.EndsWith("/*", StringComparison.Ordinal)
            ? mountPoint == pattern[..^2] || mountPoint.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(mountPoint, pattern.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/", StringComparison.Ordinal);

    private static FileSystemSpace? ReadSpace(string mountPoint)
    {
        try
        {
            // DriveInfo uses statvfs; TotalFreeSpace is f_bfree, so "used" matches df.
            var info = new DriveInfo(mountPoint);
            return new FileSystemSpace(info.TotalSize, info.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Mount fields escape space, tab, newline and backslash as octal: "\040".</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\'))
            return field;
        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                builder.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(field[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string text, int start) =>
        start + 3 <= text.Length && text.AsSpan(start, 3).IndexOfAnyExcept("01234567") < 0;

    // nvme0n1p2 → nvme0n1, mmcblk0p1 → mmcblk0, sda1 → sda.
    [GeneratedRegex(@"(?<=\d)p\d+$|(?<=^[sv]d[a-z]+)\d+$")]
    private static partial Regex PartitionSuffix();
}
