using System.Text;
using System.Text.Json.Nodes;
using Anduri.Agent.Sensors.Linux;

namespace Anduri.Agent.Tests;

/// <summary>Locates files of the repository the tests run from.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string ProtocolDirectory => Path.Combine(Root, "docs", "protocol");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "protocol", "pairing-vectors.json")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Couldn't find docs/protocol above the test output directory.");
    }
}

/// <summary>A throwaway directory, e.g. a fake procfs/sysfs root or a state directory.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "anduri-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public HostPaths Paths => new(Root);

    public string PathOf(string relative) => Path.Combine(Root, relative.TrimStart('/'));

    public TempTree Write(string relative, string contents)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return this;
    }

    /// <summary>Writes several attributes of one sysfs directory: ("temp1_input", "31250") …</summary>
    public TempTree WriteAll(string directory, params (string Name, string Value)[] files)
    {
        foreach (var (name, value) in files)
            Write($"{directory}/{name}", value + "\n");
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class JsonAssert
{
    /// <summary>Compares JSON by meaning: object key order is ignored and numbers compare by value (42.0 == 42).</summary>
    public static void SemanticallyEqual(string expected, string actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);
        var difference = FindDifference(expectedNode, actualNode, "$");
        Assert.True(difference is null, $"{difference}\nexpected: {expected}\nactual:   {actual}");
    }

    private static string? FindDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (null, _) or (_, null):
                return $"{path}: one side is null";
            case (JsonObject e, JsonObject a):
            {
                var missing = e.Select(p => p.Key).Except(a.Select(p => p.Key)).ToList();
                var extra = a.Select(p => p.Key).Except(e.Select(p => p.Key)).ToList();
                if (missing.Count > 0 || extra.Count > 0)
                    return $"{path}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]";
                foreach (var (key, value) in e)
                {
                    if (FindDifference(value, a[key], $"{path}.{key}") is { } difference)
                        return difference;
                }
                return null;
            }
            case (JsonArray e, JsonArray a):
            {
                if (e.Count != a.Count)
                    return $"{path}: {e.Count} elements vs {a.Count}";
                for (var i = 0; i < e.Count; i++)
                {
                    if (FindDifference(e[i], a[i], $"{path}[{i}]") is { } difference)
                        return difference;
                }
                return null;
            }
            case (JsonValue e, JsonValue a):
            {
                var ek = e.GetValueKind();
                var ak = a.GetValueKind();
                if (ek == System.Text.Json.JsonValueKind.Number && ak == System.Text.Json.JsonValueKind.Number)
                    return e.GetValue<double>() == a.GetValue<double>() ? null : $"{path}: {e} vs {a}";
                return ek == ak && e.ToJsonString() == a.ToJsonString() || JsonNode.DeepEquals(e, a) ? null : $"{path}: {e.ToJsonString()} vs {a.ToJsonString()}";
            }
            default:
                return $"{path}: different JSON types";
        }
    }
}
