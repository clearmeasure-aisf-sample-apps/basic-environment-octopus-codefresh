using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Onboarding.Naming;

/// <summary>
/// Naming rules of design §7.0 and ADR-IR34 decisions 8 and 10: the app slug, the fixed environment-to-tier map and
/// the names every consumer derives from a descriptor (namespaces, stores, vaults, disks, projects, identities).
/// </summary>
internal static partial class PlatformNames
{
    /// <summary>The conformance fixture app; the schema accepts it, <c>new</c> refuses it.</summary>
    public const string FixtureApp = "sandbox";

    /// <summary>The only GitHub organization the platform's Git integration reaches.</summary>
    public const string GitHubOrg = "clearmeasure-aisf-sample-apps";

    /// <summary>Registry host placeholder used in committed files.</summary>
    public const string RegistryHost = "<acr-name>.azurecr.io";

    /// <summary>The Codefresh runtime every spec names (§7.0).</summary>
    public const string CodefreshRuntime = "aks-platform-build/codefresh";

    /// <summary>The database deployable name; reserved for <c>database</c>.</summary>
    public const string DatabaseDeployable = "db";

    /// <summary>Reserved app words (ADR-IR34 decision 10), <c>sandbox</c> excluded.</summary>
    public static IReadOnlyList<string> ReservedApps { get; } =
        ["apps", "argo", "argocd", "cert", "default", "external", "infra", "kube", "kyverno", "octopus", "platform", "system"];

    /// <summary>Application environments in lifecycle order.</summary>
    public static IReadOnlyList<string> Environments { get; } = ["tdd", "uat", "prod"];

    /// <summary>Tiers in order.</summary>
    public static IReadOnlyList<string> Tiers { get; } = ["nonprod", "prod"];

    /// <summary>Vault keys written by <c>terraform/apps/tier</c>; app secrets may not reuse them.</summary>
    public static IReadOnlyList<string> PlatformVaultKeys { get; } =
        ["db-sa-password", "db-migrator-password", "db-app-password", "appinsights-connection-string", "azure-client-id"];

    [GeneratedRegex("^[a-z][a-z0-9]{2,11}$")]
    private static partial Regex AppPattern();

    [GeneratedRegex("^[a-z][a-z0-9]{1,11}$")]
    private static partial Regex PartPattern();

    /// <summary>Returns why <paramref name="app"/> is not a usable slug, or <c>null</c> when it is.</summary>
    /// <param name="app">Candidate slug.</param>
    /// <param name="allowFixture"><c>true</c> accepts <see cref="FixtureApp"/> (committed descriptors); <c>false</c> refuses it (<c>new</c>).</param>
    public static string? SlugError(string app, bool allowFixture)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!AppPattern().IsMatch(app))
        {
            return $"'{app}' is not a valid app slug: 3 to 12 characters, a lowercase letter first, then lowercase letters or digits, no dash";
        }

        if (ReservedApps.Contains(app, StringComparer.Ordinal))
        {
            return $"'{app}' is a reserved word ({string.Join(", ", ReservedApps)})";
        }

        if (!allowFixture && app == FixtureApp)
        {
            return $"'{FixtureApp}' is reserved for the conformance fixture";
        }

        return null;
    }

    /// <summary><c>true</c> when <paramref name="part"/> matches the part and deployable pattern.</summary>
    /// <param name="part">Candidate part or deployable name.</param>
    public static bool IsPartName(string part) => PartPattern().IsMatch(part);

    /// <summary>The tier of an application environment: prod for prod, nonprod otherwise (fixed map).</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string TierOf(string environment) => environment switch
    {
        "tdd" or "uat" => "nonprod",
        "prod" => "prod",
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Expected tdd, uat or prod."),
    };

    /// <summary>The one-letter environment of vault names: t, u or p.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string ShortEnvironment(string environment) => environment switch
    {
        "tdd" => "t",
        "uat" => "u",
        "prod" => "p",
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Expected tdd, uat or prod."),
    };

    /// <summary>
    /// First four lowercase hex digits of <c>sha1("&lt;AZURE_SUBSCRIPTION_ID&gt;/&lt;app&gt;/&lt;env&gt;")</c>, as Terraform's
    /// <c>substr(sha1(...), 0, 4)</c> computes them. The subscription ID is trimmed and lowercased (its ARM form).
    /// </summary>
    /// <param name="subscriptionId">Azure subscription ID.</param>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string VaultHash(string subscriptionId, string app, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        var input = $"{subscriptionId.Trim().ToLowerInvariant()}/{app}/{environment}";
#pragma warning disable CA5350 // A naming hash shared with Terraform, not a security control.
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5350
        return Convert.ToHexString(digest)[..4].ToLowerInvariant();
    }

    /// <summary>App vault name <c>kv-&lt;app&gt;-&lt;e&gt;-&lt;hash4&gt;</c>; without a subscription the hash stays <c>&lt;hash4&gt;</c>.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="subscriptionId">Azure subscription ID, or <c>null</c>.</param>
    public static string VaultName(string app, string environment, string? subscriptionId) =>
        $"kv-{app}-{ShortEnvironment(environment)}-{(string.IsNullOrWhiteSpace(subscriptionId) ? "<hash4>" : VaultHash(subscriptionId, app, environment))}";

    /// <summary>Namespace of an app environment, or of a part of it.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="part">Optional part.</param>
    public static string Namespace(string app, string environment, string? part = null) =>
        part is null ? $"{app}-{environment}" : $"{app}-{part}-{environment}";

    /// <summary>Host name of an app environment, or of a part of it.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="part">Optional part.</param>
    public static string Host(string app, string environment, string? part = null) =>
        $"{Namespace(app, environment, part)}.<apps-domain-{TierOf(environment)}>";

    /// <summary>Argo CD Application of a deployable in an environment.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="deployable">Deployable name (db for the database).</param>
    /// <param name="environment">tdd, uat or prod.</param>
    public static string Application(string app, string deployable, string environment) => $"{app}-{deployable}-{environment}";

    /// <summary>Registry repository of an app image.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="image">Image name under the app path.</param>
    public static string Repository(string app, string image) => $"apps/{app}/{image}";

    /// <summary>Full image name as the pins write it, with the registry placeholder.</summary>
    /// <param name="app">App slug.</param>
    /// <param name="image">Image name under the app path.</param>
    public static string ImageReference(string app, string image) => $"{RegistryHost}/{Repository(app, image)}";

    /// <summary>
    /// Maps a provisioned registry host (<c>&lt;name&gt;.azurecr.io</c>, filled in at provisioning) back to
    /// <see cref="RegistryHost"/>, so committed pins compare equal to the descriptor's image references.
    /// </summary>
    /// <param name="reference">An image reference.</param>
    /// <returns>The reference with its registry host normalized.</returns>
    public static string NormalizeRegistry(string reference) =>
        System.Text.RegularExpressions.Regex.Replace(reference, @"^[a-z0-9]{5,50}\.azurecr\.io/", RegistryHost + "/");
}
