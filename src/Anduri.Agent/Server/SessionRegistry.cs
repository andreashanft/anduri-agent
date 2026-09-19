using System.Collections.Concurrent;
using Anduri.Agent.Sensors;

namespace Anduri.Agent.Server;

/// <summary>Open sessions, so a catalog change reaches every connected iPad.</summary>
public sealed class SessionRegistry : IDisposable
{
    private readonly SensorHub hub;
    private readonly ConcurrentDictionary<AgentSession, byte> sessions = new();

    public SessionRegistry(SensorHub hub)
    {
        this.hub = hub;
        hub.CatalogChanged += OnCatalogChanged;
    }

    public int Count => sessions.Count;

    internal void Add(AgentSession session) => sessions.TryAdd(session, 0);

    internal void Remove(AgentSession session) => sessions.TryRemove(session, out _);

    private void OnCatalogChanged(IReadOnlyList<SensorDescriptor> catalog)
    {
        foreach (var session in sessions.Keys)
            session.NotifyCatalogChanged();
    }

    public void Dispose() => hub.CatalogChanged -= OnCatalogChanged;
}
