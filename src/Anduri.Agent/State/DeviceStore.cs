using System.Text;
using System.Text.Json;
using Anduri.Agent.Pairing;

namespace Anduri.Agent.State;

/// <summary>A paired iPad. Only the SHA-256 hash of its token is stored.</summary>
public sealed record PairedDevice(
    string Id,
    string Name,
    string? Model,
    string TokenHash,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeen);

/// <summary>
/// <c>devices.json</c> in the state directory. Every operation re-reads the file under a lock file, because
/// <c>anduri-agent devices revoke</c> runs as a separate process while the service keeps running.
/// </summary>
public sealed class DeviceStore(StateDirectory state, TimeProvider time)
{
    // lastSeen is informational; writing it on every reconnect of a flaky Wi-Fi link isn't worth the disk writes.
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    public IReadOnlyList<PairedDevice> List()
    {
        using var _ = AcquireLock();
        return Read();
    }

    /// <summary>Adds a device, replacing an earlier pairing of the same device id.</summary>
    public void Add(PairedDevice device)
    {
        using var _ = AcquireLock();
        var devices = Read().Where(d => !string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        devices.Add(device);
        Write(devices);
    }

    /// <summary>Removes a device. Accepts the full id or a unique prefix of at least 4 characters.</summary>
    public PairedDevice? Revoke(string idOrPrefix)
    {
        using var _ = AcquireLock();
        var devices = Read().ToList();
        var match = devices.FirstOrDefault(d => string.Equals(d.Id, idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (match is null && idOrPrefix.Length >= 4)
        {
            var candidates = devices.Where(d => d.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 1)
                match = candidates[0];
        }

        if (match is null)
            return null;
        devices.Remove(match);
        Write(devices);
        return match;
    }

    /// <summary>Returns the device if <paramref name="token"/> is its current token, and records when it was seen.</summary>
    public PairedDevice? Authenticate(string deviceId, string token)
    {
        var hash = PairingCrypto.HashToken(token);
        if (hash is null)
            return null;

        using var _ = AcquireLock();
        var devices = Read().ToList();
        var index = devices.FindIndex(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;

        var device = devices[index];
        if (!PairingCrypto.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(device.TokenHash.ToLowerInvariant())))
            return null;

        var now = time.GetUtcNow();
        if (device.LastSeen is null || now - device.LastSeen >= LastSeenResolution)
        {
            device = device with { LastSeen = now };
            devices[index] = device;
            Write(devices);
        }

        return device;
    }

    private IReadOnlyList<PairedDevice> Read()
    {
        if (!File.Exists(state.DevicesFile))
            return [];
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(state.DevicesFile), StateJsonContext.Default.DevicesFile);
            return file?.Devices ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{state.DevicesFile} is corrupt: {ex.Message}", ex);
        }
    }

    private void Write(List<PairedDevice> devices)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new DevicesFile(devices), StateJsonContext.Default.DevicesFile);
        state.WritePrivateFile(state.DevicesFile, json);
    }

    private FileStream AcquireLock()
    {
        state.EnsureExists();
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // FileShare.None maps to an advisory flock on Unix, which the CLI and the service both honour.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(state.DevicesLockFile, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }
}
