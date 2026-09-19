using System.Globalization;

namespace Anduri.Agent.Pairing;

public sealed record PairingDeviceInfo(string Id, string Name, string? Model);

public enum PairingOpenStatus
{
    Opened,
    /// <summary>Another connection has an open window.</summary>
    Busy,
}

public sealed record PairingOpenResult(PairingOpenStatus Status, byte[] Nonce, TimeSpan ExpiresIn)
{
    public static PairingOpenResult Busy { get; } = new(PairingOpenStatus.Busy, [], TimeSpan.Zero);
}

public enum PairingConfirmStatus
{
    Accepted,
    WrongCode,
    /// <summary>The fifth wrong code; the window is closed.</summary>
    TooManyAttempts,
    /// <summary>This connection's window expired or was never opened.</summary>
    NoWindow,
}

public sealed record PairingConfirmResult(
    PairingConfirmStatus Status,
    int AttemptsLeft = 0,
    byte[]? AgentProof = null,
    PairingDeviceInfo? Device = null);

/// <summary>What the PC shows the user while a window is open.</summary>
public sealed record PairingCodeNotice(PairingDeviceInfo Device, string Code, TimeSpan ExpiresIn)
{
    /// <summary>"481 207": easier to read and type than "481207".</summary>
    public string FormattedCode => $"{Code[..3]} {Code[3..]}";

    /// <summary>"2:00".</summary>
    public string FormattedExpiry => string.Create(CultureInfo.InvariantCulture, $"{(int)ExpiresIn.TotalMinutes}:{ExpiresIn.Seconds:D2}");
}

public enum PairingOutcome
{
    Accepted,
    TooManyAttempts,
    Cancelled,
}

/// <summary>Shows pairing codes to the person at the PC. Tests replace it to read the code.</summary>
public interface IPairingCodeDisplay
{
    void ShowCode(PairingCodeNotice notice);
    void PairingEnded(PairingDeviceInfo device, PairingOutcome outcome);
}

/// <summary>
/// The single, agent-wide pairing window. Only one connection can pair at a time, so a code on the PC's
/// console always belongs to exactly one iPad.
/// </summary>
public sealed class PairingManager(TimeProvider time, IPairingCodeDisplay display)
{
    public static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(120);
    public const int MaxAttempts = 5;

    private readonly Lock gate = new();
    private Window? current;

    /// <summary>Opens a window for <paramref name="owner"/>, replacing its own earlier window if it has one.</summary>
    public PairingOpenResult Open(object owner, PairingDeviceInfo device)
    {
        PairingCodeNotice notice;
        byte[] nonce;
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (current is { } open && !open.IsExpired(now) && !ReferenceEquals(open.Owner, owner))
                return PairingOpenResult.Busy;

            var code = PairingCrypto.GenerateCode();
            nonce = PairingCrypto.GenerateNonce();
            current = new Window(owner, device, code, nonce, now + WindowDuration);
            notice = new PairingCodeNotice(device, code, WindowDuration);
        }

        display.ShowCode(notice);
        return new PairingOpenResult(PairingOpenStatus.Opened, nonce, WindowDuration);
    }

    /// <summary>Checks a client proof against the proof computed with the agent's own certificate fingerprint.</summary>
    public PairingConfirmResult Confirm(object owner, ReadOnlySpan<byte> clientProof, ReadOnlySpan<byte> fingerprint)
    {
        PairingConfirmResult result;
        Window window;
        lock (gate)
        {
            if (current is not { } open || !ReferenceEquals(open.Owner, owner))
                return new PairingConfirmResult(PairingConfirmStatus.NoWindow);
            window = open;

            if (window.IsExpired(time.GetUtcNow()))
            {
                current = null;
                return new PairingConfirmResult(PairingConfirmStatus.NoWindow);
            }

            var expected = PairingCrypto.ComputeClientProof(window.Code, window.Nonce, fingerprint, window.Device.Id);
            if (PairingCrypto.FixedTimeEquals(expected, clientProof))
            {
                current = null;
                var agentProof = PairingCrypto.ComputeAgentProof(window.Code, window.Nonce, fingerprint, window.Device.Id);
                result = new PairingConfirmResult(PairingConfirmStatus.Accepted, AgentProof: agentProof, Device: window.Device);
            }
            else
            {
                window.Attempts++;
                if (window.Attempts >= MaxAttempts)
                {
                    current = null;
                    result = new PairingConfirmResult(PairingConfirmStatus.TooManyAttempts, Device: window.Device);
                }
                else
                {
                    return new PairingConfirmResult(PairingConfirmStatus.WrongCode, MaxAttempts - window.Attempts, Device: window.Device);
                }
            }
        }

        display.PairingEnded(window.Device, result.Status == PairingConfirmStatus.Accepted ? PairingOutcome.Accepted : PairingOutcome.TooManyAttempts);
        return result;
    }

    /// <summary>Closes the window if <paramref name="owner"/> holds it, e.g. when its connection closes.</summary>
    public void Release(object owner)
    {
        PairingDeviceInfo? device = null;
        lock (gate)
        {
            if (current is { } open && ReferenceEquals(open.Owner, owner))
            {
                if (!open.IsExpired(time.GetUtcNow()))
                    device = open.Device;
                current = null;
            }
        }

        if (device is not null)
            display.PairingEnded(device, PairingOutcome.Cancelled);
    }

    /// <summary>When the window held by <paramref name="owner"/> expires, or <c>null</c> if it holds none.</summary>
    public DateTimeOffset? ExpiryOf(object owner)
    {
        lock (gate)
            return current is { } open && ReferenceEquals(open.Owner, owner) ? open.ExpiresAt : null;
    }

    private sealed class Window(object owner, PairingDeviceInfo device, string code, byte[] nonce, DateTimeOffset expiresAt)
    {
        public object Owner { get; } = owner;
        public PairingDeviceInfo Device { get; } = device;
        public string Code { get; } = code;
        public byte[] Nonce { get; } = nonce;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Attempts { get; set; }

        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}
