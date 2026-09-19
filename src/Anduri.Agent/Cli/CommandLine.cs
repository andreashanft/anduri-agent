namespace Anduri.Agent.Cli;

/// <summary>A parsed command line: positional words plus <c>--flag value</c> / <c>--flag=value</c> options.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);

    private CommandLine(List<string> positional) => Positional = positional;

    public IReadOnlyList<string> Positional { get; }

    /// <param name="switches">Options that take no value, e.g. <c>--simulate</c>.</param>
    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlySet<string> switches)
    {
        var positional = new List<string>();
        var result = new CommandLine(positional);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                positional.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var separator = arg.IndexOf('=');
                var name = separator > 0 ? arg[2..separator] : arg[2..];
                if (separator > 0)
                    result.options[name] = arg[(separator + 1)..];
                else if (switches.Contains(name))
                    result.options[name] = null;
                else if (i + 1 < args.Count)
                    result.options[name] = args[++i];
                else
                    throw new UsageException($"--{name} needs a value.");
            }
            else if (arg is "-h")
            {
                result.options["help"] = null;
            }
            else
            {
                positional.Add(arg);
            }
        }

        return result;
    }

    public bool Has(string name)
    {
        consumed.Add(name);
        return options.ContainsKey(name);
    }

    public string? Get(string name)
    {
        consumed.Add(name);
        return options.GetValueOrDefault(name);
    }

    public int? GetInt(string name, int min, int max)
    {
        if (Get(name) is not { } text)
            return null;
        if (!int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new UsageException($"--{name} must be a number from {min} to {max}.");
        return value;
    }

    /// <summary>Rejects options no command asked for, so a typo like <c>--prot</c> isn't silently ignored.</summary>
    public void EnsureAllConsumed()
    {
        var unknown = options.Keys.Where(k => !consumed.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"Unknown option {string.Join(", ", unknown.Select(k => "--" + k))}.");
    }
}

public sealed class UsageException(string message) : Exception(message);
