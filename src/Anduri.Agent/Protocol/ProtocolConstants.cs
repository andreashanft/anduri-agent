namespace Anduri.Agent.Protocol;

public static class ProtocolVersion
{
    /// <summary>The only protocol version this agent speaks.</summary>
    public const int Current = 1;
    public const int Minimum = 1;
}

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Catalog = "catalog";
    public const string History = "history";
    public const string Snapshot = "snapshot";
    public const string SetInterval = "setInterval";
    public const string Interval = "interval";
    public const string Error = "error";
    public const string PairRequest = "pair.request";
    public const string PairChallenge = "pair.challenge";
    public const string PairConfirm = "pair.confirm";
    public const string PairAccepted = "pair.accepted";
    public const string PairRejected = "pair.rejected";
}

public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string Unauthorized = "unauthorized";
    public const string Timeout = "timeout";
    public const string Busy = "busy";
    public const string TooManyAttempts = "too_many_attempts";
}

public static class PairingReasons
{
    public const string WrongCode = "wrong_code";
    public const string Expired = "expired";
    public const string TooManyAttempts = "too_many_attempts";
    public const string Busy = "busy";
}

/// <summary>WebSocket close codes used by the agent.</summary>
public static class CloseCodes
{
    public const int GoingAway = 1001;
    public const int BadRequest = 4400;
    public const int Unauthorized = 4401;
    public const int Timeout = 4408;
    public const int Busy = 4409;
    public const int TooManyAttempts = 4429;

    public static int ForError(string code) => code switch
    {
        ErrorCodes.Unauthorized => Unauthorized,
        ErrorCodes.Timeout => Timeout,
        ErrorCodes.Busy => Busy,
        ErrorCodes.TooManyAttempts => TooManyAttempts,
        _ => BadRequest,
    };
}

/// <summary>Snapshot interval limits in seconds.</summary>
public static class SnapshotInterval
{
    public const double Minimum = 0.5;
    public const double Maximum = 10;
    public const double Default = 1;

    public static double Clamp(double? requested) =>
        requested is { } value && double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Default;
}
