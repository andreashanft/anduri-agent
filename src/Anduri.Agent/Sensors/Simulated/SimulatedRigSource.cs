namespace Anduri.Agent.Sensors.Simulated;

/// <summary>
/// A plausible, smoothly varying water-cooled gaming PC at idle, for development and demos.
/// Ids, descriptors and dynamics mirror the iPad app's <c>MockSensorProvider</c>/<c>RigSimulation</c>.
/// </summary>
public sealed class SimulatedRigSource : ISensorSource, IHistoryBackfill
{
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly RigSimulation simulation;
    private DateTimeOffset? lastStep;

    public SimulatedRigSource(TimeProvider time, ulong seed = 0xA11D_0121)
    {
        this.time = time;
        simulation = new RigSimulation(seed);
    }

    public string Name => "simulated";

    public IReadOnlyList<SensorDescriptor> Describe() => Catalog;

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var dt = lastStep is { } last ? Math.Clamp((now - last).TotalSeconds, 0.1, 10) : 0.5;
            lastStep = now;
            simulation.Step(dt);
            foreach (var (id, value) in simulation.Values())
                values[id] = value;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the simulation through the past at 1-second steps so charts are full right after start.</summary>
    public IEnumerable<(DateTimeOffset Timestamp, IReadOnlyDictionary<string, double> Values)> Backfill(DateTimeOffset now, TimeSpan duration)
    {
        var result = new List<(DateTimeOffset, IReadOnlyDictionary<string, double>)>();
        lock (gate)
        {
            var steps = (int)duration.TotalSeconds;
            for (var index = 0; index < steps; index++)
            {
                simulation.Step(1);
                result.Add((now.AddSeconds(index - steps), simulation.Values()));
            }

            lastStep = now;
        }

        return result;
    }

    public static IReadOnlyList<SensorDescriptor> Catalog { get; } = CreateCatalog();

    private static List<SensorDescriptor> CreateCatalog()
    {
        const string cpu = "Ryzen 9 7950X3D";
        const string gpu = "RTX 4090";
        const string loop = "Custom loop";
        static SensorDescriptor D(string id, string name, SensorKind kind) => SensorDescriptor.Create(id, name, kind);

        return
        [
            D("cpu.load", "CPU load", SensorKind.Load) with { Hardware = cpu, Label = "CPU", Detail = "16 cores · 32 threads" },
            D("gpu.load", "GPU load", SensorKind.Load) with { Hardware = gpu, Label = "GPU" },
            D("cpu.temp", "CPU package temperature", SensorKind.Temperature) with { Hardware = cpu, Label = "Package" },
            D("gpu.temp", "GPU core temperature", SensorKind.Temperature) with { Hardware = gpu, Label = "Core" },
            D("gpu.hotspot", "GPU hotspot temperature", SensorKind.Temperature) with { Hardware = gpu, Label = "Hotspot" },
            D("cpu.power", "CPU package power", SensorKind.Power) with { Hardware = cpu, Label = "CPU" },
            D("gpu.power", "GPU board power", SensorKind.Power) with { Hardware = gpu, Label = "GPU" },
            D("loop.coolant", "Coolant temperature", SensorKind.Temperature) with { Hardware = loop, Label = "Water" },
            D("loop.ambient", "Ambient temperature", SensorKind.Temperature) with { Hardware = loop, Label = "Air" },
            D("loop.flow", "Coolant flow", SensorKind.Flow) with { Hardware = loop, Label = "Flow" },
            D("fan.rad280", "Radiator 280 fans", SensorKind.Rpm) with { Hardware = loop, Label = "Radiator 280", ShortLabel = "Fan 280" },
            D("fan.rad240", "Radiator 240 fans", SensorKind.Rpm) with { Hardware = loop, Label = "Radiator 240", ShortLabel = "Fan 240" },
            D("loop.pump", "Pump", SensorKind.Rpm) with { Hardware = loop, Label = "Pump" },
            D("mem.ram", "System memory used", SensorKind.Memory) with { Label = "System", Capacity = 32 },
            D("mem.vram", "GPU memory used", SensorKind.Memory) with { Hardware = gpu, Label = "VRAM", Capacity = 24 },
            D("gpu.clock", "GPU core clock", SensorKind.Frequency) with { Hardware = gpu, Label = "Core", Maximum = 2520 },
            D("gpu.memclock", "GPU memory clock", SensorKind.Frequency) with { Hardware = gpu, Label = "Mem" },
            D("drive.c", "System", SensorKind.Storage) with { Label = "C:", Capacity = 1000 },
            D("drive.d", "Games", SensorKind.Storage) with { Label = "D:", Capacity = 1600 },
            D("drive.e", "Media", SensorKind.Storage) with { Label = "E:", Capacity = 4000 },
            D("drive.f", "Scratch", SensorKind.Storage) with { Label = "F:", Capacity = 2000 },
            D("net.down", "Download", SensorKind.Network) with { Label = "Down", Detail = "2.5 GbE" },
            D("net.up", "Upload", SensorKind.Network) with { Label = "Up", Detail = "2.5 GbE" },
            D("psu.12v", "+12V", SensorKind.Voltage) with { Label = "PSU rail" },
            SensorDescriptor.Create("reservoir.leakshield.filled", "Reservoir fill level", SensorKind.Other, "ml") with { Hardware = loop, Label = "Filled" },
            SensorDescriptor.Create("reservoir.leakshield.volume", "Reservoir volume", SensorKind.Other, "ml") with { Hardware = loop, Label = "Volume" },
        ];
    }
}

/// <summary>
/// Loads drive power, power drives temperatures through first-order lags, coolant temperature drives the fan curves.
/// Same model as the app's idle scenario.
/// </summary>
internal sealed class RigSimulation(ulong seed)
{
    private readonly SplitMix64 rng = new(seed);
    private double elapsed;

    private double cpuBurst;
    private double gpuBurst;
    private bool downloadActive = true;
    private double downloadRate = 11.5e6;
    private double downloadRemaining = 45;

    private double cpuLoad = 2, gpuLoad = 15, cpuPower = 28, gpuPower = 21;
    private double cpuTemp = 37, gpuTemp = 28, hotspot = 36, ambient = 19.5, coolant = 28;
    private double flow = 79, pump = 2410, fan280 = 692, fan240 = 833;
    private double ram = 5.7, vram = 1.9, gpuClock = 210, memClock = 405;
    private readonly double[] drives = [412, 1_300, 3_900, 120];
    private double download = 11.5e6, upload = 290.9e3, rail12V = 12.05;
    private double reservoirFilled = 212;
    private const double ReservoirVolume = 250;

    public void Step(double dt)
    {
        elapsed += dt;

        // Loads: idle base plus decaying random bursts.
        if (Chance(0.06 * dt)) cpuBurst += Uniform(15, 55);
        if (Chance(0.07 * dt)) gpuBurst += Uniform(10, 40);
        cpuBurst *= Math.Pow(0.55, dt);
        gpuBurst *= Math.Pow(0.6, dt);
        cpuLoad = Approach(cpuLoad, Math.Clamp(3 + cpuBurst + Gauss() * 1.2, 0.5, 100), 0.7, dt);
        gpuLoad = Approach(gpuLoad, Math.Clamp(12 + gpuBurst + Gauss() * 1.5, 0.5, 100), 0.7, dt);

        // Power follows load quickly.
        cpuPower = Approach(cpuPower, 26 + cpuLoad * 1.35 + Gauss() * 0.6, 0.6, dt);
        gpuPower = Approach(gpuPower, 14 + 430 * Math.Pow(gpuLoad / 100, 2) + Gauss() * 1.2, 0.6, dt);
        var totalPower = cpuPower + gpuPower;

        // Loop: pump, flow, and coolant with a slow time constant.
        pump = Approach(pump, 2410 + Gauss() * 12, 0.3, dt);
        flow = Math.Max(0, pump * 0.0328 + Gauss() * 0.4);
        ambient = 19.5 + 0.4 * Math.Sin(elapsed / 1_800);
        coolant = Lag(coolant, ambient + 7.5 + totalPower * 0.028, 90, dt);

        // Radiator fan curves on coolant temperature.
        var heat = Math.Max(0, coolant - 22);
        fan280 = Approach(fan280, 450 + heat * 40 + Gauss() * 4, 0.2, dt);
        fan240 = Approach(fan240, 600 + heat * 39 + Gauss() * 4, 0.2, dt);

        // Silicon reacts within seconds.
        cpuTemp = Lag(cpuTemp, coolant + 4 + cpuPower * 0.2 + Gauss() * 0.4, 2.5, dt);
        gpuTemp = Lag(gpuTemp, coolant + gpuPower * 0.055 + Gauss() * 0.3, 4, dt);
        hotspot = gpuTemp + 7 + gpuPower * 0.03 + Gauss() * 0.3;

        ram = Approach(ram, 5.7 + Gauss() * 0.02, 0.05, dt);
        vram = Approach(vram, 1.9 + gpuLoad / 100 * 9.6, 0.1, dt);

        // GPU clocks jump to boost under load.
        var clockTarget = gpuLoad > 40 ? 2_520 : 210 + Math.Min(2_310, gpuBurst * 60);
        gpuClock = Approach(gpuClock, clockTarget, 0.8, dt);
        memClock = gpuLoad > 40 || gpuBurst > 8 ? 1_313 : 405;

        drives[0] += 0.00005 * dt;

        // Network: alternating download sessions and idle periods with short bursts; upload is a trickle with spikes.
        downloadRemaining -= dt;
        if (downloadRemaining <= 0)
        {
            downloadActive = !downloadActive;
            downloadRemaining = downloadActive ? Uniform(15, 90) : Uniform(20, 150);
            downloadRate = Uniform(4e6, 26e6);
        }
        var downloadTarget = downloadActive
            ? downloadRate * Math.Max(0.2, 1 + Gauss() * 0.18) * (Chance(0.1 * dt) ? 0.45 : 1)
            : 45e3 + Math.Abs(Gauss()) * 50e3 + (Chance(0.12 * dt) ? Uniform(0.8e6, 7e6) : 0);
        download = Math.Max(0, Approach(download, downloadTarget, 0.75, dt));
        upload = Math.Max(0, 290e3 + Gauss() * 35e3 + (Chance(0.06 * dt) ? Uniform(0.3e6, 1.6e6) : 0));

        rail12V = 12.05 + Gauss() * 0.008;
        // Deterministic, so the random sequence (and every other value) stays as it was.
        reservoirFilled = 212 + 1.5 * Math.Sin(elapsed / 600) + 0.4 * Math.Sin(elapsed * 0.7);
    }

    public IReadOnlyDictionary<string, double> Values() => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["cpu.load"] = cpuLoad, ["gpu.load"] = gpuLoad,
        ["cpu.temp"] = cpuTemp, ["gpu.temp"] = gpuTemp, ["gpu.hotspot"] = hotspot,
        ["cpu.power"] = cpuPower, ["gpu.power"] = gpuPower,
        ["loop.coolant"] = coolant, ["loop.ambient"] = ambient, ["loop.flow"] = flow,
        ["fan.rad280"] = fan280, ["fan.rad240"] = fan240, ["loop.pump"] = pump,
        ["mem.ram"] = ram, ["mem.vram"] = vram,
        ["gpu.clock"] = gpuClock, ["gpu.memclock"] = memClock,
        ["drive.c"] = drives[0], ["drive.d"] = drives[1], ["drive.e"] = drives[2], ["drive.f"] = drives[3],
        ["net.down"] = download, ["net.up"] = upload,
        ["psu.12v"] = rail12V,
        ["reservoir.leakshield.filled"] = reservoirFilled, ["reservoir.leakshield.volume"] = ReservoirVolume,
    };

    private double Uniform(double low, double high) => low + (high - low) * rng.NextDouble();

    private bool Chance(double probability) => rng.NextDouble() < probability;

    /// <summary>Standard normal sample (Box–Muller).</summary>
    private double Gauss()
    {
        var u1 = Math.Max(rng.NextDouble(), double.Epsilon);
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by <paramref name="rate"/> per second.</summary>
    private static double Approach(double current, double target, double rate, double dt) =>
        current + (target - current) * (1 - Math.Pow(1 - rate, dt));

    /// <summary>First-order lag with time constant <paramref name="tau"/> seconds.</summary>
    private static double Lag(double current, double target, double tau, double dt) =>
        current + (target - current) * (1 - Math.Exp(-dt / tau));
}

/// <summary>Small seedable PRNG, the same algorithm as the app's simulation.</summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    public ulong Next()
    {
        unchecked
        {
            state += 0x9E37_79B9_7F4A_7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1) from the top 53 bits.</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
}
