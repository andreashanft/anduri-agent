using System.Globalization;
using Anduri.Agent.State;

namespace Anduri.Agent.Discovery;

/// <summary>What the agent advertises: one <c>_anduri._tcp</c> instance with the protocol's TXT keys.</summary>
public sealed record ServiceRegistration(string InstanceName, int Port, IReadOnlyList<KeyValuePair<string, string>> Txt)
{
    public const string ServiceType = "_anduri._tcp";

    public static ServiceRegistration Create(AgentIdentity identity, AgentCertificate certificate, int port, int sensorCount) => new(
        identity.Name,
        port,
        [
            new("v", "1"),
            new("id", identity.Id),
            new("name", identity.Name),
            new("ver", identity.Version),
            new("sensors", sensorCount.ToString(CultureInfo.InvariantCulture)),
            new("os", identity.Os),
            new("fp", certificate.ShortFingerprint),
        ]);

    public IEnumerable<string> TxtStrings => Txt.Select(kv => $"{kv.Key}={kv.Value}");

    public string? this[string key] => Txt.FirstOrDefault(kv => kv.Key == key).Value;
}

/// <summary>Publishes a <see cref="ServiceRegistration"/> on the local network.</summary>
public interface IServiceAdvertiser : IAsyncDisposable
{
    string Name { get; }

    /// <exception cref="DiscoveryUnavailableException">This method doesn't work on this machine.</exception>
    Task StartAsync(ServiceRegistration registration, CancellationToken cancellationToken);

    /// <summary>Publishes changed TXT values (e.g. the sensor count).</summary>
    Task UpdateAsync(ServiceRegistration registration, CancellationToken cancellationToken);

    /// <summary>False when the advertisement stopped on its own, e.g. the helper process or daemon exited.</summary>
    bool IsRunning { get; }
}

public sealed class DiscoveryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
