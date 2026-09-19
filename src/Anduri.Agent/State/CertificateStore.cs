using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Anduri.Agent.State;

/// <summary>The agent's TLS certificate and its SHA-256 fingerprint.</summary>
public sealed record AgentCertificate(X509Certificate2 Certificate, byte[] Fingerprint)
{
    /// <summary>Lowercase hex without separators, as in the protocol.</summary>
    public string FingerprintHex => Convert.ToHexStringLower(Fingerprint);

    /// <summary>The first 16 hex characters, for the <c>fp</c> TXT key.</summary>
    public string ShortFingerprint => FingerprintHex[..16];

    public static byte[] ComputeFingerprint(X509Certificate2 certificate) => SHA256.HashData(certificate.RawData);
}

/// <summary>Creates and loads the self-signed ECDSA P-256 certificate kept in the state directory.</summary>
public sealed class CertificateStore(StateDirectory state)
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(3653);

    public bool Exists => File.Exists(state.CertificateFile);

    public AgentCertificate LoadOrCreate(string name) => Exists ? Load() : Regenerate(name);

    public AgentCertificate Load()
    {
        // The PFX has no password: it is protected by file mode 0600 like an SSH host key.
        // macOS can't use ephemeral keys for TLS, everywhere else they avoid writing key files to disk.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(state.CertificateFile, password: null, flags);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"{state.CertificateFile} has no private key. Run 'anduri-agent cert regenerate'.");
        return new AgentCertificate(certificate, AgentCertificate.ComputeFingerprint(certificate));
    }

    /// <summary>Creates a new certificate, replacing the old one. Every paired iPad has to pair again.</summary>
    public AgentCertificate Regenerate(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(string.IsNullOrWhiteSpace(name) ? "Anduri agent" : name);
        subject.AddOrganizationName("Anduri agent");

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        if (IsValidDnsName(name))
        {
            san.AddDnsName(name);
            san.AddDnsName($"{name}.local");
        }
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now + Validity);
        state.WritePrivateFile(state.CertificateFile, certificate.Export(X509ContentType.Pkcs12));
        return Load();
    }

    private static bool IsValidDnsName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 63 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && name[0] != '-' && name[^1] != '-';
}
