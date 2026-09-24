using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// The §7.0 names the CAP-AZ tests read: resource groups, clusters, monitoring objects, Octopus runbooks and the
/// conformance fixture <c>sandbox</c>. The names are contracts, so the tests derive them instead of reading copies.
/// </summary>
public static partial class AzurePlatform
{
    /// <summary>Octopus project of the tier runbooks.</summary>
    public const string InfrastructureProject = "platform-infrastructure";

    /// <summary>The conformance fixture app (and its Octopus project).</summary>
    public const string Sandbox = "sandbox";

    /// <summary>App #1, whose images serve as another app's images in the registry-path test.</summary>
    public const string OtherApp = "workorders";

    /// <summary>Namespace of the backup and restore CronJobs.</summary>
    public const string BackupNamespace = "platform-backup";

    /// <summary>Unsigned fixture image, pushed once and never pinned (§7.0 Registry retention).</summary>
    public const string UnsignedFixture = "apps/sandbox/unsigned:0.0.0-fixture";

    /// <summary>Group of the registry and the build cluster.</summary>
    public const string BuildGroup = "rg-platform-build";

    /// <summary>Group of the global state account.</summary>
    public const string GlobalGroup = "rg-platform-global";

    /// <summary>Built-in role AcrPull, the only grant a tier identity may hold outside its tier.</summary>
    public const string AcrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d";

    /// <summary>The two app-cluster tiers.</summary>
    public static IReadOnlyList<PlatformTier> AppTiers { get; } = [PlatformTier.NonProd, PlatformTier.Prod];

    /// <summary>The three clusters of §7.0.</summary>
    public static IReadOnlyList<PlatformTier> ClusterTiers { get; } = [PlatformTier.Build, PlatformTier.NonProd, PlatformTier.Prod];

    /// <summary>The budget names of §7.0.</summary>
    public static IReadOnlyList<string> Budgets { get; } = ["budget-platform-build", "budget-platform-nonprod", "budget-platform-prod"];

    /// <summary><c>aks-platform-&lt;tier&gt;</c>.</summary>
    /// <param name="tier">The tier.</param>
    public static string ClusterName(PlatformTier tier) => $"aks-platform-{tier.ToKey()}";

    /// <summary>Group of the cluster: <c>rg-platform-build</c> or <c>rg-platform-&lt;tier&gt;-aks</c>.</summary>
    /// <param name="tier">The tier.</param>
    public static string ClusterGroup(PlatformTier tier) => tier == PlatformTier.Build ? BuildGroup : $"rg-platform-{tier.ToKey()}-aks";

    /// <summary>AKS node group <c>rg-platform-&lt;tier&gt;-aks-nodes</c>.</summary>
    /// <param name="tier">The tier.</param>
    public static string NodeGroup(PlatformTier tier) => $"rg-platform-{tier.ToKey()}-aks-nodes";

    /// <summary><c>rg-platform-&lt;tier&gt;-shared</c>: network, IPs, workspace, state and backup accounts.</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string SharedGroup(PlatformTier tier) => $"rg-platform-{tier.ToKey()}-shared";

    /// <summary><c>rg-platform-&lt;tier&gt;-apps</c>: app vaults, App Insights and app identities.</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string AppsGroup(PlatformTier tier) => $"rg-platform-{tier.ToKey()}-apps";

    /// <summary>Every platform group of §7.0 (the ten foundation groups and the three node groups).</summary>
    public static IReadOnlyList<string> PlatformGroups { get; } =
    [
        GlobalGroup,
        BuildGroup,
        NodeGroup(PlatformTier.Build),
        .. AppTiers.SelectMany(tier => new[] { SharedGroup(tier), ClusterGroup(tier), $"rg-platform-{tier.ToKey()}-data", AppsGroup(tier), NodeGroup(tier) }),
    ];

    /// <summary>Octopus environment of the tier runbooks: <c>infra-nonprod</c> or <c>infra-prod</c>.</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string InfraEnvironment(PlatformTier tier) => $"infra-{tier.ToKey()}";

    /// <summary>Alert processing rule <c>apr-sleep-&lt;tier&gt;</c> (in the cluster group).</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string SleepRule(PlatformTier tier) => $"apr-sleep-{tier.ToKey()}";

    /// <summary>Static ingress IP <c>pip-platform-&lt;tier&gt;-ingress</c> (in the shared group).</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string IngressAddress(PlatformTier tier) => $"pip-platform-{tier.ToKey()}-ingress";

    /// <summary>VNet <c>vnet-platform-&lt;tier&gt;</c> (in the shared group).</summary>
    /// <param name="tier">An app-cluster tier.</param>
    public static string VirtualNetwork(PlatformTier tier) => $"vnet-platform-{tier.ToKey()}";

    /// <summary>tdd and uat run on nonprod, prod on prod (the fixed tier map).</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public static PlatformTier TierOf(string environment) => environment == "prod" ? PlatformTier.Prod : PlatformTier.NonProd;

    /// <summary>App namespace <c>&lt;app&gt;-&lt;env&gt;</c>.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string Namespace(string app, string environment) => $"{app}-{environment}";

    /// <summary>Default apps domain of a tier: its ingress IP with dashes, under sslip.io (ADR-IR34 decision 21).</summary>
    /// <param name="ingressIpAddress">The address of <c>pip-platform-&lt;tier&gt;-ingress</c>.</param>
    public static string AppsDomain(string ingressIpAddress) => $"{ingressIpAddress.Trim().Replace('.', '-')}.sslip.io";

    /// <summary>Nightly backup CronJob of an app-environment in <c>platform-backup</c>.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="environment">uat or prod.</param>
    public static string BackupCronJob(string app, string environment) => $"db-backup-{app}-{environment}";

    /// <summary>
    /// App vault <c>kv-&lt;app&gt;-&lt;e&gt;-&lt;hash4&gt;</c>: the first four hex digits of
    /// sha1("&lt;subscription, lowercase&gt;/&lt;app&gt;/&lt;env&gt;"), as terraform/apps/descriptor computes it.
    /// </summary>
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

    /// <summary>The resource group named in an ARM ID, or <c>null</c> for a subscription or management-group scope.</summary>
    /// <param name="scope">An ARM resource ID or scope.</param>
    public static string? ResourceGroupOf(string scope)
    {
        var match = ResourceGroupPattern().Match(scope);
        return match.Success ? match.Groups["group"].Value : null;
    }

    /// <summary>
    /// The tier a resource group belongs to: <c>nonprod</c> or <c>prod</c> for <c>rg-platform-&lt;tier&gt;-*</c> and
    /// <c>rg-app-&lt;app&gt;-&lt;tier&gt;</c>, <c>build</c> for the build and global groups, <c>null</c> otherwise.
    /// </summary>
    /// <param name="resourceGroup">Group name.</param>
    public static string? TierOfGroup(string resourceGroup)
    {
        var name = resourceGroup.ToLowerInvariant();
        if (name == BuildGroup || name == GlobalGroup || name == NodeGroup(PlatformTier.Build))
        {
            return "build";
        }

        var match = TierGroupPattern().Match(name);
        return match.Success ? match.Groups["tier"].Value : null;
    }

    [GeneratedRegex("/resourceGroups/(?<group>[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceGroupPattern();

    [GeneratedRegex("^(?:rg-platform-(?<tier>nonprod|prod)-[a-z-]+|rg-app-[a-z][a-z0-9]{2,11}-(?<tier>nonprod|prod))$")]
    private static partial Regex TierGroupPattern();
}
