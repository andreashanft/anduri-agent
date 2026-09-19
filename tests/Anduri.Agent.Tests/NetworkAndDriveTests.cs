using Anduri.Agent.Configuration;
using Anduri.Agent.Sensors;
using Anduri.Agent.Sensors.Linux;
using Microsoft.Extensions.Time.Testing;

namespace Anduri.Agent.Tests;

public class NetworkSourceTests
{
    private const string Routes = """
        Iface	Destination	Gateway 	Flags	RefCnt	Use	Metric	Mask		MTU	Window	IRTT
        wlp6s0	00000000	0102A8C0	0003	0	0	600	00000000	0	0	0
        enp5s0	00000000	0102A8C0	0003	0	0	100	00000000	0	0	0
        enp5s0	0002A8C0	00000000	0001	0	0	100	00FFFFFF	0	0	0
        docker0	000011AC	00000000	0001	0	0	0	0000FFFF	0	0	0
        """;

    [Fact]
    public void DefaultRouteWithLowestMetricWins() => Assert.Equal("enp5s0", NetworkSource.ParseDefaultRoute(Routes));

    [Fact]
    public void NoDefaultRouteIsNull() => Assert.Null(NetworkSource.ParseDefaultRoute(Routes.Split('\n')[0] + "\nenp5s0\t0002A8C0\t00000000\t0001\t0\t0\t100\t00FFFFFF\t0\t0\t0\n"));

    [Theory]
    [InlineData(100, "100 Mb/s")]
    [InlineData(1000, "1 GbE")]
    [InlineData(2500, "2.5 GbE")]
    [InlineData(10000, "10 GbE")]
    public void LinkSpeedIsFormatted(long mbps, string expected) => Assert.Equal(expected, NetworkSource.FormatSpeed(mbps));

    [Fact]
    public async Task RatesAreByteDeltasPerSecond()
    {
        using var tree = new TempTree();
        tree.Write("proc/net/route", Routes)
            .WriteAll("sys/class/net/enp5s0", ("speed", "2500"), ("operstate", "up"))
            .WriteAll("sys/class/net/enp5s0/statistics", ("rx_bytes", "1000000"), ("tx_bytes", "50000"));
        var time = new FakeTimeProvider();
        var source = new NetworkSource(tree.Paths, time);

        var first = new Dictionary<string, double>();
        await source.ReadAsync(first, CancellationToken.None);
        tree.WriteAll("sys/class/net/enp5s0/statistics", ("rx_bytes", "24000000"), ("tx_bytes", "650000"));
        time.Advance(TimeSpan.FromSeconds(2));
        var second = new Dictionary<string, double>();
        await source.ReadAsync(second, CancellationToken.None);

        Assert.Empty(first);
        Assert.Equal(11_500_000, second["net.down"]);
        Assert.Equal(300_000, second["net.up"]);
        var down = source.Describe().Single(d => d.Id == "net.down");
        Assert.Equal("2.5 GbE", down.Detail);
        Assert.Equal("Down", down.Label);
        Assert.Equal(SensorKind.Network, down.Kind);
        Assert.Equal("B/s", down.Unit);
    }

    [Fact]
    public async Task CounterResetSkipsOneInterval()
    {
        using var tree = new TempTree();
        tree.Write("proc/net/route", Routes)
            .WriteAll("sys/class/net/enp5s0/statistics", ("rx_bytes", "5000"), ("tx_bytes", "5000"));
        var time = new FakeTimeProvider();
        var source = new NetworkSource(tree.Paths, time);
        await source.ReadAsync(new Dictionary<string, double>(), CancellationToken.None);

        tree.WriteAll("sys/class/net/enp5s0/statistics", ("rx_bytes", "10"), ("tx_bytes", "10"));
        time.Advance(TimeSpan.FromSeconds(1));
        var values = new Dictionary<string, double>();
        await source.ReadAsync(values, CancellationToken.None);

        Assert.Empty(values);
    }
}

public class DriveSourceTests
{
    private const string Mounts = """
        /dev/nvme0n1p2 / btrfs rw,relatime,ssd,subvol=/@ 0 0
        proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
        sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
        tmpfs /run tmpfs rw,nosuid,nodev,size=6573620k 0 0
        /dev/nvme0n1p2 /home btrfs rw,relatime,ssd,subvol=/@home 0 0
        /dev/nvme0n1p1 /boot/efi vfat rw,relatime 0 0
        /dev/sda1 /mnt/Games\040SSD ext4 rw,relatime 0 0
        overlay /var/lib/docker/overlay2/3f2a/merged overlay rw,relatime 0 0
        /dev/loop3 /snap/core22/1122 squashfs ro,nodev,relatime 0 0
        nas:/export/media /mnt/media nfs4 rw,relatime 0 0
        /dev/sdb1 /media/homer/USB vfat rw,nosuid,nodev 0 0
        /dev/sdc1 /run/media/homer/Backup ext4 rw 0 0
        portal /run/user/1000/doc fuse.portal rw 0 0
        """;

    private static readonly Dictionary<string, FileSystemSpace> Space = new()
    {
        ["/"] = new(1_000_204_886_016, 600_000_000_000),
        ["/mnt/Games SSD"] = new(2_000_398_934_016, 1_000_000_000_000),
        ["/media/homer/USB"] = new(64_000_000_000, 60_000_000_000),
    };

    [Fact]
    public void RealFilesystemsAreKeptOnce()
    {
        var mounts = DriveSource.FilterMounts(DriveSource.ParseMounts(Mounts), new DrivesConfig());

        Assert.Equal(["/", "/media/homer/USB", "/mnt/Games SSD"], mounts.Select(m => m.MountPoint));
    }

    [Fact]
    public void IncludeListLimitsDrives()
    {
        var config = new DrivesConfig { Include = ["/", "/run/media/*"] };

        var mounts = DriveSource.FilterMounts(DriveSource.ParseMounts(Mounts), config);

        Assert.Equal(["/", "/run/media/homer/Backup"], mounts.Select(m => m.MountPoint));
    }

    [Fact]
    public void ExcludePatternsApply()
    {
        var config = new DrivesConfig { Exclude = ["/media/*", "/boot/efi"] };

        var mounts = DriveSource.FilterMounts(DriveSource.ParseMounts(Mounts), config);

        Assert.Equal(["/", "/mnt/Games SSD"], mounts.Select(m => m.MountPoint));
    }

    [Fact]
    public async Task DrivesReportUsedGbWithCapacityAndMountPointLabel()
    {
        using var tree = new TempTree();
        tree.Write("proc/self/mounts", Mounts).Write("sys/class/block/sda/device/model", "Samsung SSD 870\n");
        var source = new DriveSource(tree.Paths, new DrivesConfig(), new FakeTimeProvider(), mount => Space.TryGetValue(mount, out var space) ? space : null);
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);
        var catalog = source.Describe().ToDictionary(d => d.Id);

        Assert.Equal(["drive.media_homer_usb", "drive.mnt_games_ssd", "drive.root"], catalog.Keys.Order());
        Assert.Equal(372.72, values["drive.root"], precision: 2);
        Assert.Equal(931.5, catalog["drive.root"].Capacity);
        Assert.Equal("/", catalog["drive.root"].Label);
        Assert.Equal("System", catalog["drive.root"].Name);
        Assert.Equal("/mnt/Games SSD", catalog["drive.mnt_games_ssd"].Label);
        Assert.Equal("Games SSD", catalog["drive.mnt_games_ssd"].Name);
        Assert.Equal("Samsung SSD 870", catalog["drive.mnt_games_ssd"].Hardware);
        Assert.Equal(SensorKind.Storage, catalog["drive.root"].Kind);
    }
}
