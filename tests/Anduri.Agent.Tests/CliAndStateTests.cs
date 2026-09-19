using Anduri.Agent.Cli;
using Anduri.Agent.Configuration;
using Anduri.Agent.Pairing;
using System.Security.Cryptography.X509Certificates;
using Anduri.Agent.State;

namespace Anduri.Agent.Tests;

public class CliAndStateTests
{
    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AgentCli.RunAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task CertificateCommandsCreateAndShowFingerprint()
    {
        using var tree = new TempTree();

        var (missingExit, missingOutput, _) = await RunCliAsync("cert", "show", "--state-dir", tree.Root);
        var (regenerateExit, regenerateOutput, _) = await RunCliAsync("cert", "regenerate", "--name", "RIG-01", "--state-dir", tree.Root);
        var (showExit, showOutput, _) = await RunCliAsync("cert", "show", "--state-dir", tree.Root);

        Assert.Equal(1, missingExit);
        Assert.Contains("No certificate yet", missingOutput);
        Assert.Equal(0, regenerateExit);
        Assert.Equal(0, showExit);
        var fingerprint = new CertificateStore(new StateDirectory(tree.Root)).Load().FingerprintHex;
        Assert.Contains(fingerprint, regenerateOutput);
        Assert.Contains($"Fingerprint (SHA-256): {fingerprint}", showOutput);
        Assert.Contains("CN=RIG-01", showOutput);
    }

    [Fact]
    public void CertificateIsEcdsaP256SelfSignedForTenYearsWithPrivateFile()
    {
        using var tree = new TempTree();
        var state = new StateDirectory(tree.Root);

        var certificate = new CertificateStore(state).Regenerate("RIG-01");

        using var key = certificate.Certificate.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(certificate.Certificate.Subject, certificate.Certificate.Issuer);
        Assert.InRange((certificate.Certificate.NotAfter - certificate.Certificate.NotBefore).TotalDays, 3650, 3660);
        Assert.Equal(64, certificate.FingerprintHex.Length);
        Assert.Equal(certificate.FingerprintHex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificate.Certificate.RawData)));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state.CertificateFile));

        // Loading again gives the same fingerprint.
        Assert.Equal(certificate.FingerprintHex, new CertificateStore(state).LoadOrCreate("RIG-01").FingerprintHex);
    }

    [Fact]
    public void AgentIdIsStableAcrossLoads()
    {
        using var tree = new TempTree();
        var state = new StateDirectory(tree.Root);

        var first = AgentIdentity.LoadOrCreate(state, "RIG-01");
        var second = AgentIdentity.LoadOrCreate(state, "Renamed");

        Assert.True(Guid.TryParse(first.Id, out _));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Renamed", second.Name);
    }

    [Fact]
    public async Task DevicesListAndRevoke()
    {
        using var tree = new TempTree();
        var store = new DeviceStore(new StateDirectory(tree.Root), TimeProvider.System);
        store.Add(new PairedDevice("6F9619FF-8B86-D011-B42D-00C04FC964FF", "Homer’s iPad", "iPad", PairingCrypto.HashToken(PairingCrypto.GenerateToken())!, DateTimeOffset.UtcNow, null));
        store.Add(new PairedDevice("0A1B2C3D-0000-0000-0000-000000000000", "Kitchen iPad", "iPad", PairingCrypto.HashToken(PairingCrypto.GenerateToken())!, DateTimeOffset.UtcNow, null));

        var (listExit, listOutput, _) = await RunCliAsync("devices", "list", "--state-dir", tree.Root);
        var (ambiguousExit, _, ambiguousError) = await RunCliAsync("devices", "revoke", "nope", "--state-dir", tree.Root);
        var (revokeExit, revokeOutput, _) = await RunCliAsync("devices", "revoke", "6f9619ff", "--state-dir", tree.Root);

        Assert.Equal(0, listExit);
        Assert.Contains("Homer’s iPad", listOutput);
        Assert.Contains("Kitchen iPad", listOutput);
        Assert.Equal(1, ambiguousExit);
        Assert.Contains("No paired device", ambiguousError);
        Assert.Equal(0, revokeExit);
        Assert.Contains("Revoked “Homer’s iPad”", revokeOutput);
        Assert.Equal(["Kitchen iPad"], store.List().Select(d => d.Name));
    }

    [Fact]
    public void DevicesFileNeverContainsRawToken()
    {
        using var tree = new TempTree();
        var state = new StateDirectory(tree.Root);
        var store = new DeviceStore(state, TimeProvider.System);
        var token = PairingCrypto.GenerateToken();

        store.Add(new PairedDevice("dev", "iPad", null, PairingCrypto.HashToken(token)!, DateTimeOffset.UtcNow, null));

        Assert.DoesNotContain(token, File.ReadAllText(state.DevicesFile));
        Assert.NotNull(store.Authenticate("DEV", token));
        Assert.Null(store.Authenticate("dev", PairingCrypto.GenerateToken()));
        Assert.NotNull(store.List().Single().LastSeen);
    }

    [Theory]
    [InlineData(new string[0], 2)]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "--version" }, 0)]
    [InlineData(new[] { "frobnicate" }, 2)]
    [InlineData(new[] { "run", "--port", "70000" }, 2)]
    [InlineData(new[] { "devices", "list", "--bogus", "1" }, 2)]
    public async Task UsageErrorsExitWithTwo(string[] args, int expected)
    {
        using var tree = new TempTree();
        var (exit, _, _) = await RunCliAsync([.. args, .. args.Length > 0 && args[0] is "run" or "devices" ? new[] { "--state-dir", tree.Root } : []]);
        Assert.Equal(expected, exit);
    }

    [Fact]
    public void ConfigFileIsParsedWithCommentsAndRejectsUnknownKeys()
    {
        using var tree = new TempTree();
        tree.Write("config.json", """
            {
              // comments are allowed
              "name": "RIG-01",
              "port": 48200,
              "sources": { "liquidctl": false },
              "coolerControl": { "url": "https://127.0.0.1:11987", "token": "cc_abc" },
              "drives": { "exclude": ["/boot/efi", "/mnt/backup/*"] },
              "network": { "interface": "enp5s0" },
              "sensors": { "temp.octo.1": { "id": "loop.coolant", "label": "Water" } },
            }
            """);
        tree.Write("bad.json", """{ "coolercontrol": {} }""");

        var (config, path) = AgentConfigLoader.Load(tree.PathOf("config.json"));

        Assert.Equal(tree.PathOf("config.json"), path);
        Assert.Equal("RIG-01", config.Name);
        Assert.Equal(48200, config.Port);
        Assert.False(config.Sources.Liquidctl);
        Assert.True(config.Sources.Nvidia);
        Assert.True(config.Sources.NvApi);
        Assert.Equal("cc_abc", config.CoolerControl.Token);
        Assert.Equal("CCAdmin", config.CoolerControl.Username);
        Assert.Equal("enp5s0", config.Network.Interface);
        Assert.Equal("loop.coolant", config.Sensors["temp.octo.1"].Id);
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(tree.PathOf("bad.json")));
        Assert.Throws<FileNotFoundException>(() => AgentConfigLoader.Load(tree.PathOf("missing.json")));
    }

    [Fact]
    public void ExampleConfigInDeployIsValid()
    {
        var example = Path.Combine(Repository.Root, "deploy", "config.example.json");
        var (config, _) = AgentConfigLoader.Load(example);
        Assert.Equal(48123, config.Port);
    }
}
