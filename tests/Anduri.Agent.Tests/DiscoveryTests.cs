using System.Net;
using Anduri.Agent.Discovery;
using Anduri.Agent.State;

namespace Anduri.Agent.Tests;

public class DiscoveryTests
{
    private static readonly ServiceRegistration Registration = new("RIG-01", 48123,
    [
        new("v", "1"), new("id", "0B7A1E6C-3D52-4C1F-9A0E-2F5D8C7B6A41"), new("name", "RIG-01"), new("ver", "1.0.0"),
        new("sensors", "38"), new("os", "linux"), new("fp", "3e584012d24fef73"),
    ]);

    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.20");

    [Fact]
    public void TxtRecordHasProtocolKeys()
    {
        using var state = new TempTree();
        var store = new CertificateStore(new StateDirectory(state.Root));
        var certificate = store.Regenerate("RIG-01");
        var identity = new AgentIdentity("0B7A1E6C-3D52-4C1F-9A0E-2F5D8C7B6A41", "RIG-01", "1.0.0", "linux");

        var registration = ServiceRegistration.Create(identity, certificate, 48123, 38);

        Assert.Equal(["v=1", "id=0B7A1E6C-3D52-4C1F-9A0E-2F5D8C7B6A41", "name=RIG-01", "ver=1.0.0", "sensors=38", "os=linux", $"fp={certificate.FingerprintHex[..16]}"],
            registration.TxtStrings);
    }

    [Fact]
    public void PtrQueryGetsInstanceWithSrvTxtAndAddress()
    {
        var query = new DnsMessage(0, false, [new DnsQuestion("_anduri._tcp.local", DnsType.Ptr, false)], [], []);

        var response = DnsMessage.Decode(MdnsResponderLogic.Respond(DnsMessage.Decode(query.Encode()), Registration, "rig01", [Lan], legacyUnicast: false)!.Encode());

        Assert.True(response.IsResponse);
        var ptr = Assert.Single(response.Answers);
        Assert.Equal(DnsType.Ptr, ptr.Type);
        Assert.Equal("RIG-01._anduri._tcp.local", DnsMessage.DecodeNameFromData(ptr.Data));

        var srv = response.Additionals.Single(r => r.Type == DnsType.Srv);
        Assert.Equal(48123, (srv.Data[4] << 8) | srv.Data[5]);
        Assert.Equal("rig01.local", DnsMessage.DecodeNameFromData(srv.Data, 6));
        Assert.True(srv.CacheFlush);

        var txt = response.Additionals.Single(r => r.Type == DnsType.Txt);
        Assert.Equal(Registration.TxtStrings, DnsMessage.DecodeTxt(txt.Data));

        var a = response.Additionals.Single(r => r.Type == DnsType.A);
        Assert.Equal(Lan, new IPAddress(a.Data));
    }

    [Fact]
    public void QueriesForOtherServicesAreIgnored()
    {
        var query = new DnsMessage(0, false, [new DnsQuestion("_airplay._tcp.local", DnsType.Ptr, false)], [], []);
        Assert.Null(MdnsResponderLogic.Respond(query, Registration, "rig01", [Lan], false));
    }

    [Fact]
    public void ResponsesAreNeverAnswered()
    {
        var response = new DnsMessage(0, true, [new DnsQuestion("_anduri._tcp.local", DnsType.Ptr, false)], [], []);
        Assert.Null(MdnsResponderLogic.Respond(response, Registration, "rig01", [Lan], false));
    }

    [Fact]
    public void HostAddressQueryIsAnsweredCaseInsensitively()
    {
        var query = new DnsMessage(0, false, [new DnsQuestion("RIG01.local", DnsType.A, false)], [], []);

        var response = MdnsResponderLogic.Respond(query, Registration, "rig01", [Lan], false)!;

        Assert.Equal(Lan, new IPAddress(Assert.Single(response.Answers).Data));
    }

    [Fact]
    public void LegacyUnicastEchoesIdAndQuestionWithoutCacheFlush()
    {
        var query = new DnsMessage(0x1234, false, [new DnsQuestion("RIG-01._anduri._tcp.local", DnsType.Srv, false)], [], []);

        var response = DnsMessage.Decode(MdnsResponderLogic.Respond(query, Registration, "rig01", [Lan], legacyUnicast: true)!.Encode());

        Assert.Equal(0x1234, response.Id);
        Assert.Single(response.Questions);
        Assert.All(response.Answers.Concat(response.Additionals), r => Assert.False(r.CacheFlush));
    }

    [Fact]
    public void CompressedNamesAreDecoded()
    {
        // Query for _anduri._tcp.local PTR followed by a second question using a pointer to offset 12.
        byte[] packet =
        [
            0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
            7, .. "_anduri"u8, 4, .. "_tcp"u8, 5, .. "local"u8, 0, 0, 12, 0, 1,
            0xC0, 12, 0, 33, 0x80, 1,
        ];

        var message = DnsMessage.Decode(packet);

        Assert.Equal("_anduri._tcp.local", message.Questions[1].Name);
        Assert.True(message.Questions[1].UnicastResponse);
    }

    [Fact]
    public void GoodbyeHasZeroTtl()
    {
        var goodbye = MdnsResponderLogic.Announcement(Registration, "rig01", [Lan], goodbye: true);
        Assert.All(goodbye.Answers, r => Assert.Equal(0u, r.Ttl));
    }

    [Theory]
    [InlineData("RIG-01.fritz.box", "RIG-01")]
    [InlineData("rig_01", "rig01")]
    [InlineData("...", "anduri-agent")]
    public void HostLabelIsDnsSafe(string machine, string expected) => Assert.Equal(expected, MdnsResponderLogic.HostLabel(machine));

    [Fact]
    public void TruncatedPacketIsRejected() => Assert.Throws<FormatException>(() => DnsMessage.Decode(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 9, 1 }));
}
