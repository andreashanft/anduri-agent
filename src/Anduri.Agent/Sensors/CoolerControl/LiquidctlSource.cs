using System.Diagnostics;
using System.Text.Json;
using Anduri.Agent.Configuration;
using Anduri.Agent.Sensors.Linux;

namespace Anduri.Agent.Sensors.CoolerControl;

/// <summary>
/// Fallback for liquidctl-supported coolers (AIOs, pumps) when CoolerControl isn't running:
/// polls <c>liquidctl --json status</c> every few seconds as <c>lc.&lt;device&gt;.&lt;key&gt;</c>.
/// </summary>
/// <remarks>Stays idle while CoolerControl works, because both talking to the same USB device at once confuses some firmware.</remarks>
public sealed class LiquidctlSource(LiquidctlConfig config, CoolerControlSource? coolerControl) : ISensorSource
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private IReadOnlyList<SensorDescriptor> descriptors = [];
    private string? executable;

    public string Name => "liquidctl";

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, config.Interval));

    public IReadOnlyList<SensorDescriptor> Describe() => descriptors;

    public async ValueTask ReadAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
    {
        if (coolerControl?.IsWorking == true)
        {
            descriptors = [];
            return;
        }

        executable ??= Executables.Find(config.Path)
            ?? throw new SensorSourceUnavailableException("liquidctl isn't installed");

        var output = await RunAsync(executable, cancellationToken);
        List<LiquidctlDevice>? devices;
        try
        {
            devices = JsonSerializer.Deserialize(output, LiquidctlJsonContext.Default.ListLiquidctlDevice);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("liquidctl printed invalid JSON; version 1.9 or later is needed for --json", ex);
        }

        var mapped = Map(devices ?? []);
        foreach (var (descriptor, value) in mapped)
            values[descriptor.Id] = value;
        var described = mapped.Select(m => m.Descriptor).ToList();
        if (!descriptors.SequenceEqual(described))
            descriptors = described;
    }

    internal static IReadOnlyList<(SensorDescriptor Descriptor, double Value)> Map(IReadOnlyList<LiquidctlDevice> devices)
    {
        var result = new List<(SensorDescriptor, double)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var deviceSlugs = new HashSet<string>(StringComparer.Ordinal);

        void Emit(SensorDescriptor descriptor, double value)
        {
            if (taken.Add(descriptor.Id))
                result.Add((descriptor, value));
        }

        foreach (var device in devices)
        {
            var hardware = device.Description ?? "liquidctl device";
            // "NZXT Kraken X (X53, X63 or X73)" → "nzxt_kraken_x"
            var baseName = hardware.Split('(')[0];
            var slug = SensorIds.Unique(SensorIds.Slug(baseName), deviceSlugs);

            foreach (var item in device.Status ?? [])
            {
                if (item.Value.ValueKind != JsonValueKind.Number)
                    continue;
                var value = item.Value.GetDouble();
                var id = $"lc.{slug}.{SensorIds.Slug(item.Key)}";
                var key = item.Key;

                switch (item.Unit)
                {
                    case "°C":
                        if (key.Contains("liquid", StringComparison.OrdinalIgnoreCase) || key.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("coolant", StringComparison.OrdinalIgnoreCase))
                            Emit(SensorDescriptor.Create("loop.coolant", "Coolant temperature", SensorKind.Temperature) with { Hardware = hardware, Label = "Water" }, value);
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Temperature) with { Hardware = hardware, Label = ShortKey(key) }, value);
                        break;
                    case "rpm":
                        if (key.StartsWith("Pump", StringComparison.OrdinalIgnoreCase))
                            Emit(SensorDescriptor.Create("loop.pump", "Pump", SensorKind.Rpm) with { Hardware = hardware, Label = "Pump" }, value);
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Rpm) with { Hardware = hardware, Label = ShortKey(key) }, value);
                        break;
                    case "dL/h" or "l/h" or "L/h":
                        var litres = item.Unit == "dL/h" ? value / 10 : value;
                        Emit(SensorDescriptor.Create("loop.flow", "Coolant flow", SensorKind.Flow) with { Hardware = hardware, Label = "Flow" }, litres);
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Flow) with { Hardware = hardware, Label = ShortKey(key) }, litres);
                        break;
                    case "%":
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Other, "%") with { Hardware = hardware, Label = ShortKey(key) }, value);
                        break;
                    case "V":
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Voltage) with { Hardware = hardware, Label = ShortKey(key) }, value);
                        break;
                    case "W":
                        Emit(SensorDescriptor.Create(id, key, SensorKind.Power) with { Hardware = hardware, Label = ShortKey(key) }, value);
                        break;
                }
            }
        }

        return result;
    }

    /// <summary>"Liquid temperature" → "Liquid", "Pump speed" → "Pump", "Fan 2 speed" → "Fan 2".</summary>
    private static string ShortKey(string key)
    {
        foreach (var suffix in new[] { " temperature", " speed", " duty", " voltage", " power" })
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && key.Length > suffix.Length)
                return key[..^suffix.Length];
        }

        return key;
    }

    private static async Task<string> RunAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo =
            {
                FileName = executable,
                ArgumentList = { "--json", "status" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        process.Start();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"exit code {process.ExitCode}";
                throw new InvalidOperationException($"liquidctl status failed: {message}");
            }

            return await stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
