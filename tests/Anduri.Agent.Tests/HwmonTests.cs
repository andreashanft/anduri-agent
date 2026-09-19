using Anduri.Agent.Sensors;
using Anduri.Agent.Sensors.Linux;
using Microsoft.Extensions.Time.Testing;

namespace Anduri.Agent.Tests;

public class HwmonTests
{
    /// <summary>
    /// A desktop with a Nuvoton Super I/O, an NVMe drive, and a D5 Next, Octo and High Flow Next on the
    /// aquacomputer_d5next driver, using the driver's labels and raw units (m°C, dL/h, µW, mV).
    /// An empty *_input file stands in for a read that fails with ENODATA (unconnected sensor).
    /// </summary>
    private static TempTree CreateTree()
    {
        var tree = new TempTree();
        tree.WriteAll("sys/class/hwmon/hwmon0", ("name", "k10temp"), ("temp1_label", "Tctl"), ("temp1_input", "45000"))
            .WriteAll("sys/class/hwmon/hwmon1", ("name", "nvme"), ("temp1_label", "Composite"), ("temp1_input", "38850"), ("device/model", "Samsung SSD 990 PRO 2TB"))
            .WriteAll("sys/class/hwmon/hwmon2", ("name", "nct6799"),
                ("fan1_input", "0"), ("fan2_input", "712"), ("fan3_input", "1043"), ("fan4_input", "0"),
                ("temp1_label", "SYSTIN"), ("temp1_input", "35000"),
                ("temp2_label", "CPUTIN"), ("temp2_input", "41500"),
                ("temp3_label", "AUXTIN0"), ("temp3_input", "-62000"),
                ("temp4_label", "AUXTIN1"), ("temp4_input", "127000"))
            .WriteAll("sys/class/hwmon/hwmon3", ("name", "acpitz"), ("temp1_input", "27800"))
            .WriteAll("sys/class/hwmon/hwmon4", ("name", "d5next"),
                ("temp1_label", "Coolant temp"), ("temp1_input", "28350"),
                ("temp2_label", "Virtual sensor 1"), ("temp2_input", ""),
                ("fan1_label", "Pump speed"), ("fan1_input", "2410"),
                ("fan2_label", "Fan speed"), ("fan2_input", "0"),
                ("power1_label", "Pump power"), ("power1_input", "2340000"),
                ("power2_label", "Fan power"), ("power2_input", "0"),
                ("in0_label", "Pump voltage"), ("in0_input", "12010"),
                ("in2_label", "+5V voltage"), ("in2_input", "5020"),
                ("in3_label", "+12V voltage"), ("in3_input", "12060"),
                ("curr1_label", "Pump current"), ("curr1_input", "190"))
            .WriteAll("sys/class/hwmon/hwmon5", ("name", "octo"),
                ("temp1_label", "Sensor 1"), ("temp1_input", "27800"),
                ("temp2_label", "Sensor 2"), ("temp2_input", ""),
                ("temp5_label", "Virtual sensor 1"), ("temp5_input", ""),
                ("fan1_label", "Fan 1 speed"), ("fan1_input", "812"),
                ("fan2_label", "Fan 2 speed"), ("fan2_input", "0"),
                ("fan9_label", "Flow speed [dL/h]"), ("fan9_input", "792"),
                ("power1_label", "Fan 1 power"), ("power1_input", "1200000"))
            .WriteAll("sys/class/hwmon/hwmon6", ("name", "highflownext"),
                ("temp1_label", "Coolant temp"), ("temp1_input", "28100"),
                ("temp2_label", "External sensor"), ("temp2_input", "21500"),
                ("fan1_label", "Flow [dL/h]"), ("fan1_input", "801"),
                ("fan2_label", "Water quality [%]"), ("fan2_input", "98"),
                ("fan3_label", "Conductivity [nS/cm]"), ("fan3_input", "22500"),
                ("power1_label", "Dissipated power"), ("power1_input", "4294967235"));
        return tree;
    }

    [Fact]
    public void ScannerFindsChipsAndLabels()
    {
        using var tree = CreateTree();

        var chips = HwmonScanner.Scan(tree.Paths);

        Assert.Equal(["acpitz", "d5next", "highflownext", "k10temp", "nct6799", "nvme", "octo"], chips.Select(c => c.Name));
        var octo = chips.Single(c => c.Name == "octo");
        Assert.Equal("Flow speed [dL/h]", octo.Channels.Single(c => c.Key == "fan9").Label);
        Assert.Equal("Samsung SSD 990 PRO 2TB", chips.Single(c => c.Name == "nvme").DeviceModel);
    }

    [Fact]
    public void SecondChipWithSameNameGetsSuffix()
    {
        using var tree = new TempTree();
        tree.WriteAll("sys/class/hwmon/hwmon3", ("name", "nvme"), ("temp1_input", "40000"))
            .WriteAll("sys/class/hwmon/hwmon7", ("name", "nvme"), ("temp1_input", "41000"));

        Assert.Equal(["nvme", "nvme_2"], HwmonScanner.Scan(tree.Paths).Select(c => c.Instance));
    }

    [Fact]
    public async Task AquacomputerChannelsMapToWellKnownIdsAndUnits()
    {
        using var tree = CreateTree();
        var source = new AquacomputerSource(tree.Paths, new FakeTimeProvider());
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);
        var catalog = source.Describe().ToDictionary(d => d.Id);

        var expected = new Dictionary<string, double>
        {
            ["loop.coolant"] = 28.35,           // D5 Next "Coolant temp", m°C
            ["loop.pump"] = 2410,               // D5 Next "Pump speed"
            ["power.d5next.pump"] = 2.34,       // µW → W
            ["voltage.d5next.5v"] = 5.02,       // mV → V
            ["voltage.d5next.12v"] = 12.06,
            ["temp.highflownext.coolant"] = 28.1, // second coolant sensor
            ["loop.ambient"] = 21.5,            // "External sensor"
            ["loop.flow"] = 80.1,               // High Flow Next "Flow [dL/h]" → l/h
            ["quality.highflownext"] = 98,
            ["conductivity.highflownext"] = 22.5, // nS/cm → µS/cm
            ["temp.octo.1"] = 27.8,
            ["fan.aqua.1"] = 812,               // Octo fans come first
            ["flow.octo.9"] = 79.2,             // second flow sensor
        };
        Assert.Equal(expected.Keys.Order(), values.Keys.Order());
        foreach (var (id, value) in expected)
            Assert.Equal(value, values[id], precision: 6);
        Assert.Equal(expected.Keys.Order(), catalog.Keys.Order());

        Assert.Equal(SensorKind.Flow, catalog["loop.flow"].Kind);
        Assert.Equal("l/h", catalog["loop.flow"].Unit);
        Assert.Equal("Aquacomputer High Flow Next", catalog["loop.flow"].Hardware);
        Assert.Equal("Water", catalog["loop.coolant"].Label);
        Assert.Equal("Aquacomputer D5 Next", catalog["loop.pump"].Hardware);
        Assert.Equal("Sensor 1", catalog["temp.octo.1"].Label);
        Assert.Equal("Fan 1", catalog["fan.aqua.1"].Label);
        Assert.Equal("µS/cm", catalog["conductivity.highflownext"].Unit);
        Assert.Equal(SensorKind.Other, catalog["conductivity.highflownext"].Kind);
    }

    [Fact]
    public async Task UnconnectedAquacomputerSensorAppearsOnceItReads()
    {
        using var tree = CreateTree();
        var time = new FakeTimeProvider();
        var source = new AquacomputerSource(tree.Paths, time);
        await source.ReadAsync(new Dictionary<string, double>(), CancellationToken.None);

        tree.Write("sys/class/hwmon/hwmon5/temp2_input", "24500\n");
        tree.Write("sys/class/hwmon/hwmon5/fan2_input", "650\n");
        var values = new Dictionary<string, double>();
        await source.ReadAsync(values, CancellationToken.None);

        Assert.Equal(24.5, values["temp.octo.2"]);
        Assert.Equal(650, values["fan.aqua.2"]);
        Assert.Contains(source.Describe(), d => d.Id == "temp.octo.2");
    }

    [Fact]
    public async Task GenericHwmonSkipsUnconnectedFansAndBogusTemperatures()
    {
        using var tree = CreateTree();
        var source = new HwmonSource(tree.Paths, new FakeTimeProvider());
        var values = new Dictionary<string, double>();

        await source.ReadAsync(values, CancellationToken.None);
        var catalog = source.Describe().ToDictionary(d => d.Id);

        Assert.Equal(["fan.nct6799.2", "fan.nct6799.3", "temp.nct6799.1", "temp.nct6799.2", "temp.nvme.1"], catalog.Keys.Order());
        Assert.Equal(712, values["fan.nct6799.2"]);
        Assert.Equal(41.5, values["temp.nct6799.2"]);
        Assert.Equal("CPUTIN", catalog["temp.nct6799.2"].Label);
        Assert.Equal("Fan 2", catalog["fan.nct6799.2"].Label);
        Assert.Equal("Nuvoton NCT6799", catalog["fan.nct6799.2"].Hardware);
        Assert.Equal("Samsung SSD 990 PRO 2TB", catalog["temp.nvme.1"].Hardware);
        Assert.DoesNotContain(catalog.Keys, id => id.Contains("d5next") || id.Contains("octo") || id.Contains("k10temp") || id.Contains("acpitz"));
    }

    [Fact]
    public async Task FanThatStopsStaysInCatalog()
    {
        using var tree = CreateTree();
        var source = new HwmonSource(tree.Paths, new FakeTimeProvider());
        await source.ReadAsync(new Dictionary<string, double>(), CancellationToken.None);

        tree.Write("sys/class/hwmon/hwmon2/fan2_input", "0\n");
        var values = new Dictionary<string, double>();
        await source.ReadAsync(values, CancellationToken.None);

        Assert.Equal(0, values["fan.nct6799.2"]);
        Assert.Contains(source.Describe(), d => d.Id == "fan.nct6799.2");
    }
}
