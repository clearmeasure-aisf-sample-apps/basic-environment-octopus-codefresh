using System.Security.Cryptography;
using System.Text;
using Platform.Conformance.Harness.Settings;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Tests.Kit;

/// <summary>What the live Kit tests need from one descriptor apps/&lt;app&gt;.yaml (design §7.0).</summary>
/// <param name="Name">App slug.</param>
/// <param name="Status">active or frozen.</param>
/// <param name="Environments">Application environments.</param>
/// <param name="Repositories">Repositories as (owner/name, default branch).</param>
/// <param name="OctopusProjects">Octopus project names.</param>
/// <param name="CodefreshProjects">Codefresh project names.</param>
/// <param name="Images">Image names under apps/&lt;app&gt;/.</param>
/// <param name="HasDatabase">Whether the app declares a database.</param>
/// <param name="HasResourceGroup">Whether the app declares rg-app-&lt;app&gt;-&lt;tier&gt;.</param>
public sealed record KitDescriptor(
    string Name,
    string Status,
    IReadOnlyList<string> Environments,
    IReadOnlyList<(string Name, string DefaultBranch)> Repositories,
    IReadOnlyList<string> OctopusProjects,
    IReadOnlyList<string> CodefreshProjects,
    IReadOnlyList<string> Images,
    bool HasDatabase,
    bool HasResourceGroup)
{
    /// <summary>Environments of a tier, by the fixed map (tdd and uat on nonprod, prod on prod).</summary>
    /// <param name="tier">nonprod or prod.</param>
    public IEnumerable<string> EnvironmentsOf(string tier) => Environments.Where(environment => KitNames.TierOf(environment) == tier);
}

/// <summary>Platform names the Kit tests derive from descriptors, independently of the onboarding tool (an oracle).</summary>
public static class KitNames
{
    /// <summary>Platform Octopus projects, which no descriptor declares.</summary>
    public static IReadOnlyList<string> PlatformOctopusProjects { get; } = ["platform-infrastructure", "platform-wake"];

    /// <summary>Platform Codefresh project.</summary>
    public const string PlatformCodefreshProject = "platform-env";

    /// <summary>The conformance fixture's unsigned image repository, which no deployable pins.</summary>
    public const string FixtureUnsignedRepository = "apps/sandbox/unsigned";

    /// <summary>tdd and uat run on nonprod, prod on prod.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string TierOf(string environment) => environment == "prod" ? "prod" : "nonprod";

    /// <summary><c>kv-&lt;app&gt;-&lt;e&gt;-&lt;hash4&gt;</c>: first four hex digits of sha1("&lt;subscription&gt;/&lt;app&gt;/&lt;env&gt;") (ADR-IR34 decision 8).</summary>
    /// <param name="subscriptionId">Subscription ID.</param>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string VaultName(string subscriptionId, string app, string environment)
    {
#pragma warning disable CA5350 // A naming hash shared with Terraform, not a security control.
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{subscriptionId.Trim().ToLowerInvariant()}/{app}/{environment}")))[..4].ToLowerInvariant();
#pragma warning restore CA5350
        return $"kv-{app}-{environment[0]}-{hash}";
    }
}

/// <summary>Reads apps/*.yaml of the repository.</summary>
public static class KitDescriptors
{
    /// <summary>Loads every descriptor; a file that does not parse fails the test.</summary>
    /// <param name="repositoryRoot">The environment repository root.</param>
    public static IReadOnlyList<KitDescriptor> Load(string repositoryRoot)
    {
        var descriptors = new List<KitDescriptor>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repositoryRoot, "apps"), "*.yaml").Order(StringComparer.Ordinal))
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(path)));
            var root = (YamlMappingNode)stream.Documents[0].RootNode;
            descriptors.Add(new KitDescriptor(
                Scalar(root, "name") ?? Path.GetFileNameWithoutExtension(path),
                Scalar(root, "status") ?? "active",
                Sequence(root, "environments") is { Count: > 0 } environments ? environments : ["tdd", "uat", "prod"],
                Mappings(root, "repositories").Select(repository => (Scalar(repository, "name") ?? string.Empty, Scalar(repository, "defaultBranch") ?? "main")).ToArray(),
                Mappings(Child(root, "octopus"), "projects").Select(project => Scalar(project, "name") ?? string.Empty).ToArray(),
                Sequence(Child(root, "codefresh"), "projects"),
                Mappings(root, "deployables").SelectMany(deployable => Sequence(deployable, "images")).ToArray(),
                Child(root, "database") is not null,
                Scalar(Child(root, "azure"), "resourceGroup") == "true"));
        }

        return descriptors;
    }

    /// <summary>The platform settings' subscription, or <c>null</c> when unset or a placeholder.</summary>
    /// <param name="settings">Harness settings.</param>
    public static string? Subscription(PlatformSettings settings) =>
        PlatformSettings.IsMissing(settings.AzureSubscriptionId) ? null : settings.AzureSubscriptionId;

    private static YamlMappingNode? Child(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child as YamlMappingNode : null;

    private static string? Scalar(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? (child as YamlScalarNode)?.Value : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? string.Empty).ToArray()
            : [];

    private static IEnumerable<YamlMappingNode> Mappings(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>()
            : [];
}

/// <summary>
/// The end-to-end pass (CAP-KIT-009) changes app #1 only in its copy in clearmeasure-aisf-sample-apps, never in the
/// upstream ClearMeasureLabs repository (directive §14, design §6.3).
/// </summary>
public static class EndToEndGuard
{
    /// <summary>The only owner the end-to-end pass may write to.</summary>
    public const string AllowedOwner = "clearmeasure-aisf-sample-apps";

    /// <summary>Throws unless <paramref name="repository"/> is <c>clearmeasure-aisf-sample-apps/&lt;name&gt;</c>.</summary>
    /// <param name="repository">owner/name.</param>
    /// <exception cref="InvalidOperationException">Any other owner, a placeholder or a malformed name.</exception>
    public static void AssertAllowedRepository(string repository)
    {
        var parts = (repository ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || parts[1].Length == 0 || parts[1].Contains('<', StringComparison.Ordinal) || !string.Equals(parts[0], AllowedOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"the end-to-end pass writes only to {AllowedOwner}/<name>; refusing '{repository}' (never the upstream ClearMeasureLabs repository)");
        }
    }
}
