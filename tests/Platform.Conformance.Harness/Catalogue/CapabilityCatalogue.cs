using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Harness.Catalogue;

/// <summary>
/// The machine-readable capability catalogue. <see cref="LoadDirectory"/> merges the optional
/// <c>catalogue/capabilities.yaml</c> with every <c>catalogue/capabilities.d/*.yaml</c> fragment, in file-name order.
/// Loading validates the schema and reports every problem with its file and line.
/// </summary>
/// <remarks>
/// Schema of each file: a mapping with one key, <c>capabilities</c>, holding a list of entries with the keys
/// <c>id</c>, <c>statement</c>, <c>owner</c>, <c>adr</c>, <c>observed_by</c>, <c>tests</c>, <c>live</c>,
/// <c>destructive</c>, <c>tier</c> and, when <c>live</c> is false, <c>why_offline</c>.
/// </remarks>
public sealed partial class CapabilityCatalogue
{
    /// <summary>Catalogue folder under the repository root.</summary>
    public const string FolderName = "catalogue";

    /// <summary>Optional main file inside <see cref="FolderName"/>.</summary>
    public const string MainFileName = "capabilities.yaml";

    /// <summary>Fragment folder inside <see cref="FolderName"/>; one file per owning role.</summary>
    public const string FragmentFolderName = "capabilities.d";

    private static readonly string[] AllowedKeys =
        ["id", "statement", "owner", "adr", "observed_by", "tests", "live", "destructive", "tier", "why_offline"];

    private readonly Dictionary<string, Capability> byId;

    private CapabilityCatalogue(IReadOnlyList<Capability> capabilities, IReadOnlyList<string> sources)
    {
        Capabilities = capabilities;
        Sources = sources;
        byId = capabilities.ToDictionary(capability => capability.Id, StringComparer.Ordinal);
    }

    /// <summary>Every capability, in file order then entry order.</summary>
    public IReadOnlyList<Capability> Capabilities { get; }

    /// <summary>Names of the files that were merged, in load order.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>Returns the capability with <paramref name="id"/>, or <c>null</c>.</summary>
    /// <param name="id">Capability ID (case-sensitive).</param>
    public Capability? Find(string id) => byId.GetValueOrDefault(id);

    /// <summary>Loads and validates a single catalogue file.</summary>
    /// <param name="path">Path of the YAML file.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="CatalogueValidationException">The file breaks the schema.</exception>
    public static CapabilityCatalogue Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Catalogue file {path} does not exist.", path);
        }

        return FromSources([new CatalogueSource(path, File.ReadAllText(path))]);
    }

    /// <summary>
    /// Loads <c>catalogue/capabilities.yaml</c> (optional) and every <c>catalogue/capabilities.d/*.yaml</c> under
    /// <paramref name="repositoryRoot"/>, sorted by file name, and validates the merged result.
    /// </summary>
    /// <param name="repositoryRoot">The folder that holds <c>catalogue/</c> and <c>tests/</c>.</param>
    /// <exception cref="CatalogueValidationException">No catalogue file exists, a fragment is misnamed, a file breaks the schema, or an ID is defined twice.</exception>
    public static CapabilityCatalogue LoadDirectory(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var folder = Path.Combine(repositoryRoot, FolderName);
        var sources = new List<CatalogueSource>();
        var errors = new List<string>();
        var mainFile = Path.Combine(folder, MainFileName);
        if (File.Exists(mainFile))
        {
            sources.Add(new CatalogueSource($"{FolderName}/{MainFileName}", File.ReadAllText(mainFile)));
        }

        var fragments = Path.Combine(folder, FragmentFolderName);
        if (Directory.Exists(fragments))
        {
            foreach (var file in Directory.EnumerateFiles(fragments).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var displayName = $"{FolderName}/{FragmentFolderName}/{name}";
                if (name.EndsWith(".yaml", StringComparison.Ordinal))
                {
                    sources.Add(new CatalogueSource(displayName, File.ReadAllText(file)));
                }
                else if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{displayName}: fragments must end in lowercase .yaml; this file is not loaded until it is renamed");
                }
            }
        }

        if (sources.Count == 0 && errors.Count == 0)
        {
            errors.Add($"no catalogue under {folder}: expected {FolderName}/{MainFileName} or {FolderName}/{FragmentFolderName}/*.yaml");
        }

        return FromSources(sources, errors);
    }

    /// <summary>Parses and validates one YAML text; used by tests and tools.</summary>
    /// <param name="yaml">Catalogue YAML.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <exception cref="CatalogueValidationException">The text breaks the schema.</exception>
    public static CapabilityCatalogue Parse(string yaml, string sourceName = "catalogue.yaml") => FromSources([new CatalogueSource(sourceName, yaml)]);

    /// <summary>Merges and validates several catalogue texts, in the given order.</summary>
    /// <param name="sources">The catalogue files.</param>
    /// <exception cref="CatalogueValidationException">A text breaks the schema, or an ID is defined more than once (the message names both places).</exception>
    public static CapabilityCatalogue FromSources(IEnumerable<CatalogueSource> sources) => FromSources(sources, []);

    /// <summary>Builds a catalogue from capabilities made in code, without schema validation (unit tests of the consistency rules).</summary>
    /// <param name="capabilities">The capabilities; IDs must be unique.</param>
    internal static CapabilityCatalogue FromCapabilities(IReadOnlyList<Capability> capabilities) =>
        new(capabilities, capabilities.Select(capability => capability.Source).Distinct(StringComparer.Ordinal).ToArray());

    private static CapabilityCatalogue FromSources(IEnumerable<CatalogueSource> sources, IEnumerable<string> earlierErrors)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var errors = new List<string>(earlierErrors);
        var capabilities = new List<Capability>();
        var declarations = new List<(string Id, string Location)>();
        var names = new List<string>();
        foreach (var source in sources)
        {
            names.Add(source.Name);
            ParseSource(source, capabilities, declarations, errors);
        }

        foreach (var duplicate in declarations.GroupBy(declaration => declaration.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"capability {duplicate.Key} is defined more than once: {string.Join(" and ", duplicate.Select(declaration => declaration.Location))}");
        }

        if (errors.Count > 0)
        {
            throw new CatalogueValidationException(errors);
        }

        return new CapabilityCatalogue(capabilities, names);
    }

    private static void ParseSource(CatalogueSource source, List<Capability> capabilities, List<(string Id, string Location)> declarations, List<string> errors)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(source.Content));
        }
        catch (YamlException ex)
        {
            errors.Add($"{source.Name}:{ex.Start.Line}: invalid YAML: {ex.Message}");
            return;
        }

        if (stream.Documents.Count == 0 || IsNull(stream.Documents[0].RootNode))
        {
            return;
        }

        if (stream.Documents.Count > 1)
        {
            errors.Add($"{source.Name}: holds {stream.Documents.Count} YAML documents; a catalogue file holds one");
        }

        var root = stream.Documents[0].RootNode;
        if (root is not YamlMappingNode mapping)
        {
            errors.Add($"{source.Name}:{root.Start.Line}: the top level must be a mapping with the key 'capabilities'");
            return;
        }

        YamlNode? list = null;
        foreach (var (key, value) in mapping.Children)
        {
            var name = (key as YamlScalarNode)?.Value;
            if (name == "capabilities")
            {
                list = value;
            }
            else
            {
                errors.Add($"{source.Name}:{key.Start.Line}: unknown top-level key '{name}'; the only top-level key is 'capabilities'");
            }
        }

        if (list is null)
        {
            errors.Add($"{source.Name}:{root.Start.Line}: missing the top-level key 'capabilities'");
            return;
        }

        if (IsNull(list))
        {
            return;
        }

        if (list is not YamlSequenceNode entries)
        {
            errors.Add($"{source.Name}:{list.Start.Line}: 'capabilities' must be a list");
            return;
        }

        var index = 0;
        foreach (var entry in entries.Children)
        {
            index++;
            var parser = new EntryParser(source.Name, entry, index, errors);
            var capability = parser.Parse();
            if (parser.Id is not null)
            {
                declarations.Add((parser.Id, $"{source.Name}:{entry.Start.Line}"));
            }

            if (capability is not null)
            {
                capabilities.Add(capability);
            }
        }
    }

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar
        && (string.IsNullOrEmpty(scalar.Value) || scalar.Value is "~" or "null" or "Null" or "NULL");

    [GeneratedRegex("^CAP-[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-[0-9]{3}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*){2,}$")]
    private static partial Regex TestNamePattern();

    private sealed class EntryParser
    {
        private readonly string source;
        private readonly YamlNode entry;
        private readonly int index;
        private readonly List<string> errors;
        private readonly Dictionary<string, YamlNode> fields = new(StringComparer.Ordinal);
        private bool failed;

        public EntryParser(string source, YamlNode entry, int index, List<string> errors)
        {
            this.source = source;
            this.entry = entry;
            this.index = index;
            this.errors = errors;
        }

        public string? Id { get; private set; }

        private string Label => Id ?? $"entry {index}";

        public Capability? Parse()
        {
            if (entry is not YamlMappingNode mapping)
            {
                Error(entry, "each capability must be a mapping");
                return null;
            }

            Id = ReadIdKey(mapping);
            foreach (var (key, value) in mapping.Children)
            {
                var name = (key as YamlScalarNode)?.Value ?? string.Empty;
                if (!AllowedKeys.Contains(name, StringComparer.Ordinal))
                {
                    Error(key, $"unknown key '{name}'; allowed keys: {string.Join(", ", AllowedKeys)}");
                    continue;
                }

                fields[name] = value;
            }

            var id = RequiredString("id");
            if (id is not null && !IdPattern().IsMatch(id))
            {
                Error(fields["id"], $"id '{id}' must look like CAP-AREA-001 (uppercase letters, digits and hyphens, ending in three digits)");
            }

            var statement = RequiredString("statement");
            var owner = RequiredOwner();
            var adr = RequiredString("adr");
            var observedBy = RequiredString("observed_by");
            var tests = RequiredTests();
            var live = RequiredBool("live");
            var destructive = RequiredBool("destructive");
            var tier = RequiredTier();
            var whyOffline = OptionalString("why_offline");

            if (live == false && whyOffline is null)
            {
                Error(entry, "why_offline is required when live is false (say why the capability is proven offline)");
            }

            if (live == true && whyOffline is not null)
            {
                Error(fields["why_offline"], "why_offline applies only when live is false; remove it or set live: false");
            }

            if (destructive == true && tier == CapabilityTier.Prod)
            {
                Error(fields["tier"], "a destructive capability cannot have tier prod: destructive tests never run in prod (use nonprod, build or all)");
            }

            if (failed || id is null || statement is null || owner is null || adr is null || observedBy is null
                || tests is null || live is null || destructive is null || tier is null)
            {
                return null;
            }

            return new Capability
            {
                Id = id,
                Statement = statement,
                Owner = owner.Value,
                Adr = adr,
                ObservedBy = observedBy,
                Tests = tests,
                Live = live.Value,
                Destructive = destructive.Value,
                Tier = tier.Value,
                WhyOffline = whyOffline,
                Source = source,
                Line = checked((int)entry.Start.Line),
            };
        }

        private static string? ReadIdKey(YamlMappingNode mapping)
        {
            foreach (var (key, value) in mapping.Children)
            {
                if (key is YamlScalarNode { Value: "id" } && value is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
                {
                    return scalar.Value.Trim();
                }
            }

            return null;
        }

        private string? RequiredString(string key)
        {
            if (!fields.TryGetValue(key, out var node) || IsNull(node))
            {
                Error(entry, $"missing required key '{key}'");
                return null;
            }

            if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                Error(node, $"'{key}' must be a non-empty string");
                return null;
            }

            return scalar.Value.Trim();
        }

        private string? OptionalString(string key)
        {
            if (!fields.TryGetValue(key, out var node) || IsNull(node))
            {
                return null;
            }

            if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                Error(node, $"'{key}' must be a non-empty string");
                return null;
            }

            return scalar.Value.Trim();
        }

        private bool? RequiredBool(string key)
        {
            var value = RequiredString(key);
            switch (value)
            {
                case null:
                    return null;
                case "true":
                    return true;
                case "false":
                    return false;
                default:
                    Error(fields[key], $"'{key}' must be true or false (was '{value}')");
                    return null;
            }
        }

        private CapabilityOwner? RequiredOwner()
        {
            var value = RequiredString("owner");
            if (value is null)
            {
                return null;
            }

            if (CatalogueNames.TryParseOwner(value, out var owner))
            {
                return owner;
            }

            Error(fields["owner"], $"owner must be one of {string.Join(", ", CatalogueNames.Owners)} (was '{value}')");
            return null;
        }

        private CapabilityTier? RequiredTier()
        {
            var value = RequiredString("tier");
            if (value is null)
            {
                return null;
            }

            if (CatalogueNames.TryParseTier(value, out var tier))
            {
                return tier;
            }

            Error(fields["tier"], $"tier must be one of {string.Join(", ", CatalogueNames.Tiers)} (was '{value}')");
            return null;
        }

        private IReadOnlyList<string>? RequiredTests()
        {
            if (!fields.TryGetValue("tests", out var node) || IsNull(node))
            {
                Error(entry, "missing required key 'tests' (the fully qualified names of the tests that prove the capability)");
                return null;
            }

            if (node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
            {
                Error(node, "'tests' must be a non-empty list of fully qualified test names");
                return null;
            }

            var tests = new List<string>();
            foreach (var item in sequence.Children)
            {
                if (item is not YamlScalarNode { Value: { } raw } || string.IsNullOrWhiteSpace(raw))
                {
                    Error(item, "each entry of 'tests' must be a fully qualified test name");
                    continue;
                }

                var name = raw.Trim();
                if (!TestNamePattern().IsMatch(name))
                {
                    Error(item, $"'{name}' is not a fully qualified test name (Namespace.Class.Method, without arguments)");
                }
                else if (tests.Contains(name, StringComparer.Ordinal))
                {
                    Error(item, $"test '{name}' is listed twice");
                }
                else
                {
                    tests.Add(name);
                }
            }

            return tests;
        }

        private void Error(YamlNode node, string message)
        {
            failed = true;
            errors.Add($"{source}:{node.Start.Line}: {Label}: {message}");
        }
    }
}
