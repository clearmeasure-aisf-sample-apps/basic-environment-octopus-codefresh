using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Conformance.Tests.GitOps;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Offline.GitOps;

/// <summary>Result of a <c>helm template</c> run.</summary>
/// <param name="ExitCode">Exit code.</param>
/// <param name="Output">Standard output (the rendered manifests).</param>
/// <param name="Error">Standard error (a <c>fail</c> guard's message).</param>
internal sealed record ChartRender(int ExitCode, string Output, string Error)
{
    /// <summary>Every rendered object as JSON, in render order.</summary>
    public IReadOnlyList<JsonElement> Objects => TenantChart.Parse(Output);
}

/// <summary>
/// Renders the tenant chart <c>gitops/platform/tenant</c> with <c>helm template</c>, exactly as the ApplicationSet
/// <c>apps</c> does: <c>values-&lt;tier&gt;.yaml</c>, then the descriptor as a values file. helm comes from <c>HELM</c> or the
/// PATH; without it a test is Inconclusive locally and fails when <c>CI=true</c> (env-checks must provide it).
/// </summary>
internal static class TenantChart
{
    /// <summary>Stand-in apps domain, so that the ListenerSets render (the committed value is a placeholder until P1-07).</summary>
    public const string StandInDomain = "20-65-1-2.sslip.io";

    /// <summary>Tiers of the platform's app clusters.</summary>
    public static IReadOnlyList<string> Tiers { get; } = ["nonprod", "prod"];

    /// <summary>Every committed descriptor with its path.</summary>
    public static IReadOnlyList<(GitOpsApp App, string Path)> Descriptors =>
        Directory.EnumerateFiles(Path.Combine(GitOpsRepository.Root, "apps"), "*.yaml")
            .Order(StringComparer.Ordinal)
            .Select(path => (GitOpsRepository.LoadFile(path), path))
            .ToArray();

    /// <summary>Renders the chart for one descriptor file on one tier.</summary>
    /// <param name="descriptorPath">A descriptor file.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    /// <param name="settings">Extra <c>--set</c> values, for example the stand-in apps domain.</param>
    /// <param name="releaseName">Release name; <c>tenant-&lt;file name&gt;</c> by default, as the ApplicationSet names it.</param>
    public static ChartRender Render(string descriptorPath, string tier, IReadOnlyDictionary<string, string>? settings = null, string? releaseName = null)
    {
        var helm = RequireHelm();
        var chart = GitOpsRepository.TenantChart;
        var arguments = new List<string> { "template", releaseName ?? $"tenant-{Path.GetFileNameWithoutExtension(descriptorPath)}", chart, "-f", Path.Combine(chart, $"values-{tier}.yaml"), "-f", descriptorPath };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            arguments.Add("--set");
            arguments.Add($"{key}={value}");
        }

        return Run(helm, arguments);
    }

    /// <summary>Renders the chart for a descriptor given as YAML text (written to a temporary file).</summary>
    /// <param name="descriptorYaml">Descriptor text.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static ChartRender RenderText(string descriptorYaml, string tier)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tenant-descriptor-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, descriptorYaml, new UTF8Encoding(false));
        try
        {
            return Render(path, tier, releaseName: "tenant-check");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Parses multi-document YAML into JSON objects; empty documents are skipped.</summary>
    /// <param name="yaml">YAML text.</param>
    public static IReadOnlyList<JsonElement> Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return stream.Documents
            .Select(document => Convert(document.RootNode))
            .OfType<JsonObject>()
            .Select(node => JsonSerializer.SerializeToElement(node))
            .ToArray();
    }

    /// <summary>The kind, namespace and name of an object, as <c>Kind namespace/name</c> (namespace <c>-</c> when cluster-scoped).</summary>
    /// <param name="item">The object.</param>
    public static string Identity(JsonElement item) =>
        $"{GitOpsCluster.Text(item, "kind")} {GitOpsCluster.Text(item, "metadata", "namespace") ?? "-"}/{GitOpsCluster.Text(item, "metadata", "name")}";

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => KeyValuePair.Create(((YamlScalarNode)pair.Key).Value ?? string.Empty, Convert(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlScalarNode { Value: null } => null,
        YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain, Value: "null" or "~" or "" } => null,
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };

    /// <summary>Fails (CI) or ends the test Inconclusive (locally) when helm is missing. Call it before any
    /// multiple-assertion block: NUnit rejects Inconclusive inside one.</summary>
    public static void EnsureHelmAvailable() => RequireHelm();

    private static string RequireHelm()
    {
        var configured = Environment.GetEnvironmentVariable("HELM");
        var helm = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? configured
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "helm.exe" : "helm"))
                .FirstOrDefault(File.Exists);
        if (helm is not null)
        {
            return helm;
        }

        const string message = "helm (v4) not found in HELM or on PATH; the tenant chart is rendered where helm is installed";
        if (Environment.GetEnvironmentVariable("CI") is "true" or "TRUE" or "True" or "1")
        {
            Assert.Fail($"{message} (CI=true: env-checks must provide it)");
        }

        Assert.Inconclusive(message);
        return string.Empty;
    }

    private static ChartRender Run(string fileName, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = GitOpsRepository.Root,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"helm {string.Join(' ', start.ArgumentList)} did not finish in two minutes");
        }

        return new ChartRender(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
