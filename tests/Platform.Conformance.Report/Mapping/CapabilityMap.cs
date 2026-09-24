using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Platform.Conformance.Harness.Catalogue;

namespace Platform.Conformance.Report;

/// <summary>Which capabilities each test proves, as emitted by the <c>map</c> command (JSON, schema 1).</summary>
internal sealed record CapabilityMap
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Schema version.</summary>
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>Assemblies the map was reflected from.</summary>
    public IReadOnlyList<string> Assemblies { get; init; } = [];

    /// <summary>Every test with its capability IDs and categories.</summary>
    public IReadOnlyList<CapabilityMapEntry> Tests { get; init; } = [];

    /// <summary>Builds a map from discovered tests.</summary>
    /// <param name="tests">Tests found by <see cref="TestDiscovery"/>.</param>
    public static CapabilityMap FromTests(IReadOnlyList<DiscoveredTest> tests) => new()
    {
        Assemblies = tests.Select(test => test.Assembly).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        Tests = tests.Select(test => new CapabilityMapEntry(test.FullName, test.Assembly, test.CapabilityIds, test.Categories)).ToArray(),
    };

    /// <summary>Reflects over test assemblies loaded from disk, each in its own load context.</summary>
    /// <param name="assemblyPaths">Paths of the test assemblies (their <c>.deps.json</c> must sit next to them).</param>
    /// <exception cref="FileNotFoundException">An assembly does not exist.</exception>
    public static CapabilityMap FromAssemblyFiles(IEnumerable<string> assemblyPaths) =>
        FromTests(TestDiscovery.Discover(assemblyPaths.Select(LoadIsolated).ToArray()));

    /// <summary>Reads a map written by <see cref="ToJson"/>.</summary>
    /// <param name="json">Map JSON.</param>
    /// <param name="source">File name for messages.</param>
    /// <exception cref="InvalidDataException">The JSON is not a capability map of a known schema.</exception>
    public static CapabilityMap FromJson(string json, string source)
    {
        CapabilityMap? map;
        try
        {
            map = JsonSerializer.Deserialize<CapabilityMap>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source} is not a capability map: {ex.Message}", ex);
        }

        if (map is null || map.Schema != CurrentSchema)
        {
            throw new InvalidDataException($"{source} is not a capability map of schema {CurrentSchema}.");
        }

        return map;
    }

    /// <summary>Serializes the map as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The map as discovered tests, for the consistency check and the report.</summary>
    public IReadOnlyList<DiscoveredTest> ToDiscoveredTests() =>
        Tests.Select(test => new DiscoveredTest(test.Name, test.Assembly, test.Capabilities, test.Categories)).ToArray();

    private static Assembly LoadIsolated(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Test assembly {fullPath} does not exist; build the test projects first.", fullPath);
        }

        return new IsolatedLoadContext(fullPath).LoadFromAssemblyPath(fullPath);
    }

    private sealed class IsolatedLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public IsolatedLoadContext(string mainAssemblyPath)
            : base($"conformance-map:{Path.GetFileName(mainAssemblyPath)}")
        {
            resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
    }
}

/// <summary>One test of a <see cref="CapabilityMap"/>.</summary>
/// <param name="Name">Fully qualified test name without arguments.</param>
/// <param name="Assembly">Assembly name.</param>
/// <param name="Capabilities">Capability IDs.</param>
/// <param name="Categories">NUnit categories.</param>
internal sealed record CapabilityMapEntry(string Name, string Assembly, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Categories);
