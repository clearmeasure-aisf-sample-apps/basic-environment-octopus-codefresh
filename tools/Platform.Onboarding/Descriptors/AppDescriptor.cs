using System.Globalization;
using System.Text.Json.Nodes;
using Platform.Onboarding.Naming;

namespace Platform.Onboarding.Descriptors;

/// <summary>A source repository of an app.</summary>
/// <param name="Name"><c>owner/name</c>, or a §7.0 placeholder such as <c>&lt;sandbox-app-repo&gt;</c>.</param>
/// <param name="DefaultBranch">The branch releases come from.</param>
internal sealed record RepositoryRef(string Name, string DefaultBranch)
{
    /// <summary><c>true</c> while the name is still a placeholder.</summary>
    public bool IsPlaceholder => Name.StartsWith('<');
}

/// <summary>An Octopus project of an app.</summary>
/// <param name="Name">Project name and slug.</param>
/// <param name="Lifecycle">Lifecycle of the Default channel.</param>
/// <param name="Channels">Channel names.</param>
internal sealed record OctopusProjectSpec(string Name, string Lifecycle, IReadOnlyList<string> Channels);

/// <summary>Helm settings of a Helm deployable.</summary>
/// <param name="Chart">Chart directory relative to <c>gitops/apps/&lt;app&gt;/</c>.</param>
/// <param name="ImageReplacePaths">Value of <c>argo.octopus.com/image-replace-paths</c>.</param>
internal sealed record HelmSpec(string Chart, IReadOnlyList<string> ImageReplacePaths);

/// <summary>A deployable: one Argo CD Application per environment.</summary>
/// <param name="Name">Deployable name.</param>
/// <param name="Part">Optional part (namespace <c>&lt;app&gt;-&lt;part&gt;-&lt;env&gt;</c>).</param>
/// <param name="OctopusProject">The Octopus project that pins it.</param>
/// <param name="Packaging">kustomize, helm or raw.</param>
/// <param name="Images">Image names under <c>apps/&lt;app&gt;/</c>.</param>
/// <param name="Helm">Helm settings, for Helm deployables.</param>
internal sealed record Deployable(string Name, string? Part, string OctopusProject, string Packaging, IReadOnlyList<string> Images, HelmSpec? Helm);

/// <summary>The optional database.</summary>
/// <param name="Engine">Platform database component.</param>
/// <param name="OctopusWorkerAccess">Whether <c>octopus-worker-&lt;env&gt;</c> may reach the database.</param>
internal sealed record DatabaseSpec(string Engine, bool OctopusWorkerAccess);

/// <summary>Optional Azure access.</summary>
/// <param name="ResourceGroup">Creates <c>rg-app-&lt;app&gt;-&lt;tier&gt;</c>.</param>
/// <param name="WorkloadIdentity">Creates <c>id-&lt;app&gt;-&lt;env&gt;-app</c>.</param>
/// <param name="ServiceAccount">Service account of the federated credential.</param>
/// <param name="Roles">Roles on the app resource group.</param>
internal sealed record AzureSpec(bool ResourceGroup, bool WorkloadIdentity, string? ServiceAccount, IReadOnlyList<string> Roles)
{
    /// <summary>No Azure access.</summary>
    public static AzureSpec None { get; } = new(false, false, null, []);

    /// <summary><c>true</c> when the app declares any Azure access (security owners review it).</summary>
    public bool Any => ResourceGroup || WorkloadIdentity || Roles.Count > 0;
}

/// <summary>An app-specific vault key.</summary>
/// <param name="Name">Key Vault secret name.</param>
/// <param name="Generate">Terraform generates a random value when <c>true</c>.</param>
internal sealed record SecretSpec(string Name, bool Generate);

/// <summary>A validated app descriptor with the schema defaults applied.</summary>
internal sealed record AppDescriptor
{
    /// <summary>App slug.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description.</summary>
    public string? Description { get; init; }

    /// <summary>active or frozen.</summary>
    public string Status { get; init; } = "active";

    /// <summary>Optional end of the course.</summary>
    public DateOnly? Expires { get; init; }

    /// <summary>Source repositories; the first is primary.</summary>
    public required IReadOnlyList<RepositoryRef> Repositories { get; init; }

    /// <summary>Application environments.</summary>
    public IReadOnlyList<string> Environments { get; init; } = PlatformNames.Environments;

    /// <summary>
    /// Host of the main namespace per environment where it is not the default <c>&lt;app&gt;-&lt;env&gt;.&lt;apps-domain&gt;</c>
    /// (R35: a host the tier provisioned, such as the Azure DNS label of its ingress IP).
    /// </summary>
    public IReadOnlyDictionary<string, string> Hosts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Codefresh project names.</summary>
    public required IReadOnlyList<string> CodefreshProjects { get; init; }

    /// <summary>Octopus projects.</summary>
    public required IReadOnlyList<OctopusProjectSpec> OctopusProjects { get; init; }

    /// <summary>Whether Octopus gets <c>azure-&lt;app&gt;-&lt;env&gt;</c> accounts.</summary>
    public bool OctopusAzureAccount { get; init; }

    /// <summary>Deployables.</summary>
    public required IReadOnlyList<Deployable> Deployables { get; init; }

    /// <summary>The database, if any.</summary>
    public DatabaseSpec? Database { get; init; }

    /// <summary>Azure access.</summary>
    public AzureSpec Azure { get; init; } = AzureSpec.None;

    /// <summary>Whether phase-6 previews are on.</summary>
    public bool Previews { get; init; }

    /// <summary>App vault keys.</summary>
    public IReadOnlyList<SecretSpec> Secrets { get; init; } = [];

    /// <summary>The primary repository.</summary>
    public RepositoryRef PrimaryRepository => Repositories[0];

    /// <summary><c>true</c> when the app is parked.</summary>
    public bool IsFrozen => Status == "frozen";

    /// <summary>Environments grouped by tier, in order.</summary>
    public IEnumerable<IGrouping<string, string>> EnvironmentsByTier =>
        PlatformNames.Environments.Where(Environments.Contains).GroupBy(PlatformNames.TierOf);

    /// <summary>Distinct parts, without the null part.</summary>
    public IEnumerable<string> Parts => Deployables.Select(deployable => deployable.Part).OfType<string>().Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Builds the typed descriptor from JSON that already passed the schema. Absent optional keys take the schema
    /// defaults.
    /// </summary>
    /// <param name="root">Descriptor JSON.</param>
    public static AppDescriptor FromJson(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var octopus = root["octopus"]!.AsObject();
        var azure = root["azure"] as JsonObject;
        var database = root["database"] as JsonObject;
        var expires = Str(root, "expires");
        return new AppDescriptor
        {
            Name = Str(root, "name")!,
            Description = Str(root, "description"),
            Status = Str(root, "status") ?? "active",
            Expires = expires is null ? null : DateOnly.ParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Repositories = root["repositories"]!.AsArray().OfType<JsonObject>()
                .Select(repository => new RepositoryRef(Str(repository, "name")!, Str(repository, "defaultBranch")!)).ToArray(),
            Environments = Strings(root["environments"]) is { Count: > 0 } environments
                ? PlatformNames.Environments.Where(environments.Contains).ToArray()
                : PlatformNames.Environments,
            Hosts = (root["hosts"] as JsonObject ?? [])
                .Select(pair => (Environment: pair.Key, Host: pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : null))
                .Where(entry => entry.Host is not null)
                .ToDictionary(entry => entry.Environment, entry => entry.Host!, StringComparer.Ordinal),
            CodefreshProjects = Strings(root["codefresh"]?["projects"]),
            OctopusProjects = octopus["projects"]!.AsArray().OfType<JsonObject>()
                .Select(project => new OctopusProjectSpec(
                    Str(project, "name")!,
                    Str(project, "lifecycle") ?? "platform-standard",
                    Strings(project["channels"]) is { Count: > 0 } channels ? channels : ["Default", "Hotfix"]))
                .ToArray(),
            OctopusAzureAccount = Bool(octopus, "azureAccount"),
            Deployables = root["deployables"]!.AsArray().OfType<JsonObject>().Select(ReadDeployable).ToArray(),
            Database = database is null ? null : new DatabaseSpec(Str(database, "engine")!, Bool(database, "octopusWorkerAccess")),
            Azure = azure is null
                ? AzureSpec.None
                : new AzureSpec(Bool(azure, "resourceGroup"), Bool(azure, "workloadIdentity"), Str(azure, "serviceAccount"), Strings(azure["roles"])),
            Previews = Bool(root, "previews"),
            Secrets = (root["secrets"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(secret => new SecretSpec(Str(secret, "name")!, Bool(secret, "generate"))).ToArray(),
        };
    }

    private static Deployable ReadDeployable(JsonObject deployable)
    {
        var helm = deployable["helm"] as JsonObject;
        return new Deployable(
            Str(deployable, "name")!,
            Str(deployable, "part"),
            Str(deployable, "octopusProject")!,
            Str(deployable, "packaging")!,
            Strings(deployable["images"]),
            helm is null ? null : new HelmSpec(Str(helm, "chart")!, Strings(helm["imageReplacePaths"])));
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToArray()
            : [];
}
