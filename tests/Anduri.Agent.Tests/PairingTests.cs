using System.Text.Json;
using Anduri.Agent.Pairing;
using Microsoft.Extensions.Time.Testing;

namespace Anduri.Agent.Tests;

public class PairingVectorTests
{
    private sealed record Vectors(
        string Code, string Nonce, string Fingerprint, string DeviceId, string ClientProof, string AgentProof,
        string WrongCode, string WrongCodeClientProof, string Token, string TokenHash);

    private static Vectors Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.ProtocolDirectory, "pairing-vectors.json")));
        var root = document.RootElement;
        string S(string name) => root.GetProperty(name).GetString()!;
        return new Vectors(S("code"), S("nonce"), S("fingerprint"), S("deviceId"), S("clientProof"), S("agentProof"),
            S("wrongCode"), S("wrongCodeClientProof"), S("token"), S("tokenHash"));
    }

    [Fact]
    public void ClientProofMatchesVector()
    {
        var v = Load();
        var proof = PairingCrypto.ComputeClientProof(v.Code, Convert.FromBase64String(v.Nonce), Convert.FromHexString(v.Fingerprint), v.DeviceId);
        Assert.Equal(v.ClientProof, Convert.ToBase64String(proof));
    }

    [Fact]
    public void AgentProofMatchesVector()
    {
        var v = Load();
        var proof = PairingCrypto.ComputeAgentProof(v.Code, Convert.FromBase64String(v.Nonce), Convert.FromHexString(v.Fingerprint), v.DeviceId);
        Assert.Equal(v.AgentProof, Convert.ToBase64String(proof));
    }

    [Fact]
    public void WrongCodeProofMatchesVectorAndDiffers()
    {
        var v = Load();
        var proof = PairingCrypto.ComputeClientProof(v.WrongCode, Convert.FromBase64String(v.Nonce), Convert.FromHexString(v.Fingerprint), v.DeviceId);
        Assert.Equal(v.WrongCodeClientProof, Convert.ToBase64String(proof));
        Assert.NotEqual(v.ClientProof, v.WrongCodeClientProof);
    }

    [Fact]
    public void TokenHashMatchesVector()
    {
        var v = Load();
        Assert.Equal(v.TokenHash, PairingCrypto.HashToken(v.Token));
    }

    [Fact]
    public void GeneratedTokensAre32BytesBase64UrlWithoutPadding()
    {
        var token = PairingCrypto.GenerateToken();
        Assert.Equal(43, token.Length);
        Assert.DoesNotContain('=', token);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.NotNull(PairingCrypto.HashToken(token));
    }

    [Fact]
    public void CodesAreSixDigits()
    {
        for (var i = 0; i < 200; i++)
            Assert.Matches("^[0-9]{6}$", PairingCrypto.GenerateCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    public void InvalidTokensHaveNoHash(string token) => Assert.Null(PairingCrypto.HashToken(token));
}

public class PairingManagerTests
{
    private static readonly byte[] Fingerprint = Convert.FromHexString("3e584012d24fef738718c039a9adcebc8beabb2f9efebe7dcbf030146a21925c");
    private static readonly PairingDeviceInfo Device = new("6F9619FF-8B86-D011-B42D-00C04FC964FF", "Homer’s iPad", "iPad");

    private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-09-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly RecordingDisplay display = new();

    private PairingManager CreateManager() => new(time, display);

    private byte[] ProofFor(string code, byte[] nonce, PairingDeviceInfo? device = null) =>
        PairingCrypto.ComputeClientProof(code, nonce, Fingerprint, (device ?? Device).Id);

    private static string WrongCode(string code) => ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void CorrectCodeIsAcceptedWithAgentProof()
    {
        var manager = CreateManager();
        var owner = new object();

        var opened = manager.Open(owner, Device);
        var code = display.Notices.Single().Code;
        var result = manager.Confirm(owner, ProofFor(code, opened.Nonce), Fingerprint);

        Assert.Equal(PairingOpenStatus.Opened, opened.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), opened.ExpiresIn);
        Assert.Equal(32, opened.Nonce.Length);
        Assert.Equal(PairingConfirmStatus.Accepted, result.Status);
        Assert.Equal(PairingCrypto.ComputeAgentProof(code, opened.Nonce, Fingerprint, Device.Id), result.AgentProof);
        Assert.Equal(PairingOutcome.Accepted, display.Ended.Single().Outcome);
        Assert.Null(manager.ExpiryOf(owner));
    }

    [Fact]
    public void WrongCodesCountDownThenTooManyAttemptsClosesWindow()
    {
        var manager = CreateManager();
        var owner = new object();
        var opened = manager.Open(owner, Device);
        var wrong = ProofFor(WrongCode(display.Notices.Single().Code), opened.Nonce);

        var attemptsLeft = Enumerable.Range(0, 4).Select(_ => manager.Confirm(owner, wrong, Fingerprint)).ToList();
        var fifth = manager.Confirm(owner, wrong, Fingerprint);

        Assert.All(attemptsLeft, r => Assert.Equal(PairingConfirmStatus.WrongCode, r.Status));
        Assert.Equal([4, 3, 2, 1], attemptsLeft.Select(r => r.AttemptsLeft));
        Assert.Equal(PairingConfirmStatus.TooManyAttempts, fifth.Status);
        Assert.Equal(PairingOutcome.TooManyAttempts, display.Ended.Single().Outcome);

        // The window is gone: even the right code is refused now, and another device can pair.
        var right = ProofFor(display.Notices.Single().Code, opened.Nonce);
        Assert.Equal(PairingConfirmStatus.NoWindow, manager.Confirm(owner, right, Fingerprint).Status);
        Assert.Equal(PairingOpenStatus.Opened, manager.Open(new object(), Device).Status);
    }

    [Fact]
    public void WindowExpiresAfter120Seconds()
    {
        var manager = CreateManager();
        var owner = new object();
        var opened = manager.Open(owner, Device);
        var proof = ProofFor(display.Notices.Single().Code, opened.Nonce);

        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(1), manager.ExpiryOf(owner));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(PairingConfirmStatus.NoWindow, manager.Confirm(owner, proof, Fingerprint).Status);
    }

    [Fact]
    public void CorrectCodeJustBeforeExpiryIsAccepted()
    {
        var manager = CreateManager();
        var owner = new object();
        var opened = manager.Open(owner, Device);

        time.Advance(TimeSpan.FromSeconds(119.9));

        Assert.Equal(PairingConfirmStatus.Accepted, manager.Confirm(owner, ProofFor(display.Notices.Single().Code, opened.Nonce), Fingerprint).Status);
    }

    [Fact]
    public void SecondRequesterIsBusyWhileWindowIsOpen()
    {
        var manager = CreateManager();
        var first = new object();
        manager.Open(first, Device);

        var second = manager.Open(new object(), Device with { Id = "OTHER", Name = "Other iPad" });

        Assert.Equal(PairingOpenStatus.Busy, second.Status);
        Assert.Single(display.Notices);
    }

    [Fact]
    public void ExpiredWindowNoLongerBlocksOthers()
    {
        var manager = CreateManager();
        manager.Open(new object(), Device);

        time.Advance(PairingManager.WindowDuration);

        Assert.Equal(PairingOpenStatus.Opened, manager.Open(new object(), Device).Status);
    }

    [Fact]
    public void ReleasedWindowNoLongerBlocksOthers()
    {
        var manager = CreateManager();
        var first = new object();
        manager.Open(first, Device);

        manager.Release(first);

        Assert.Equal(PairingOutcome.Cancelled, display.Ended.Single().Outcome);
        Assert.Equal(PairingOpenStatus.Opened, manager.Open(new object(), Device).Status);
    }

    [Fact]
    public void SameConnectionCanRequestAgainWithFreshCodeAndAttempts()
    {
        var manager = CreateManager();
        var owner = new object();
        var firstWindow = manager.Open(owner, Device);
        manager.Confirm(owner, ProofFor(WrongCode(display.Notices[0].Code), firstWindow.Nonce), Fingerprint);

        var second = manager.Open(owner, Device);
        var wrong = manager.Confirm(owner, ProofFor(WrongCode(display.Notices[1].Code), second.Nonce), Fingerprint);

        Assert.Equal(PairingOpenStatus.Opened, second.Status);
        Assert.NotEqual(firstWindow.Nonce, second.Nonce);
        Assert.Equal(4, wrong.AttemptsLeft);
    }

    [Fact]
    public void ProofForAnotherFingerprintIsRejected()
    {
        var manager = CreateManager();
        var owner = new object();
        var opened = manager.Open(owner, Device);
        var otherFingerprint = new byte[32];
        var proofWithMitmFingerprint = PairingCrypto.ComputeClientProof(display.Notices.Single().Code, opened.Nonce, otherFingerprint, Device.Id);

        Assert.Equal(PairingConfirmStatus.WrongCode, manager.Confirm(owner, proofWithMitmFingerprint, Fingerprint).Status);
    }

    [Fact]
    public void ConfirmFromConnectionWithoutWindowIsNoWindow()
    {
        var manager = CreateManager();
        manager.Open(new object(), Device);

        Assert.Equal(PairingConfirmStatus.NoWindow, manager.Confirm(new object(), new byte[32], Fingerprint).Status);
    }

    [Fact]
    public void NoticeFormatsCodeAndExpiry()
    {
        var notice = new PairingCodeNotice(Device, "481207", TimeSpan.FromSeconds(120));
        Assert.Equal("481 207", notice.FormattedCode);
        Assert.Equal("2:00", notice.FormattedExpiry);
    }

    internal sealed class RecordingDisplay : IPairingCodeDisplay
    {
        public List<PairingCodeNotice> Notices { get; } = [];
        public List<(PairingDeviceInfo Device, PairingOutcome Outcome)> Ended { get; } = [];

        public void ShowCode(PairingCodeNotice notice)
        {
            lock (Notices)
                Notices.Add(notice);
        }

        public void PairingEnded(PairingDeviceInfo device, PairingOutcome outcome)
        {
            lock (Ended)
                Ended.Add((device, outcome));
        }
    }
}
