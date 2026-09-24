using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// The fence of an Argo CD AppProject, evaluated like Argo CD does: a cluster-scoped kind is permitted only when
/// <c>clusterResourceWhitelist</c> matches it; a namespaced kind is refused when <c>namespaceResourceBlacklist</c> matches
/// it (or a <c>namespaceResourceWhitelist</c> does not); a destination namespace must match a destination pattern. Groups,
/// kinds and namespaces match as globs (<c>*</c>). Used on live AppProjects and on the tenant chart's render.
/// </summary>
public sealed class AppProjectFence
{
    private const string InCluster = "https://kubernetes.default.svc";

    /// <summary>Cluster-scoped kinds an app must never create (a sample of the dangerous ones), as (group, kind).</summary>
    public static IReadOnlyList<(string Group, string Kind)> ClusterScopedSamples { get; } =
    [
        ("", "Namespace"),
        ("", "PersistentVolume"),
        ("rbac.authorization.k8s.io", "ClusterRole"),
        ("rbac.authorization.k8s.io", "ClusterRoleBinding"),
        ("apiextensions.k8s.io", "CustomResourceDefinition"),
        ("storage.k8s.io", "StorageClass"),
        ("admissionregistration.k8s.io", "ValidatingWebhookConfiguration"),
        ("external-secrets.io", "ClusterSecretStore"),
        ("policies.kyverno.io", "ValidatingPolicy"),
        ("policies.kyverno.io", "ImageValidatingPolicy"),
        ("cert-manager.io", "ClusterIssuer"),
    ];

    /// <summary>Namespaced kinds an app deploys every day; the fence must let them through.</summary>
    public static IReadOnlyList<(string Group, string Kind)> AllowedSamples { get; } =
    [
        ("apps", "Deployment"),
        ("", "Service"),
        ("", "ConfigMap"),
        ("batch", "Job"),
        ("external-secrets.io", "ExternalSecret"),
        ("gateway.networking.k8s.io", "HTTPRoute"),
    ];

    private AppProjectFence(
        string name,
        IReadOnlyList<string> sourceRepos,
        IReadOnlyList<(string Server, string Namespace)> destinations,
        IReadOnlyList<(string Group, string Kind)> clusterWhitelist,
        IReadOnlyList<(string Group, string Kind)> namespaceBlacklist,
        IReadOnlyList<(string Group, string Kind)>? namespaceWhitelist)
    {
        Name = name;
        SourceRepos = sourceRepos;
        Destinations = destinations;
        ClusterWhitelist = clusterWhitelist;
        NamespaceBlacklist = namespaceBlacklist;
        NamespaceWhitelist = namespaceWhitelist;
    }

    /// <summary>AppProject name.</summary>
    public string Name { get; }

    /// <summary><c>spec.sourceRepos</c>.</summary>
    public IReadOnlyList<string> SourceRepos { get; }

    /// <summary><c>spec.destinations</c> as (server, namespace).</summary>
    public IReadOnlyList<(string Server, string Namespace)> Destinations { get; }

    /// <summary><c>spec.clusterResourceWhitelist</c>.</summary>
    public IReadOnlyList<(string Group, string Kind)> ClusterWhitelist { get; }

    /// <summary><c>spec.namespaceResourceBlacklist</c>.</summary>
    public IReadOnlyList<(string Group, string Kind)> NamespaceBlacklist { get; }

    /// <summary><c>spec.namespaceResourceWhitelist</c>, or <c>null</c> when unset (every namespaced kind not blacklisted).</summary>
    public IReadOnlyList<(string Group, string Kind)>? NamespaceWhitelist { get; }

    /// <summary>Reads an AppProject object.</summary>
    /// <param name="appProject">The AppProject as JSON.</param>
    public static AppProjectFence Read(JsonElement appProject)
    {
        var spec = GitOpsCluster.Child(appProject, "spec");
        var whitelist = GitOpsCluster.Child(spec, "namespaceResourceWhitelist");
        return new AppProjectFence(
            GitOpsCluster.Text(appProject, "metadata", "name") ?? string.Empty,
            Items(spec, "sourceRepos").Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty).ToArray(),
            Items(spec, "destinations").Select(item => (GitOpsCluster.Text(item, "server") ?? GitOpsCluster.Text(item, "name") ?? string.Empty, GitOpsCluster.Text(item, "namespace") ?? string.Empty)).ToArray(),
            Kinds(spec, "clusterResourceWhitelist"),
            Kinds(spec, "namespaceResourceBlacklist"),
            whitelist.ValueKind == JsonValueKind.Array ? Kinds(spec, "namespaceResourceWhitelist") : null);
    }

    /// <summary>Whether Argo CD would let an Application of this project apply a cluster-scoped kind.</summary>
    /// <param name="group">API group ("" for the core group).</param>
    /// <param name="kind">Kind.</param>
    public bool PermitsClusterScoped(string group, string kind) => ClusterWhitelist.Any(entry => Matches(entry, group, kind));

    /// <summary>Whether Argo CD would let an Application of this project apply a namespaced kind into a namespace.</summary>
    /// <param name="group">API group ("" for the core group).</param>
    /// <param name="kind">Kind.</param>
    /// <param name="namespaceName">Target namespace.</param>
    public bool PermitsNamespaced(string group, string kind, string namespaceName) =>
        PermitsDestination(namespaceName)
        && !NamespaceBlacklist.Any(entry => Matches(entry, group, kind))
        && (NamespaceWhitelist is null || NamespaceWhitelist.Any(entry => Matches(entry, group, kind)));

    /// <summary>Whether a destination namespace of the in-cluster server matches a destination of the project.</summary>
    /// <param name="namespaceName">Target namespace.</param>
    public bool PermitsDestination(string namespaceName) =>
        Destinations.Any(destination => (destination.Server == InCluster || destination.Server == "in-cluster") && Glob(destination.Namespace, namespaceName));

    /// <summary>
    /// Every way this project differs from the fence of §7.0 for <paramref name="app"/> on <paramref name="tier"/>: the only
    /// source is the environment repository; the destinations are exactly the app's namespaces of the tier; no
    /// cluster-scoped kind; the forbidden namespaced kinds refused; other namespaces refused; everyday kinds permitted.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    /// <param name="envRepoUrl">The environment repository URL, or <c>null</c> while it is a placeholder.</param>
    /// <param name="otherApps">Other app slugs, for foreign destinations.</param>
    public IReadOnlyList<string> Violations(GitOpsApp app, string tier, string? envRepoUrl, IEnumerable<string> otherApps)
    {
        ArgumentNullException.ThrowIfNull(app);
        var problems = new List<string>();
        if (SourceRepos.Count != 1 || (envRepoUrl is not null && SourceRepos[0] != envRepoUrl))
        {
            problems.Add($"{Name}: sourceRepos is [{string.Join(", ", SourceRepos)}], expected only the environment repository {envRepoUrl}");
        }

        var expected = app.EnvironmentsOf(tier).SelectMany(app.Namespaces).ToHashSet(StringComparer.Ordinal);
        var actual = Destinations.Select(destination => destination.Namespace).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
        {
            problems.Add($"{Name}: destinations are [{string.Join(", ", actual.Order(StringComparer.Ordinal))}], expected [{string.Join(", ", expected.Order(StringComparer.Ordinal))}]");
        }

        problems.AddRange(Destinations.Where(destination => destination.Server != InCluster).Select(destination => $"{Name}: destination server {destination.Server} is not {InCluster}"));
        problems.AddRange(ClusterScopedSamples.Where(sample => PermitsClusterScoped(sample.Group, sample.Kind)).Select(sample => $"{Name}: permits cluster-scoped {Describe(sample)}"));
        var own = expected.Order(StringComparer.Ordinal).FirstOrDefault();
        if (own is null)
        {
            return problems;
        }

        problems.AddRange(GitOpsNames.ForbiddenNamespacedKinds
            .Select(forbidden => forbidden.Kind == "*" ? (forbidden.Group, Kind: "Policy") : forbidden)
            .Where(forbidden => PermitsNamespaced(forbidden.Group, forbidden.Kind, own))
            .Select(forbidden => $"{Name}: permits {Describe(forbidden)} in {own}"));
        var foreign = new[] { GitOpsNames.ArgoNamespace, "kube-system", "default", GitOpsNames.IngressNamespace, GitOpsNames.BackupNamespace }
            .Concat((otherApps ?? []).Where(other => other != app.Name).SelectMany(other => GitOpsNames.TierEnvironments(tier).Select(environment => $"{other}-{environment}")))
            .Concat(GitOpsNames.TierEnvironments(tier == "prod" ? "nonprod" : "prod").Select(environment => app.Namespace(environment)));
        problems.AddRange(foreign.Where(PermitsDestination).Select(namespaceName => $"{Name}: permits destination {namespaceName}"));
        problems.AddRange(AllowedSamples.Where(sample => !PermitsNamespaced(sample.Group, sample.Kind, own)).Select(sample => $"{Name}: refuses everyday {Describe(sample)} in {own}"));
        return problems;
    }

    private static bool Matches((string Group, string Kind) entry, string group, string kind) => Glob(entry.Group, group) && Glob(entry.Kind, kind);

    private static bool Glob(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string Describe((string Group, string Kind) kind) => kind.Group.Length == 0 ? kind.Kind : $"{kind.Kind}.{kind.Group}";

    private static IEnumerable<JsonElement> Items(JsonElement spec, string property)
    {
        var list = GitOpsCluster.Child(spec, property);
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
    }

    private static IReadOnlyList<(string Group, string Kind)> Kinds(JsonElement spec, string property) =>
        Items(spec, property).Select(item => (GitOpsCluster.Text(item, "group") ?? string.Empty, GitOpsCluster.Text(item, "kind") ?? string.Empty)).ToArray();
}
