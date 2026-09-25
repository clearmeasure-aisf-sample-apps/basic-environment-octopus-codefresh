using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>A deployable of an app descriptor (design §7.0, apps/schema.json).</summary>
/// <param name="Name">Deployable name; the Application is <c>&lt;app&gt;-&lt;deployable&gt;-&lt;env&gt;</c>.</param>
/// <param name="Part">Optional part: the deployable then runs in <c>&lt;app&gt;-&lt;part&gt;-&lt;env&gt;</c>.</param>
/// <param name="OctopusProject">The Octopus project that pins it (<c>argo.octopus.com/project</c>).</param>
/// <param name="Packaging"><c>kustomize</c>, <c>helm</c> or <c>raw</c>.</param>
public sealed record GitOpsDeployable(string Name, string? Part, string OctopusProject, string Packaging);

/// <summary>
/// What the GitOps capability tests read from one descriptor <c>apps/&lt;app&gt;.yaml</c>, and the names the tenant chart
/// (<c>gitops/platform/tenant</c>) derives from it. An oracle written independently of the chart.
/// </summary>
/// <param name="Name">App slug.</param>
/// <param name="Status"><c>active</c> or <c>frozen</c>.</param>
/// <param name="Environments">Application environments of the descriptor.</param>
/// <param name="Deployables">Deployables.</param>
/// <param name="HasDatabase">Whether the descriptor declares <c>database</c>.</param>
/// <param name="OctopusWorkerAccess">Whether <c>database.octopusWorkerAccess</c> is set.</param>
/// <param name="Previews">Whether <c>previews</c> is set.</param>
/// <param name="PrimaryRepository">The first repository (<c>owner/name</c>), or <c>null</c>.</param>
/// <param name="OctopusProjects">Octopus project names.</param>
/// <param name="Hosts"><c>hosts</c>: environment to the host of the main namespace, where it is not the default (R35).</param>
public sealed record GitOpsApp(
    string Name,
    string Status,
    IReadOnlyList<string> Environments,
    IReadOnlyList<GitOpsDeployable> Deployables,
    bool HasDatabase,
    bool OctopusWorkerAccess,
    bool Previews,
    string? PrimaryRepository,
    IReadOnlyList<string> OctopusProjects,
    IReadOnlyDictionary<string, string> Hosts)
{
    /// <summary><c>true</c> for <c>status: frozen</c> (decision 28).</summary>
    public bool IsFrozen => Status == "frozen";

    /// <summary>AppProject <c>app-&lt;app&gt;</c>.</summary>
    public string AppProject => $"app-{Name}";

    /// <summary>Per-app ImageValidatingPolicy of the release signer.</summary>
    public string SignerPolicy => $"app-{Name}-release-signatures";

    /// <summary>The ApplicationSet's Application that renders the tenant.</summary>
    public string TenantApplication => $"tenant-{Name}";

    /// <summary>The descriptor's environments that run on <paramref name="tier"/>, in lifecycle order.</summary>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public IReadOnlyList<string> EnvironmentsOf(string tier) =>
        GitOpsNames.TierEnvironments(tier).Where(environment => Environments.Contains(environment, StringComparer.Ordinal)).ToArray();

    /// <summary>Namespace of the app, or of one of its parts, in an environment.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="part">Optional part.</param>
    public string Namespace(string environment, string? part = null) => part is null ? $"{Name}-{environment}" : $"{Name}-{part}-{environment}";

    /// <summary>Every namespace of the app in an environment: the main one, then one per part.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public IReadOnlyList<string> Namespaces(string environment) =>
        new[] { Namespace(environment) }
            .Concat(Deployables.Select(deployable => deployable.Part).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(part => Namespace(environment, part)))
            .ToArray();

    /// <summary>Namespace a deployable runs in.</summary>
    /// <param name="deployable">The deployable.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public string NamespaceOf(GitOpsDeployable deployable, string environment) => Namespace(environment, deployable?.Part);

    /// <summary>ClusterSecretStore <c>&lt;app&gt;-&lt;env&gt;</c>.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public string Store(string environment) => $"{Name}-{environment}";

    /// <summary>Application of a deployable.</summary>
    /// <param name="deployable">The deployable.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public string Application(GitOpsDeployable deployable, string environment) => $"{Name}-{deployable?.Name}-{environment}";

    /// <summary>Application <c>&lt;app&gt;-db-&lt;env&gt;</c> of the database.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public string DatabaseApplication(string environment) => $"{Name}-db-{environment}";

    /// <summary>Static PersistentVolume and managed disk of the database.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public string Disk(string environment) => $"disk-{Name}-{environment}-db";
}

/// <summary>Platform facts of one tier from the tenant chart's values files (<c>gitops/platform/tenant/values*.yaml</c>).</summary>
/// <param name="Tier"><c>nonprod</c> or <c>prod</c>.</param>
/// <param name="EnvRepoUrl">Environment repository URL, possibly still a placeholder.</param>
/// <param name="SubscriptionId">Azure subscription of the tier, possibly still a placeholder.</param>
/// <param name="AppsDomain">The tier's apps domain (<c>&lt;ingress-ip-dashed&gt;.sslip.io</c>), or <c>null</c> while it is a placeholder.</param>
/// <param name="HostOverrides">
/// <c>platform.hostOverrides</c>: namespace to host, for hosts the tier provisioned in place of the apps domain (R35, the
/// Azure DNS label of the tier's ingress IP). Empty when the tier has none.
/// </param>
public sealed record TenantPlatformValues(string Tier, string EnvRepoUrl, string SubscriptionId, string? AppsDomain, IReadOnlyDictionary<string, string> HostOverrides)
{
    /// <summary>
    /// Host name of a namespace: its <see cref="HostOverrides"/> entry, else <c>&lt;namespace&gt;.&lt;apps domain&gt;</c>
    /// (decision 21).
    /// </summary>
    /// <param name="namespaceName">App namespace.</param>
    /// <exception cref="InvalidOperationException">The namespace has no override and the apps domain is not provisioned yet.</exception>
    public string Host(string namespaceName) =>
        HostOverrides.TryGetValue(namespaceName, out var host)
            ? host
            : AppsDomain is null ? throw new InvalidOperationException($"the {Tier} apps domain is not provisioned") : $"{namespaceName}.{AppsDomain}";
}

/// <summary>Fixed platform names of ADR-IR34 that the GitOps tests use.</summary>
public static partial class GitOpsNames
{
    /// <summary>The conformance fixture app, the only target of destructive tests.</summary>
    public const string FixtureApp = "sandbox";

    /// <summary>App #1.</summary>
    public const string FirstApp = "workorders";

    /// <summary>Namespace of Argo CD.</summary>
    public const string ArgoNamespace = "argocd";

    /// <summary>Namespace of the Gateway and the ListenerSets.</summary>
    public const string IngressNamespace = "platform-ingress";

    /// <summary>Namespace of the backup CronJobs.</summary>
    public const string BackupNamespace = "platform-backup";

    /// <summary>Environments whose databases are backed up (tenant chart <c>platform.database.backupEnvironments</c>).</summary>
    public static IReadOnlyList<string> BackupEnvironments { get; } = ["uat", "prod"];

    /// <summary>NetworkPolicies the tenant chart renders in every app namespace.</summary>
    public static IReadOnlyList<string> NetworkPolicies { get; } = ["platform-allow-ingress-gateway", "platform-allow-same-app", "platform-default-deny-ingress"];

    /// <summary>The extra NetworkPolicy of the main namespace of an app with a database.</summary>
    public const string DatabaseNetworkPolicy = "platform-allow-database-clients";

    /// <summary>
    /// Namespaced kinds AppProject <c>app-&lt;app&gt;</c> denies (§7.0 AppProjects): what the platform renders for the app,
    /// every Kyverno kind and every Argo CD kind, as (group, kind).
    /// </summary>
    public static IReadOnlyList<(string Group, string Kind)> ForbiddenNamespacedKinds { get; } =
    [
        ("", "ResourceQuota"),
        ("", "LimitRange"),
        ("networking.k8s.io", "NetworkPolicy"),
        ("external-secrets.io", "SecretStore"),
        ("external-secrets.io", "ClusterSecretStore"),
        ("kyverno.io", "*"),
        ("policies.kyverno.io", "*"),
        ("gateway.networking.k8s.io", "Gateway"),
        ("gateway.networking.k8s.io", "ListenerSet"),
        ("argoproj.io", "Application"),
        ("argoproj.io", "ApplicationSet"),
        ("argoproj.io", "AppProject"),
    ];

    /// <summary>
    /// Diagnostic paths the platform hides at the Gateway in uat and prod (§7.9, decision 21): app #1's, and the
    /// fixture's. Other apps choose their own.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> HiddenDiagnosticPaths { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [FirstApp] = ["/_healthcheck/detailed", "/_demo", "/_diagnostics", "/mcp"],
        [FixtureApp] = ["/_diagnostics"],
    };

    /// <summary>The fixed environment-to-tier map: prod on prod, tdd and uat on nonprod.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string TierOf(string environment) => environment == "prod" ? "prod" : "nonprod";

    /// <summary>Environments of a tier in lifecycle order.</summary>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static IReadOnlyList<string> TierEnvironments(string tier) => tier == "prod" ? ["prod"] : ["tdd", "uat"];

    /// <summary>The tier key of a harness tier.</summary>
    /// <param name="tier">The tier.</param>
    public static string Key(PlatformTier tier) => tier == PlatformTier.Prod ? "prod" : "nonprod";

    /// <summary>
    /// App vault <c>kv-&lt;app&gt;-&lt;e&gt;-&lt;hash4&gt;</c> (decision 8): the first four hex digits of
    /// sha1("&lt;subscription&gt;/&lt;app&gt;/&lt;env&gt;"), the subscription trimmed and lowercase.
    /// </summary>
    /// <param name="subscriptionId">Azure subscription ID.</param>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string VaultName(string subscriptionId, string app, string environment)
    {
        ArgumentNullException.ThrowIfNull(subscriptionId);
        ArgumentNullException.ThrowIfNull(environment);
#pragma warning disable CA5350 // A naming hash shared with Terraform and the tenant chart, not a security control.
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes($"{subscriptionId.Trim().ToLowerInvariant()}/{app}/{environment}"));
#pragma warning restore CA5350
        return $"kv-{app}-{environment[0]}-{Convert.ToHexString(digest)[..4].ToLowerInvariant()}";
    }

    /// <summary><c>true</c> when <paramref name="value"/> is a DNS name (no placeholder).</summary>
    /// <param name="value">Candidate domain.</param>
    public static bool IsDomain(string? value) => value is not null && DomainPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)+$")]
    private static partial Regex DomainPattern();
}

/// <summary>Reads the descriptors and the tenant chart values of the environment repository checked out with the tests.</summary>
public static class GitOpsRepository
{
    /// <summary>The environment repository root (the folder that holds <c>tests/Platform.Conformance.sln</c>).</summary>
    public static string Root => RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance);

    /// <summary>The tenant chart folder.</summary>
    public static string TenantChart => Path.Combine(Root, "gitops", "platform", "tenant");

    /// <summary>Every descriptor <c>apps/*.yaml</c>, sorted by name.</summary>
    public static IReadOnlyList<GitOpsApp> LoadApps() =>
        Directory.EnumerateFiles(Path.Combine(Root, "apps"), "*.yaml").Order(StringComparer.Ordinal).Select(LoadFile).ToArray();

    /// <summary>One descriptor.</summary>
    /// <param name="name">App slug.</param>
    /// <exception cref="FileNotFoundException">No descriptor of that name.</exception>
    public static GitOpsApp LoadApp(string name) => LoadFile(Path.Combine(Root, "apps", $"{name}.yaml"));

    /// <summary>Reads a descriptor file.</summary>
    /// <param name="path">Path of <c>apps/&lt;app&gt;.yaml</c>.</param>
    public static GitOpsApp LoadFile(string path)
    {
        var root = ReadMapping(path);
        var database = Child(root, "database");
        return new GitOpsApp(
            Scalar(root, "name") ?? Path.GetFileNameWithoutExtension(path),
            Scalar(root, "status") ?? "active",
            Sequence(root, "environments") is { Count: > 0 } environments ? environments : ["tdd", "uat", "prod"],
            Mappings(root, "deployables")
                .Select(deployable => new GitOpsDeployable(
                    Scalar(deployable, "name") ?? string.Empty,
                    Scalar(deployable, "part"),
                    Scalar(deployable, "octopusProject") ?? string.Empty,
                    Scalar(deployable, "packaging") ?? string.Empty))
                .ToArray(),
            database is not null && Scalar(database, "engine") is not null,
            database is not null && Scalar(database, "octopusWorkerAccess") == "true",
            Scalar(root, "previews") == "true",
            Mappings(root, "repositories").Select(repository => Scalar(repository, "name")).FirstOrDefault(),
            Mappings(Child(root, "octopus"), "projects").Select(project => Scalar(project, "name") ?? string.Empty).ToArray(),
            ScalarMap(Child(root, "hosts")));
    }

    /// <summary>The platform block of the tenant chart for a tier: <c>values.yaml</c> overlaid with <c>values-&lt;tier&gt;.yaml</c>.</summary>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static TenantPlatformValues TenantValues(string tier)
    {
        var defaults = Child(ReadMapping(Path.Combine(TenantChart, "values.yaml")), "platform");
        var tierValues = Child(ReadMapping(Path.Combine(TenantChart, $"values-{tier}.yaml")), "platform");
        string Value(string key) => Scalar(tierValues, key) ?? Scalar(defaults, key) ?? string.Empty;
        var domain = Value("appsDomain");
        return new TenantPlatformValues(tier, Value("envRepoUrl"), Value("subscriptionId"), GitOpsNames.IsDomain(domain) ? domain : null, ScalarMap(Child(tierValues, "hostOverrides")));
    }

    private static Dictionary<string, string> ScalarMap(YamlMappingNode? node) =>
        node is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : node.Children
                .Where(pair => pair.Key is YamlScalarNode { Value.Length: > 0 } && pair.Value is YamlScalarNode { Value.Length: > 0 })
                .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => ((YamlScalarNode)pair.Value).Value!, StringComparer.Ordinal);

    private static YamlMappingNode ReadMapping(string path)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(path));
        stream.Load(reader);
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode mapping ? mapping : new YamlMappingNode();
    }

    private static YamlMappingNode? Child(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child as YamlMappingNode : null;

    private static string? Scalar(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? string.Empty).ToArray()
            : [];

    private static IEnumerable<YamlMappingNode> Mappings(YamlMappingNode? node, string key) =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>()
            : [];
}
