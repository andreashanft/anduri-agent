using System.Globalization;
using System.Text.RegularExpressions;

namespace Anduri.Agent.Sensors.Linux;

/// <summary>Total jiffies and idle jiffies from the aggregate "cpu" line of <c>/proc/stat</c>.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

public sealed record CpuInfo(string? Model, int Cores, int Threads)
{
    public string Detail => $"{Cores} cores · {Threads} threads";
}

/// <summary><c>cpu.load</c>, <c>cpu.temp</c> and <c>cpu.power</c>.</summary>
public sealed partial class CpuSource : ISensorSource
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(1);
    private static readonly string[] CpuChips = ["k10temp", "zenpower", "coretemp"];

    private readonly HostPaths paths;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly RaplReader rapl;
    private readonly CpuInfo info;
    private CpuTimes? previousTimes;
    private string? temperaturePath;
    private DateTimeOffset nextTemperatureScan = DateTimeOffset.MinValue;
    private bool raplReported;
    private IReadOnlyList<SensorDescriptor> descriptors = [];

    public CpuSource(HostPaths paths, TimeProvider time, ILogger logger)
    {
        this.paths = paths;
        this.time = time;
        this.logger = logger;
        rapl = new RaplReader(paths);
        info = SysFs.TryReadText(paths.Proc("cpuinfo")) is { } text ? ParseCpuInfo(text) : new CpuInfo(null, 0, 0);
    }

    public string Name => "cpu";

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        var statText = SysFs.TryReadText(paths.Proc("stat"))
            ?? throw new SensorSourceUnavailableException("/proc/stat isn't readable");
        var times = ParseProcStat(statText);
        if (times is { } current && previousTimes is { } previous && LoadPercent(previous, current) is { } load)
            values["cpu.load"] = load;
        previousTimes = times;

        var now = time.GetUtcNow();
        if (temperaturePath is null && now >= nextTemperatureScan)
        {
            temperaturePath = FindTemperatureInput(HwmonScanner.Scan(paths));
            nextTemperatureScan = now + RescanInterval;
        }
        if (temperaturePath is not null && SysFs.TryReadLong(temperaturePath) is { } milli)
            values["cpu.temp"] = milli / 1000.0;

        var raplStatus = rapl.Read(now, out var watts);
        if (watts is { } w)
            values["cpu.power"] = w;
        if (raplStatus == RaplStatus.PermissionDenied && !raplReported)
        {
            raplReported = true;
            LogRaplDenied(logger);
        }

        var hardware = info.Model;
        var list = new List<SensorDescriptor>
        {
            SensorDescriptor.Create("cpu.load", "CPU load", SensorKind.Load) with
            {
                Hardware = hardware,
                Label = "CPU",
                Detail = info.Threads > 0 ? info.Detail : null,
            },
        };
        if (temperaturePath is not null)
            list.Add(SensorDescriptor.Create("cpu.temp", "CPU package temperature", SensorKind.Temperature) with { Hardware = hardware, Label = "Package" });
        if (raplStatus != RaplStatus.Unavailable && raplStatus != RaplStatus.PermissionDenied)
            list.Add(SensorDescriptor.Create("cpu.power", "CPU package power", SensorKind.Power) with { Hardware = hardware, Label = "CPU" });
        descriptors = list;
        return ValueTask.CompletedTask;
    }

    internal static CpuTimes? ParseProcStat(string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("cpu "))
                continue;

            // user nice system idle iowait irq softirq steal guest guest_nice. Guest time is already
            // included in user and nice, so only the first eight columns count toward the total.
            ulong total = 0, idle = 0;
            var column = 0;
            foreach (var range in line[4..].Split(' '))
            {
                var part = line[4..][range];
                if (part.IsEmpty)
                    continue;
                if (!ulong.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    return null;
                if (column < 8)
                    total += value;
                if (column is 3 or 4)
                    idle += value;
                column++;
            }

            return column >= 4 ? new CpuTimes(idle, total) : null;
        }

        return null;
    }

    internal static double? LoadPercent(CpuTimes previous, CpuTimes current)
    {
        // Counters only go backwards when CPUs are hot-unplugged; skip that interval.
        if (current.Total <= previous.Total || current.Idle < previous.Idle)
            return null;
        var total = (double)(current.Total - previous.Total);
        var idle = (double)(current.Idle - previous.Idle);
        return Math.Clamp(100 * (1 - idle / total), 0, 100);
    }

    internal static CpuInfo ParseCpuInfo(string text)
    {
        string? model = null;
        var threads = 0;
        var cores = new HashSet<(string, string)>();
        var coresPerSocket = new Dictionary<string, int>();
        string physicalId = "0", coreId = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var separator = rawLine.IndexOf(':');
            if (separator < 0)
            {
                if (coreId.Length > 0)
                    cores.Add((physicalId, coreId));
                physicalId = "0";
                coreId = "";
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            switch (key)
            {
                case "processor":
                    threads++;
                    break;
                case "model name":
                    model ??= value;
                    break;
                case "physical id":
                    physicalId = value;
                    break;
                case "core id":
                    coreId = value;
                    break;
                case "cpu cores" when int.TryParse(value, CultureInfo.InvariantCulture, out var count):
                    coresPerSocket[physicalId] = count;
                    break;
            }
        }
        if (coreId.Length > 0)
            cores.Add((physicalId, coreId));

        var coreCount = cores.Count > 0 ? cores.Count : coresPerSocket.Count > 0 ? coresPerSocket.Values.Sum() : threads;
        return new CpuInfo(model is null ? null : CleanModelName(model), coreCount, threads);
    }

    /// <summary>"AMD Ryzen 9 7950X3D 16-Core Processor" → "AMD Ryzen 9 7950X3D", "Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz" → "Intel Core i9-9900K".</summary>
    internal static string CleanModelName(string model)
    {
        var name = model.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        name = CpuAtFrequency().Replace(name, "");
        name = CoreCountSuffix().Replace(name, "");
        name = GraphicsSuffix().Replace(name, "");
        name = GenerationPrefix().Replace(name, "");
        name = name.Replace(" Processor", "", StringComparison.Ordinal);
        return Whitespace().Replace(name, " ").Trim();
    }

    internal static string? FindTemperatureInput(IReadOnlyList<HwmonChip> chips)
    {
        foreach (var chipName in CpuChips)
        {
            foreach (var chip in chips.Where(c => c.Name == chipName))
            {
                var temps = chip.OfType("temp").ToList();
                // Tdie is the real die temperature; Tctl can carry a fan-curve offset on some older Ryzen models.
                var channel = temps.FirstOrDefault(t => t.Label == "Tdie")
                    ?? temps.FirstOrDefault(t => t.Label == "Tctl")
                    ?? temps.FirstOrDefault(t => t.Label?.StartsWith("Package id", StringComparison.Ordinal) == true)
                    ?? (chipName == "k10temp" ? temps.FirstOrDefault(t => t.Index == 1) : null);
                if (channel is not null)
                    return channel.InputPath;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CPU package power (RAPL energy_uj) is only readable by root; cpu.power is left out. See the README for a udev rule.")]
    private static partial void LogRaplDenied(ILogger logger);

    [GeneratedRegex(@"\s+CPU\s+@\s+[\d.]+\s*[GM]Hz", RegexOptions.IgnoreCase)]
    private static partial Regex CpuAtFrequency();

    [GeneratedRegex(@"\s+\d+-Core(\s+Processor)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoreCountSuffix();

    [GeneratedRegex(@"\s+w/\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsSuffix();

    [GeneratedRegex(@"^\s*\d+th Gen\s+", RegexOptions.IgnoreCase)]
    private static partial Regex GenerationPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
