using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Anduri.Agent.Discovery;

/// <summary>
/// Answers mDNS queries for the agent's service itself (PTR, SRV, TXT and A over IPv4), for machines without
/// Avahi or mDNSResponder. It shares UDP port 5353 with other responders through SO_REUSEADDR where the OS allows it.
/// </summary>
public sealed partial class ManagedMdnsAdvertiser(ILogger logger) : IServiceAdvertiser
{
    private const int MdnsPort = 5353;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");

    private Socket? socket;
    private CancellationTokenSource? stopping;
    private Task receiveLoop = Task.CompletedTask;
    private volatile ServiceRegistration? registration;
    private readonly string hostLabel = MdnsResponderLogic.HostLabel(Environment.MachineName);

    public string Name => "managed mDNS";

    public bool IsRunning => socket is not null && !receiveLoop.IsCompleted;

    public async Task StartAsync(ServiceRegistration newRegistration, CancellationToken cancellationToken)
    {
        registration = newRegistration;
        try
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            udp.MulticastLoopback = true;

            var joined = 0;
            foreach (var (_, address) in Interfaces())
            {
                try
                {
                    udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, address));
                    joined++;
                }
                catch (SocketException)
                {
                    // Already joined through another address of the same interface.
                }
            }

            if (joined == 0)
            {
                udp.Dispose();
                throw new DiscoveryUnavailableException("no network interface with IPv4 multicast");
            }

            socket = udp;
        }
        catch (SocketException ex)
        {
            throw new DiscoveryUnavailableException($"UDP port 5353 isn't available ({ex.SocketErrorCode})", ex);
        }

        stopping = new CancellationTokenSource();
        receiveLoop = ReceiveLoopAsync(socket, stopping.Token);
        await AnnounceAsync(cancellationToken);
        LogStarted(logger, newRegistration.InstanceName, newRegistration.Port, hostLabel);
    }

    public Task UpdateAsync(ServiceRegistration newRegistration, CancellationToken cancellationToken)
    {
        registration = newRegistration;
        return AnnounceAsync(cancellationToken);
    }

    /// <summary>Unsolicited responses on every interface, twice, as RFC 6762 §8.3 suggests.</summary>
    private async Task AnnounceAsync(CancellationToken cancellationToken, bool goodbye = false)
    {
        if (socket is null || registration is not { } current)
            return;

        for (var round = 0; round < (goodbye ? 1 : 2); round++)
        {
            if (round > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            foreach (var (_, address) in Interfaces())
            {
                var message = MdnsResponderLogic.Announcement(current, hostLabel, [address], goodbye);
                await SendAsync(message, address, new IPEndPoint(MulticastGroup, MdnsPort), cancellationToken);
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket udp, CancellationToken cancellationToken)
    {
        var buffer = new byte[9000];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult received;
            try
            {
                received = await udp.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                LogReceiveFailed(logger, ex.Message);
                return;
            }

            if (registration is not { } current)
                continue;

            DnsMessage query;
            try
            {
                query = DnsMessage.Decode(buffer.AsSpan(0, received.ReceivedBytes));
            }
            catch (FormatException)
            {
                continue;
            }

            // Answer with the addresses of the interface the query arrived on, so a Docker bridge address never reaches the iPad.
            var addresses = AddressesOf(received.PacketInformation.Interface);
            if (addresses.Count == 0)
                continue;

            var source = (IPEndPoint)received.RemoteEndPoint;
            // Legacy unicast queries (source port isn't 5353) get a direct reply that echoes the question id.
            var legacyUnicast = source.Port != MdnsPort;
            var response = MdnsResponderLogic.Respond(query, current, hostLabel, addresses, legacyUnicast);
            if (response is null)
                continue;

            var destination = legacyUnicast || query.Questions.All(q => q.UnicastResponse)
                ? source
                : new IPEndPoint(MulticastGroup, MdnsPort);
            try
            {
                await SendAsync(response, addresses[0], destination, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendAsync(DnsMessage message, IPAddress outgoingInterface, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (socket is null)
            return;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, outgoingInterface.GetAddressBytes());
            await socket.SendToAsync(message.Encode(), SocketFlags.None, destination, cancellationToken);
        }
        catch (SocketException ex)
        {
            LogSendFailed(logger, outgoingInterface.ToString(), ex.SocketErrorCode.ToString());
        }
    }

    private static List<(int Index, IPAddress Address)> Interfaces()
    {
        var result = new List<(int, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast)
                continue;
            if (IsVirtualBridge(nic.Name))
                continue;
            var properties = nic.GetIPProperties();
            var ipv4 = properties.GetIPv4Properties();
            if (ipv4 is null)
                continue;
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                    result.Add((ipv4.Index, unicast.Address));
            }
        }

        return result;
    }

    private static List<IPAddress> AddressesOf(int interfaceIndex) =>
        Interfaces().Where(i => i.Index == interfaceIndex).Select(i => i.Address).ToList();

    // Container and VM bridges: advertising their addresses sends iPads to unreachable IPs.
    private static bool IsVirtualBridge(string name) =>
        name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal) ||
        name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal) ||
        name.StartsWith("podman", StringComparison.Ordinal) || name.StartsWith("cni", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (socket is null)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await AnnounceAsync(timeout.Token, goodbye: true);
        }
        catch (OperationCanceledException)
        {
        }

        if (stopping is not null)
            await stopping.CancelAsync();
        socket.Dispose();
        await receiveLoop;
        stopping?.Dispose();
        socket = null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Advertising “{Instance}” as _anduri._tcp on port {Port} with the built-in mDNS responder (host {Host}.local)")]
    private static partial void LogStarted(ILogger logger, string instance, int port, string host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS responder stopped receiving: {Reason}")]
    private static partial void LogReceiveFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS send via {Interface} failed: {Error}")]
    private static partial void LogSendFailed(ILogger logger, string @interface, string error);
}

/// <summary>The responder's decisions, separate from sockets so they can be tested.</summary>
public static class MdnsResponderLogic
{
    // RFC 6762 §10: 120 s for records with host names, 75 minutes for the rest.
    private const uint HostTtl = 120;
    private const uint OtherTtl = 4500;
    private const string ServicesEnumeration = "_services._dns-sd._udp.local";

    public static string ServiceName => $"{ServiceRegistration.ServiceType}.local";

    public static string InstanceName(ServiceRegistration registration) => $"{registration.InstanceName}.{ServiceName}";

    /// <summary>"RIG-01.fritz.box" → "RIG-01".</summary>
    public static string HostLabel(string machineName)
    {
        var label = machineName.Split('.')[0];
        var clean = new string(label.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return clean.Length == 0 ? "anduri-agent" : clean;
    }

    /// <summary>Builds the answer to <paramref name="query"/>, or <c>null</c> when none of its questions are about this agent.</summary>
    public static DnsMessage? Respond(DnsMessage query, ServiceRegistration registration, string hostLabel, IReadOnlyList<IPAddress> addresses, bool legacyUnicast)
    {
        if (query.IsResponse)
            return null;

        var instance = InstanceName(registration);
        var host = $"{hostLabel}.local";
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();

        foreach (var question in query.Questions)
        {
            var name = question.Name.TrimEnd('.');
            var any = question.Type == DnsType.Any;
            if (Matches(name, ServiceName) && (question.Type == DnsType.Ptr || any))
            {
                answers.Add(DnsRecord.Ptr(ServiceName, instance, OtherTtl));
                additionals.AddRange(ServiceRecords(registration, host, addresses));
            }
            else if (Matches(name, ServicesEnumeration) && (question.Type == DnsType.Ptr || any))
            {
                answers.Add(DnsRecord.Ptr(ServicesEnumeration, ServiceName, OtherTtl));
            }
            else if (Matches(name, instance))
            {
                if (question.Type is DnsType.Srv or DnsType.Any)
                    answers.Add(DnsRecord.Srv(instance, host, registration.Port, HostTtl));
                if (question.Type is DnsType.Txt or DnsType.Any)
                    answers.Add(DnsRecord.Txt(instance, registration.TxtStrings, OtherTtl));
                if (question.Type is DnsType.Srv or DnsType.Any)
                    additionals.AddRange(addresses.Select(a => DnsRecord.A(host, a, HostTtl)));
            }
            else if (Matches(name, host) && (question.Type == DnsType.A || any))
            {
                answers.AddRange(addresses.Select(a => DnsRecord.A(host, a, HostTtl)));
            }
        }

        if (answers.Count == 0)
            return null;

        var answerKeys = answers.Select(a => (a.Name, a.Type)).ToHashSet();
        additionals = additionals.Where(a => !answerKeys.Contains((a.Name, a.Type))).DistinctBy(a => (a.Name, a.Type, Convert.ToHexString(a.Data))).ToList();

        // Legacy unicast replies echo the query id and questions and must not set the cache-flush bit (RFC 6762 §6.7).
        if (legacyUnicast)
        {
            return new DnsMessage(query.Id, true, query.Questions,
                answers.Select(a => a with { CacheFlush = false, Ttl = Math.Min(a.Ttl, 10) }).ToList(),
                additionals.Select(a => a with { CacheFlush = false, Ttl = Math.Min(a.Ttl, 10) }).ToList());
        }

        return new DnsMessage(0, true, [], answers, additionals);
    }

    /// <summary>All records of the service, as sent at startup, on TXT changes, and with TTL 0 as a goodbye.</summary>
    public static DnsMessage Announcement(ServiceRegistration registration, string hostLabel, IReadOnlyList<IPAddress> addresses, bool goodbye = false)
    {
        var host = $"{hostLabel}.local";
        List<DnsRecord> records = [DnsRecord.Ptr(ServiceName, InstanceName(registration), OtherTtl), .. ServiceRecords(registration, host, addresses)];
        if (goodbye)
            records = records.Select(r => r with { Ttl = 0 }).ToList();
        return new DnsMessage(0, true, [], records, []);
    }

    private static IEnumerable<DnsRecord> ServiceRecords(ServiceRegistration registration, string host, IReadOnlyList<IPAddress> addresses)
    {
        var instance = InstanceName(registration);
        yield return DnsRecord.Srv(instance, host, registration.Port, HostTtl);
        yield return DnsRecord.Txt(instance, registration.TxtStrings, OtherTtl);
        foreach (var address in addresses)
            yield return DnsRecord.A(host, address, HostTtl);
    }

    private static bool Matches(string name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
