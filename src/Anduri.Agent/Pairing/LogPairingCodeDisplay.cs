namespace Anduri.Agent.Pairing;

/// <summary>Shows pairing codes in the agent's log, which is the console or the systemd journal.</summary>
public sealed partial class LogPairingCodeDisplay(ILogger<LogPairingCodeDisplay> logger) : IPairingCodeDisplay
{
    public void ShowCode(PairingCodeNotice notice)
    {
        LogCode(logger, notice.Device.Name, notice.FormattedCode, notice.FormattedExpiry);

        // In a terminal the code also gets a banner, so it doesn't drown in log lines. Under systemd the log line is enough.
        if (!Console.IsOutputRedirected)
        {
            Console.Out.WriteLine($"""

                  ╭──────────────────────────────────────────────╮
                    Pairing request from “{notice.Device.Name}”
                    Code  {notice.FormattedCode}      expires in {notice.FormattedExpiry}
                  ╰──────────────────────────────────────────────╯

                """);
        }
    }

    public void PairingEnded(PairingDeviceInfo device, PairingOutcome outcome)
    {
        switch (outcome)
        {
            case PairingOutcome.Accepted:
                LogAccepted(logger, device.Name, device.Id);
                break;
            case PairingOutcome.TooManyAttempts:
                LogTooManyAttempts(logger, device.Name);
                break;
            default:
                LogCancelled(logger, device.Name);
                break;
        }
    }

    // Warning level so the code stands out between routine information lines.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Pairing request from “{DeviceName}” — code {Code} (expires in {Expiry})")]
    private static partial void LogCode(ILogger logger, string deviceName, string code, string expiry);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paired with “{DeviceName}” (device id {DeviceId})")]
    private static partial void LogAccepted(ILogger logger, string deviceName, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pairing with “{DeviceName}” failed: too many wrong codes")]
    private static partial void LogTooManyAttempts(ILogger logger, string deviceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pairing with “{DeviceName}” was cancelled")]
    private static partial void LogCancelled(ILogger logger, string deviceName);
}
