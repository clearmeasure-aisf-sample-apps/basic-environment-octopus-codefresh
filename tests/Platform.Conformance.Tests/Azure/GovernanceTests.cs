using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-012: production is segmented. Every tier identity (the foundation's in <c>rg-platform-&lt;tier&gt;-shared</c> and
/// <c>-aks</c>, the per-app ones in <c>-apps</c>) holds roles only in its own tier; the one exception is AcrPull on the shared
/// registry. The conformance principal's reads are not tier identities. No virtual network in a platform group is peered.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class TierSegmentationTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-012")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ListRoleAssignments_TierIdentities_HaveNoCrossTierWrite()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var identities = new Dictionary<string, (string Name, string Tier)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in AzurePlatform.AppTiers)
        {
            foreach (var group in new[] { AzurePlatform.SharedGroup(tier), AzurePlatform.ClusterGroup(tier), AzurePlatform.AppsGroup(tier) })
            {
                foreach (var identity in await Arm.ListIdentitiesAsync(group, cancellationToken))
                {
                    if (ArmReader.Text(identity, "properties", "principalId") is { Length: > 0 } principal)
                    {
                        identities[principal] = (ArmReader.Text(identity, "name") ?? principal, tier.ToKey());
                    }
                }
            }
        }

        var assignments = await Governance.RoleAssignmentsAsync(Arm, Azure, cancellationToken);
        var violations = assignments
            .Where(assignment => identities.ContainsKey(assignment.PrincipalId))
            .Where(assignment => !Governance.StaysInTier(assignment, identities[assignment.PrincipalId].Tier))
            .Select(assignment => $"{identities[assignment.PrincipalId].Name} ({identities[assignment.PrincipalId].Tier}): role {assignment.RoleId} on {assignment.Scope}")
            .ToArray();

        identities.ShouldNotBeEmpty("no tier identity exists in rg-platform-<tier>-{shared,aks,apps}; terraform/foundation creates them");
        violations.ShouldBeEmpty("tier identities must hold roles only in their own tier (AcrPull on the registry excepted)");
    }

    [Test]
    [Capability("CAP-AZ-012")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ListVirtualNetworks_BothTiers_HaveNoPeering()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var networks = new List<JsonElement>();

        foreach (var group in await Governance.PlatformGroupsAsync(Azure, cancellationToken))
        {
            networks.AddRange(await Arm.ListVirtualNetworksAsync(group, cancellationToken));
        }

        var names = networks.Select(network => ArmReader.Text(network, "name") ?? string.Empty).ToArray();
        var peered = networks
            .Where(network => network.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("virtualNetworkPeerings", out var peerings)
                && peerings.ValueKind == JsonValueKind.Array
                && peerings.GetArrayLength() > 0)
            .Select(network => ArmReader.Text(network, "id") ?? string.Empty)
            .ToArray();

        foreach (var tier in AzurePlatform.AppTiers)
        {
            names.ShouldContain(AzurePlatform.VirtualNetwork(tier), $"{AzurePlatform.VirtualNetwork(tier)} must exist in {AzurePlatform.SharedGroup(tier)}");
        }

        peered.ShouldBeEmpty("no platform network may be peered: the tiers share nothing on the network");
    }
}

/// <summary>
/// CAP-AZ-013: every platform and app resource carries cost tags. Every resource in <c>rg-platform-*</c> and <c>rg-app-*</c>
/// carries <c>platform-tier</c>; platform resources also <c>platform-component</c>; per-app resources (in <c>rg-app-*</c>, or
/// named for an app: vaults, App Insights, alert rules, disks and app identities) also <c>platform-app</c> and
/// <c>platform-env</c>. Foreign groups (NetworkWatcherRG, ai-model) are not read, and neither is the one resource Azure
/// creates by itself (<see cref="Governance.IsAzureGenerated"/>).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class CostTagTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-013")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ListResources_PlatformAndAppGroups_CarryCostTags()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var groups = await Governance.PlatformGroupsAsync(Azure, cancellationToken, includeAppGroups: true);
        var untagged = new List<string>();
        var count = 0;

        foreach (var group in groups)
        {
            foreach (var resource in await Arm.ListResourcesAsync(group, cancellationToken))
            {
                var name = ArmReader.Text(resource, "name") ?? string.Empty;
                if (Governance.IsAzureGenerated(ArmReader.Text(resource, "type") ?? string.Empty, name)
                    || Governance.IsWorkerVolume(group, ArmReader.Text(resource, "type") ?? string.Empty, ArmReader.Tags(resource)))
                {
                    continue;
                }

                count++;
                var missing = Governance.MissingCostTags(group, name, ArmReader.Tags(resource));
                if (missing.Count > 0)
                {
                    untagged.Add($"{group}/{ArmReader.Text(resource, "type")}/{name}: {string.Join(", ", missing)}");
                }
            }
        }

        count.ShouldBeGreaterThan(0, "no resource found in rg-platform-* or rg-app-*");
        untagged.ShouldBeEmpty($"{untagged.Count} of {count} resources lack cost tags (§7.0 Azure tags)");
    }
}

/// <summary>
/// CAP-AZ-014: budgets cover every platform resource group, node groups included. The three budgets of §7.0 exist, and
/// the union of their resource-group filters names every <c>rg-platform-*</c> group of the subscription and every §7.0
/// platform group, the AKS node groups among them. Inconclusive when the subscription cannot hold budgets (the
/// foundation then skips them) or the conformance principal cannot read them.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class BudgetTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-014")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ListBudgets_PlatformGroups_AreEachCovered()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var groups = (await Governance.PlatformGroupsAsync(Azure, cancellationToken)).Union(AzurePlatform.PlatformGroups, StringComparer.OrdinalIgnoreCase).ToArray();

        var budgets = await ReadBudgetsAsync(cancellationToken);
        if (budgets.Count == 0)
        {
            // Some offers list no budgets instead of refusing the call; the foundation skips them there (budgets.tf).
            var subscription = await Arm.GetAsync(Arm.SubscriptionScope, "2022-12-01", cancellationToken);
            var quota = subscription is { } found ? ArmReader.Text(found, "subscriptionPolicies", "quotaId") ?? string.Empty : string.Empty;
            if (Governance.CostManagementUnsupportedQuotaIds.Contains(quota, StringComparer.OrdinalIgnoreCase))
            {
                throw new PlatformPrerequisiteException(
                    $"Cost Management does not support this subscription's offer (quota ID {quota}), so terraform/foundation skips the budgets (budgets.tf, output budgets.status).");
            }
        }

        var names = budgets.Select(budget => ArmReader.Text(budget, "name") ?? string.Empty).ToArray();
        var covered = budgets.SelectMany(Governance.ResourceGroupFilter).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AzurePlatform.Budgets.Except(names, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("the three §7.0 budgets must exist");
        groups.Where(group => !covered.Contains(group)).ShouldBeEmpty("every platform group must be in a budget's resource-group filter");
    }

    private async Task<IReadOnlyList<JsonElement>> ReadBudgetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Arm.ListAsync($"{Arm.SubscriptionScope}/providers/Microsoft.Consumption/budgets", ArmReader.BudgetsApiVersion, cancellationToken);
        }
        catch (PlatformApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new PlatformPrerequisiteException(
                "The conformance principal cannot read budgets at subscription scope (Microsoft.Consumption/budgets/read, for example Reader on the subscription). " + ex.Message,
                ex);
        }
        catch (PlatformApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest && Governance.IsUnsupportedOffer(ex.Message))
        {
            throw new PlatformPrerequisiteException(
                "Cost Management does not support this subscription's offer, so terraform/foundation skips the budgets (budgets.tf, output budgets.status). " + ex.Message,
                ex);
        }
    }
}

/// <summary>
/// CAP-AZ-015: clusters accept Entra ID only. <c>aks-platform-build</c>, <c>aks-platform-nonprod</c> and <c>aks-platform-prod</c>
/// have local accounts disabled and Entra ID with Azure RBAC for Kubernetes authorization.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ClusterAuthTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-015")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_GetClusterState_ThreeClusters_DisableLocalAccounts()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var findings = new List<string>();

        foreach (var tier in AzurePlatform.ClusterTiers)
        {
            var name = AzurePlatform.ClusterName(tier);
            try
            {
                var state = await ClusterStateAsync(tier, cancellationToken);
                if (state.LocalAccountsDisabled != true)
                {
                    findings.Add($"{name}: local accounts are enabled");
                }

                if (state.AzureRbacEnabled != true)
                {
                    findings.Add($"{name}: Kubernetes authorization does not use Azure RBAC");
                }
            }
            catch (global::Azure.RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
            {
                findings.Add($"{name}: not found in {AzurePlatform.ClusterGroup(tier)}");
            }
        }

        findings.ShouldBeEmpty("every cluster must accept Entra ID only");
    }
}

/// <summary>
/// CAP-AZ-016: the registry has no admin user and no anonymous pull. ARM properties of the shared registry in
/// <c>rg-platform-build</c> (named by <c>RegistryLoginServer</c>): <c>adminUserEnabled</c> and <c>anonymousPullEnabled</c> are false.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class RegistryHardeningTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-016")]
    [Category(Categories.Build)]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_GetRegistry_SharedRegistry_DisableAdminUserAndAnonymousPull()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var name = RegistryLoginServer.Split('.')[0];

        var registry = await Arm.GetAsync($"{Arm.GroupScope(AzurePlatform.BuildGroup)}/providers/Microsoft.ContainerRegistry/registries/{name}", ArmReader.RegistryApiVersion, cancellationToken);

        registry.ShouldNotBeNull($"registry {name} not found in {AzurePlatform.BuildGroup}");
        var properties = registry.GetValueOrDefault();
        (ArmReader.Text(properties, "properties", "adminUserEnabled") ?? "false").ShouldBe("false", $"{name} must have no admin user");
        (ArmReader.Text(properties, "properties", "anonymousPullEnabled") ?? "false").ShouldBe("false", $"{name} must not allow anonymous pull");
    }
}

/// <summary>
/// CAP-AZ-017: app identities reach only their own resources. Every <c>id-&lt;app&gt;-&lt;env&gt;-deploy</c> and
/// <c>id-&lt;app&gt;-&lt;env&gt;-app</c> in <c>rg-platform-&lt;tier&gt;-apps</c> holds roles only on its own vault
/// (<c>kv-&lt;app&gt;-&lt;e&gt;-&lt;hash4&gt;</c>) and its own <c>rg-app-&lt;app&gt;-&lt;tier&gt;</c>.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public partial class AppIdentityScopeTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-017")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ListRoleAssignments_AppIdentities_ScopeToOwnResources()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var identities = new Dictionary<string, (string Name, string App, string Environment)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in AzurePlatform.AppTiers)
        {
            foreach (var identity in await Arm.ListIdentitiesAsync(AzurePlatform.AppsGroup(tier), cancellationToken))
            {
                var name = ArmReader.Text(identity, "name") ?? string.Empty;
                if (AppIdentityName().Match(name) is { Success: true } match && ArmReader.Text(identity, "properties", "principalId") is { Length: > 0 } principal)
                {
                    identities[principal] = (name, match.Groups["app"].Value, match.Groups["env"].Value);
                }
            }
        }

        if (identities.Count == 0)
        {
            throw new PlatformPrerequisiteException("No app identity (id-<app>-<env>-deploy or -app) exists yet; terraform/apps/grants creates them for apps that ask.");
        }

        var assignments = await Governance.RoleAssignmentsAsync(Arm, Azure, cancellationToken);
        var violations = assignments
            .Where(assignment => identities.ContainsKey(assignment.PrincipalId))
            .Where(assignment => !OwnsScope(identities[assignment.PrincipalId].App, identities[assignment.PrincipalId].Environment, assignment.Scope))
            .Select(assignment => $"{identities[assignment.PrincipalId].Name}: role {assignment.RoleId} on {assignment.Scope}")
            .ToArray();

        violations.ShouldBeEmpty("app identities must hold roles only on their own vault and their own rg-app-<app>-<tier>");
    }

    private bool OwnsScope(string app, string environment, string scope)
    {
        var tier = AzurePlatform.TierOf(environment).ToKey();
        var vault = $"{Arm.GroupScope($"rg-platform-{tier}-apps")}/providers/Microsoft.KeyVault/vaults/{AzurePlatform.VaultName(Arm.SubscriptionId, app, environment)}";
        var group = Arm.GroupScope($"rg-app-{app}-{tier}");
        return string.Equals(scope, vault, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, group, StringComparison.OrdinalIgnoreCase)
            || scope.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^id-(?<app>[a-z][a-z0-9]{2,11})-(?<env>tdd|uat|prod)-(?:deploy|app)$")]
    private static partial Regex AppIdentityName();
}

/// <summary>Reads shared by the governance tests (CAP-AZ-012 to CAP-AZ-017).</summary>
public static partial class Governance
{
    /// <summary>The <c>rg-platform-*</c> groups of the subscription, and with <paramref name="includeAppGroups"/> the <c>rg-app-*</c> groups.</summary>
    /// <param name="azure">The harness Azure client.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="includeAppGroups"><c>true</c> to add the app groups.</param>
    public static async Task<IReadOnlyList<string>> PlatformGroupsAsync(IAzureApi azure, CancellationToken cancellationToken, bool includeAppGroups = false) =>
        (await azure.ListResourceGroupsAsync(cancellationToken))
            .Select(group => group.Name)
            .Where(name => name.StartsWith("rg-platform-", StringComparison.OrdinalIgnoreCase) || (includeAppGroups && name.StartsWith("rg-app-", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Every role assignment that applies at a platform or app group (at, above or below it), once each.</summary>
    /// <param name="arm">ARM reader.</param>
    /// <param name="azure">The harness Azure client.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public static async Task<IReadOnlyList<RoleAssignment>> RoleAssignmentsAsync(ArmReader arm, IAzureApi azure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var assignments = new Dictionary<string, RoleAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in await PlatformGroupsAsync(azure, cancellationToken, includeAppGroups: true))
        {
            foreach (var item in await arm.ListRoleAssignmentsAsync(group, cancellationToken))
            {
                var id = ArmReader.Text(item, "id") ?? string.Empty;
                assignments[id] = new RoleAssignment(
                    id,
                    ArmReader.Text(item, "properties", "principalId") ?? string.Empty,
                    (ArmReader.Text(item, "properties", "roleDefinitionId") ?? string.Empty).Split('/').Last(),
                    ArmReader.Text(item, "properties", "scope") ?? string.Empty);
            }
        }

        return assignments.Values.ToArray();
    }

    /// <summary><c>true</c> when an assignment of a tier identity stays in its tier; AcrPull on the shared registry is the one exception.</summary>
    /// <param name="assignment">The assignment.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static bool StaysInTier(RoleAssignment assignment, string tier)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var group = AzurePlatform.ResourceGroupOf(assignment.Scope);
        var scopeTier = group is null ? "subscription" : AzurePlatform.TierOfGroup(group) ?? "foreign";
        if (scopeTier == tier)
        {
            return true;
        }

        return scopeTier == "build"
            && string.Equals(assignment.RoleId, AzurePlatform.AcrPullRoleId, StringComparison.OrdinalIgnoreCase)
            && assignment.Scope.Contains("/providers/Microsoft.ContainerRegistry/registries/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>true</c> for the action group <c>Application Insights Smart Detection</c>, which Azure creates with the first App
    /// Insights component of the subscription for the rules it generates ("Failure Anomalies - &lt;component&gt;"). The
    /// platform does not declare it and cannot tag it at creation; terraform/apps/tier deletes the generated rules
    /// (<c>disable_generated_rule</c>), so the group serves no rule and costs nothing. A generated rule is not exempt: it
    /// has no tags and fails CAP-AZ-013.
    /// </summary>
    /// <param name="type">Resource type, any case.</param>
    /// <param name="name">Resource name.</param>
    public static bool IsAzureGenerated(string type, string name) =>
        string.Equals(type, "microsoft.insights/actiongroups", StringComparison.OrdinalIgnoreCase)
        && string.Equals(name, "Application Insights Smart Detection", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> for the work-volume disk of an Octopus Kubernetes worker: the kubernetes-agent chart's PVC in namespace
    /// octopus-worker-&lt;env&gt; uses AKS's default StorageClass, so AKS creates the disk in the cluster's node group with
    /// only its kubernetes.io-* tags. The chart's storage class cannot change on a running worker, and the node group is in
    /// the tier's budget (CAP-AZ-014), so its cost stays attributed. Other node-group disks are not exempt.
    /// </summary>
    /// <param name="group">Resource group.</param>
    /// <param name="type">Resource type, any case.</param>
    /// <param name="tags">Its tags.</param>
    public static bool IsWorkerVolume(string group, string type, IReadOnlyDictionary<string, string> tags) =>
        group.EndsWith("-aks-nodes", StringComparison.OrdinalIgnoreCase)
        && string.Equals(type, "Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase)
        && tags.TryGetValue("kubernetes.io-created-for-pvc-namespace", out var ns)
        && ns.StartsWith("octopus-worker-", StringComparison.Ordinal);

    /// <summary>The cost tags a resource lacks (§7.0 Azure tags).</summary>
    /// <param name="group">Its resource group.</param>
    /// <param name="name">Its name.</param>
    /// <param name="tags">Its tags.</param>
    public static IReadOnlyList<string> MissingCostTags(string group, string name, IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(tags);
        var required = new List<string> { "platform-tier" };
        if (group.StartsWith("rg-platform-", StringComparison.OrdinalIgnoreCase))
        {
            required.Add("platform-component");
        }

        if (group.StartsWith("rg-app-", StringComparison.OrdinalIgnoreCase) || tags.ContainsKey("platform-app") || PerAppName().IsMatch(name))
        {
            required.Add("platform-app");
            required.Add("platform-env");
        }

        return required.Where(tag => !tags.TryGetValue(tag, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
    }

    /// <summary>The resource groups a budget's filter names (dimension <c>ResourceGroupName</c>, directly or under <c>and</c>).</summary>
    /// <param name="budget">A <c>Microsoft.Consumption/budgets</c> object.</param>
    public static IEnumerable<string> ResourceGroupFilter(JsonElement budget)
    {
        if (!budget.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("filter", out var filter) || filter.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var clauses = new List<JsonElement> { filter };
        if (filter.TryGetProperty("and", out var and) && and.ValueKind == JsonValueKind.Array)
        {
            clauses.AddRange(and.EnumerateArray());
        }

        foreach (var clause in clauses)
        {
            if (clause.TryGetProperty("dimensions", out var dimension)
                && dimension.ValueKind == JsonValueKind.Object
                && (ArmReader.Text(dimension, "name") ?? string.Empty).StartsWith("ResourceGroup", StringComparison.OrdinalIgnoreCase)
                && dimension.TryGetProperty("values", out var values)
                && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.GetString() is { Length: > 0 } group)
                    {
                        yield return group;
                    }
                }
            }
        }
    }

    /// <summary><c>true</c> when a Cost Management error says the subscription's offer is not supported.</summary>
    /// <param name="message">Error text.</param>
    /// <summary>Quota IDs of the offers Cost Management does not support; the same list as terraform/foundation/budgets.tf.</summary>
    public static IReadOnlyList<string> CostManagementUnsupportedQuotaIds { get; } =
        ["Sponsored_2016-01-01", "AzureForStudents_2018-01-01", "DreamSpark_2015-02-01", "Default_2014-09-01"];

    public static bool IsUnsupportedOffer(string message) =>
        message.Contains("offer", StringComparison.OrdinalIgnoreCase) || message.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^(?:kv-[a-z][a-z0-9]{2,11}-[tup]-[0-9a-f]{4}|appi-[a-z][a-z0-9]{2,11}-(?:tdd|uat|prod)|slo-fast-burn-[a-z][a-z0-9]{2,11}-(?:tdd|uat|prod)|disk-[a-z][a-z0-9]{2,11}-(?:tdd|uat|prod)-db|id-[a-z][a-z0-9]{2,11}-(?:tdd|uat|prod)-(?:deploy|app))$", RegexOptions.IgnoreCase)]
    private static partial Regex PerAppName();
}

/// <summary>A role assignment.</summary>
/// <param name="Id">Assignment ID.</param>
/// <param name="PrincipalId">Object ID of the assignee.</param>
/// <param name="RoleId">Role definition GUID.</param>
/// <param name="Scope">Scope.</param>
public sealed record RoleAssignment(string Id, string PrincipalId, string RoleId, string Scope);
