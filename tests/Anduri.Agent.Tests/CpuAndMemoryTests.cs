using System.Text;
using Anduri.Agent.Sensors;
using Anduri.Agent.Sensors.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anduri.Agent.Tests;

public class CpuSourceTests
{
    private const string StatBefore = """
        cpu  10000 500 3000 80000 1000 0 200 0 0 0
        cpu0 5000 250 1500 40000 500 0 100 0 0 0
        intr 123456
        ctxt 7890
        """;

    // +1000 user, +500 system, +8000 idle, +500 iowait: 10000 jiffies, 8500 of them idle → 15 %.
    private const string StatAfter = """
        cpu  11000 500 3500 88000 1500 0 200 0 0 0
        cpu0 5500 250 1750 44000 750 0 100 0 0 0
        intr 123999
        ctxt 7999
        """;

    [Fact]
    public void ProcStatAggregateLineIsParsed()
    {
        var times = CpuSource.ParseProcStat(StatBefore);
        Assert.Equal(new CpuTimes(Idle: 81000, Total: 94700), times);
    }

    [Fact]
    public void LoadIsNonIdleShareOfDelta()
    {
        var load = CpuSource.LoadPercent(CpuSource.ParseProcStat(StatBefore)!.Value, CpuSource.ParseProcStat(StatAfter)!.Value);
        Assert.Equal(15, load!.Value, precision: 6);
    }

    [Fact]
    public void GuestColumnsAreNotCountedTwice()
    {
        var before = CpuSource.ParseProcStat("cpu  100 0 0 900 0 0 0 0 0 0\n")!.Value;
        var after = CpuSource.ParseProcStat("cpu  200 0 0 1800 0 0 0 0 5000 0\n")!.Value;
        Assert.Equal(10, CpuSource.LoadPercent(before, after)!.Value, precision: 6);
    }

    [Fact]
    public void CountersGoingBackwardsGiveNoLoad()
    {
        Assert.Null(CpuSource.LoadPercent(new CpuTimes(100, 1000), new CpuTimes(50, 900)));
    }

    [Fact]
    public void CpuInfoGivesModelCoresAndThreads()
    {
        var cpuinfo = new StringBuilder();
        for (var i = 0; i < 32; i++)
        {
            cpuinfo.AppendLine($"processor\t: {i}");
            cpuinfo.AppendLine("vendor_id\t: AuthenticAMD");
            cpuinfo.AppendLine("model name\t: AMD Ryzen 9 7950X3D 16-Core Processor");
            cpuinfo.AppendLine("physical id\t: 0");
            cpuinfo.AppendLine($"core id\t\t: {i % 16}");
            cpuinfo.AppendLine("cpu cores\t: 16");
            cpuinfo.AppendLine();
        }

        var info = CpuSource.ParseCpuInfo(cpuinfo.ToString());

        Assert.Equal("AMD Ryzen 9 7950X3D", info.Model);
        Assert.Equal("16 cores · 32 threads", info.Detail);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-14900K", "Intel Core i9-14900K")]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Intel Core i7-9700K")]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13600K", "Intel Core i5-13600K")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "AMD Ryzen 7 5800X3D")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "AMD Ryzen 7 7840HS")]
    public void ModelNamesAreCleanedUp(string raw, string expected) => Assert.Equal(expected, CpuSource.CleanModelName(raw));

    [Fact]
    public async Task SourceReadsLoadTemperatureAndRaplPowerFromFixtureTree()
    {
        using var tree = new TempTree();
        tree.Write("proc/stat", StatBefore)
            .Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD Ryzen 9 7950X3D 16-Core Processor\ncore id\t: 0\n\nprocessor\t: 1\nmodel name\t: AMD Ryzen 9 7950X3D 16-Core Processor\ncore id\t: 0\n")
            .WriteAll("sys/class/hwmon/hwmon1", ("name", "k10temp"), ("temp1_label", "Tctl"), ("temp1_input", "45250"), ("temp3_label", "Tccd1"), ("temp3_input", "39000"))
            .WriteAll("sys/class/hwmon/hwmon2", ("name", "nct6799"), ("temp1_input", "30000"))
            .WriteAll("sys/class/powercap/intel-rapl:0", ("name", "package-0"), ("energy_uj", "1000000000"), ("max_energy_range_uj", "262143328850"))
            .WriteAll("sys/class/powercap/intel-rapl:0:0", ("name", "core"), ("energy_uj", "5"));
        var time = new FakeTimeProvider();
        var source = new CpuSource(tree.Paths, time, NullLogger.Instance);

        var first = new Dictionary<string, double>();
        await source.ReadAsync(first, CancellationToken.None);

        tree.Write("proc/stat", StatAfter).Write("sys/class/powercap/intel-rapl:0/energy_uj", "1090000000\n");
        time.Advance(TimeSpan.FromSeconds(2));
        var second = new Dictionary<string, double>();
        await source.ReadAsync(second, CancellationToken.None);

        Assert.False(first.ContainsKey("cpu.load"));
        Assert.False(first.ContainsKey("cpu.power"));
        Assert.Equal(15, second["cpu.load"], precision: 6);
        Assert.Equal(45.25, second["cpu.temp"]);
        Assert.Equal(45, second["cpu.power"], precision: 6); // 90 J over 2 s

        var descriptors = source.Describe();
        Assert.Equal(["cpu.load", "cpu.temp", "cpu.power"], descriptors.Select(d => d.Id));
        Assert.All(descriptors, d => Assert.Equal("AMD Ryzen 9 7950X3D", d.Hardware));
        Assert.Equal("1 cores · 2 threads", descriptors[0].Detail);
        Assert.Equal("Package", descriptors[1].Label);
    }

    [Fact]
    public void CoretempPackageIsFound()
    {
        using var tree = new TempTree();
        tree.WriteAll("sys/class/hwmon/hwmon3", ("name", "coretemp"), ("temp2_label", "Core 0"), ("temp2_input", "40000"),
            ("temp1_label", "Package id 0"), ("temp1_input", "52000"));

        var path = CpuSource.FindTemperatureInput(HwmonScanner.Scan(tree.Paths));

        Assert.Equal(tree.PathOf("sys/class/hwmon/hwmon3/temp1_input"), path);
    }
}

public class RaplTests
{
    [Fact]
    public void EnergyDeltaHandlesWraparound()
    {
        Assert.Equal(500UL, RaplReader.EnergyDelta(1000, 1500, 262143328850));
        Assert.Equal(828_850UL, RaplReader.EnergyDelta(262_143_000_000, 500_000, 262_143_328_850));
    }

    [Fact]
    public void PowerAcrossCounterWrapIsPositive()
    {
        using var tree = new TempTree();
        tree.WriteAll("sys/class/powercap/intel-rapl:0", ("name", "package-0"), ("energy_uj", "262143000000"), ("max_energy_range_uj", "262143328850"));
        var reader = new RaplReader(tree.Paths);
        var start = DateTimeOffset.UnixEpoch.AddDays(20000);

        Assert.Equal(RaplStatus.Warming, reader.Read(start, out _));
        tree.Write("sys/class/powercap/intel-rapl:0/energy_uj", "99671150\n");
        var status = reader.Read(start.AddSeconds(2), out var watts);

        Assert.Equal(RaplStatus.Ok, status);
        Assert.Equal(50, watts!.Value, precision: 6); // (328850 + 99671150) µJ = 100 J over 2 s
    }

    [Fact]
    public void MultiplePackagesAreSummed()
    {
        using var tree = new TempTree();
        tree.WriteAll("sys/class/powercap/intel-rapl:0", ("name", "package-0"), ("energy_uj", "0"), ("max_energy_range_uj", "1000000000000"))
            .WriteAll("sys/class/powercap/intel-rapl:1", ("name", "package-1"), ("energy_uj", "0"), ("max_energy_range_uj", "1000000000000"));
        var reader = new RaplReader(tree.Paths);
        var start = DateTimeOffset.UnixEpoch;
        reader.Read(start, out _);
        tree.Write("sys/class/powercap/intel-rapl:0/energy_uj", "30000000").Write("sys/class/powercap/intel-rapl:1/energy_uj", "10000000");

        reader.Read(start.AddSeconds(1), out var watts);

        Assert.Equal(40, watts!.Value, precision: 6);
    }

    [Fact]
    public void MissingPowercapIsUnavailable()
    {
        using var tree = new TempTree();
        Assert.Equal(RaplStatus.Unavailable, new RaplReader(tree.Paths).Read(DateTimeOffset.UnixEpoch, out var watts));
        Assert.Null(watts);
    }

    [Fact]
    public void UnreadableEnergyIsPermissionDenied()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        using var tree = new TempTree();
        tree.WriteAll("sys/class/powercap/intel-rapl:0", ("name", "package-0"), ("energy_uj", "1"), ("max_energy_range_uj", "100"));
        File.SetUnixFileMode(tree.PathOf("sys/class/powercap/intel-rapl:0/energy_uj"), UnixFileMode.None);

        Assert.Equal(RaplStatus.PermissionDenied, new RaplReader(tree.Paths).Read(DateTimeOffset.UnixEpoch, out _));
    }
}

public class MemorySourceTests
{
    private const string Meminfo = """
        MemTotal:       32768000 kB
        MemFree:         1024000 kB
        MemAvailable:   26787840 kB
        Buffers:          512000 kB
        Cached:         20000000 kB
        """;

    [Fact]
    public void UsedIsTotalMinusAvailable()
    {
        var usage = MemorySource.ParseMeminfo(Meminfo)!.Value;

        Assert.Equal(31.25, usage.TotalGb, precision: 6);
        Assert.Equal(5.703125, usage.UsedGb, precision: 6);
    }

    [Fact]
    public async Task SourceReportsRamWithCapacity()
    {
        using var tree = new TempTree();
        tree.Write("proc/meminfo", Meminfo);
        var source = new MemorySource(tree.Paths);
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);

        var descriptor = Assert.Single(source.Describe());
        Assert.Equal("mem.ram", descriptor.Id);
        Assert.Equal(SensorKind.Memory, descriptor.Kind);
        Assert.Equal("GB", descriptor.Unit);
        Assert.Equal(31.2, descriptor.Capacity);
        Assert.Equal(5.703125, values["mem.ram"], precision: 6);
    }

    [Fact]
    public void MissingAvailableIsNull() => Assert.Null(MemorySource.ParseMeminfo("MemTotal: 100 kB\n"));
}
