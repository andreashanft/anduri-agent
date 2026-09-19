using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Anduri.Agent.Pairing;

/// <summary>The pairing proofs, codes and tokens from the protocol's "Pairing" section.</summary>
public static class PairingCrypto
{
    public const int NonceLength = 32;
    public const int TokenLength = 32;
    public const int ProofLength = 32;

    private static readonly System.Buffers.SearchValues<char> Base64UrlAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly byte[] ClientLabel = "anduri-pair-v1/client"u8.ToArray();
    private static readonly byte[] AgentLabel = "anduri-pair-v1/agent"u8.ToArray();

    /// <summary>HMAC-SHA256(key: UTF8(code), message: "anduri-pair-v1/client" ‖ nonce ‖ fingerprint ‖ UTF8(deviceId)).</summary>
    public static byte[] ComputeClientProof(string code, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> fingerprint, string deviceId) =>
        ComputeProof(ClientLabel, code, nonce, fingerprint, deviceId);

    /// <summary>HMAC-SHA256(key: UTF8(code), message: "anduri-pair-v1/agent" ‖ nonce ‖ fingerprint ‖ UTF8(deviceId)).</summary>
    public static byte[] ComputeAgentProof(string code, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> fingerprint, string deviceId) =>
        ComputeProof(AgentLabel, code, nonce, fingerprint, deviceId);

    /// <summary>A uniformly random 6-digit code, "000000" to "999999".</summary>
    public static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static byte[] GenerateNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>32 random bytes as Base64url without padding.</summary>
    public static string GenerateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenLength));

    /// <summary>
    /// Lowercase hex SHA-256 of the token's decoded bytes (not of its text), matching the test vectors.
    /// Returns <c>null</c> when the token isn't valid Base64url.
    /// </summary>
    public static string? HashToken(string token)
    {
        // The decoder skips whitespace, so check the alphabet first: a token is exactly one Base64url string.
        if (string.IsNullOrEmpty(token) || token.Length > 256 || token.AsSpan().ContainsAnyExcept(Base64UrlAlphabet))
            return null;

        Span<byte> buffer = stackalloc byte[192];
        if (!Base64Url.TryDecodeFromChars(token, buffer, out var written))
            return null;
        return Convert.ToHexStringLower(SHA256.HashData(buffer[..written]));
    }

    /// <summary>Compares two proofs or hashes without leaking where they differ.</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    private static byte[] ComputeProof(byte[] label, string code, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> fingerprint, string deviceId)
    {
        var deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);
        var message = new byte[label.Length + nonce.Length + fingerprint.Length + deviceIdBytes.Length];
        var span = message.AsSpan();
        label.CopyTo(span);
        nonce.CopyTo(span[label.Length..]);
        fingerprint.CopyTo(span[(label.Length + nonce.Length)..]);
        deviceIdBytes.CopyTo(span[(label.Length + nonce.Length + fingerprint.Length)..]);
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), message);
    }
}
