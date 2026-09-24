using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-002, live part: every descriptor <c>apps/*.yaml</c> yields its tenant on each cluster (§7.0 Tenant): Application
/// <c>tenant-&lt;app&gt;</c> Synced and Healthy, AppProject <c>app-&lt;app&gt;</c>, labelled namespaces, quota and limit
/// range, the platform NetworkPolicies, ClusterSecretStores <c>&lt;app&gt;-&lt;env&gt;</c> Ready, the Applications, the
/// per-app image policy, the database volume and backups, and the ListenerSets once the apps domain is provisioned.
/// The offline part (Platform.Conformance.Offline.GitOps.TenantTests) checks the chart render itself.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class TenantTests : GitOpsTestBase
{
    private static readonly CustomResourceKind AppProjects = new("argoproj.io", "v1alpha1", "appprojects");
    private static readonly CustomResourceKind ClusterSecretStores = new("external-secrets.io", "v1", "clustersecretstores");
    private static readonly CustomResourceKind ListenerSets = new("gateway.networking.k8s.io", "v1", "listenersets");

    [Test]
    [Capability("CAP-GIT-002")]
    [Category(Categories.NonProd)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Tenant_EveryDescriptorOnNonprod_HasItsObjects()
    {
        var problems = await TenantProblemsAsync(PlatformTier.NonProd, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    [Capability("CAP-GIT-002")]
    [Category(Categories.Prod)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Tenant_EveryDescriptorOnProd_HasItsObjects()
    {
        var problems = await TenantProblemsAsync(PlatformTier.Prod, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private async Task<List<string>> TenantProblemsAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var tierKey = GitOpsNames.Key(tier);
        var values = TenantValues(tier);
        var kubernetes = await KubernetesAsync(tier, cancellationToken);
        var cluster = await ClusterAsync(tier, cancellationToken);
        var namespaces = (await kubernetes.ListNamespacesAsync(cancellationToken: cancellationToken)).ToDictionary(item => item.Name, StringComparer.Ordinal);
        var policies = await kubernetes.ListKyvernoPoliciesAsync(cancellationToken);
        var problems = new List<string>();
        foreach (var app in Apps())
        {
            var environments = app.EnvironmentsOf(tierKey);
            if (environments.Count == 0)
            {
                continue;
            }

            await CheckApplicationAsync(kubernetes, app.TenantApplication, requireHealthy: true, problems, cancellationToken);
            if (await cluster.GetAsync(AppProjects, GitOpsNames.ArgoNamespace, app.AppProject, cancellationToken) is null)
            {
                problems.Add($"AppProject {app.AppProject} is missing");
            }

            if (!policies.Any(policy => policy.Kind == "ImageValidatingPolicy" && policy.Name == app.SignerPolicy))
            {
                problems.Add($"ImageValidatingPolicy {app.SignerPolicy} is missing");
            }

            foreach (var environment in environments)
            {
                await CheckEnvironmentAsync(app, environment, values, namespaces, kubernetes, cluster, problems, cancellationToken);
            }
        }

        return problems;
    }

    private static async Task CheckEnvironmentAsync(
        GitOpsApp app,
        string environment,
        TenantPlatformValues values,
        Dictionary<string, KubernetesNamespace> namespaces,
        IKubernetesApi kubernetes,
        GitOpsCluster cluster,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        foreach (var namespaceName in app.Namespaces(environment))
        {
            if (!namespaces.TryGetValue(namespaceName, out var found))
            {
                problems.Add($"namespace {namespaceName} is missing");
                continue;
            }

            ExpectLabel(found, "platform/app", app.Name, problems);
            ExpectLabel(found, "environment", environment, problems);
            ExpectLabel(found, "tier", "app", problems);
            ExpectLabel(found, "pod-security.kubernetes.io/enforce", "restricted", problems);
            if (!(await kubernetes.ListResourceQuotasAsync(namespaceName, cancellationToken)).Any(quota => quota.Name == "tenant"))
            {
                problems.Add($"ResourceQuota {namespaceName}/tenant is missing");
            }

            if (await cluster.LimitRangeAsync(namespaceName, "tenant", cancellationToken) is null)
            {
                problems.Add($"LimitRange {namespaceName}/tenant is missing");
            }

            var expectedPolicies = GitOpsNames.NetworkPolicies.ToList();
            if (app.HasDatabase && namespaceName == app.Namespace(environment))
            {
                expectedPolicies.Add(GitOpsNames.DatabaseNetworkPolicy);
            }

            var policies = (await kubernetes.ListNetworkPoliciesAsync(namespaceName, cancellationToken)).Select(policy => policy.Name).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(expectedPolicies.Where(policy => !policies.Contains(policy)).Select(policy => $"NetworkPolicy {namespaceName}/{policy} is missing"));
            if (values.AppsDomain is not null && await cluster.GetAsync(ListenerSets, GitOpsNames.IngressNamespace, namespaceName, cancellationToken) is null)
            {
                problems.Add($"ListenerSet {GitOpsNames.IngressNamespace}/{namespaceName} is missing");
            }
        }

        var store = await cluster.GetAsync(ClusterSecretStores, null, app.Store(environment), cancellationToken);
        if (store is null)
        {
            problems.Add($"ClusterSecretStore {app.Store(environment)} is missing");
        }
        else if (GitOpsCluster.ReadyCondition(store.Value) is { Ready: not true } ready)
        {
            problems.Add($"ClusterSecretStore {app.Store(environment)} is not Ready: {ready.Message}");
        }

        foreach (var deployable in app.Deployables)
        {
            await CheckApplicationAsync(kubernetes, app.Application(deployable, environment), requireHealthy: false, problems, cancellationToken);
        }

        if (!app.HasDatabase)
        {
            return;
        }

        await CheckApplicationAsync(kubernetes, app.DatabaseApplication(environment), requireHealthy: false, problems, cancellationToken);
        if (await cluster.PersistentVolumeAsync(app.Disk(environment), cancellationToken) is null)
        {
            problems.Add($"PersistentVolume {app.Disk(environment)} is missing");
        }

        if (GitOpsNames.BackupEnvironments.Contains(environment, StringComparer.Ordinal))
        {
            foreach (var cronJob in new[] { $"db-backup-{app.Name}-{environment}", $"db-restore-{app.Name}-{environment}" })
            {
                if (await cluster.CronJobAsync(GitOpsNames.BackupNamespace, cronJob, cancellationToken) is null)
                {
                    problems.Add($"CronJob {GitOpsNames.BackupNamespace}/{cronJob} is missing");
                }
            }
        }
    }

    private static async Task CheckApplicationAsync(IKubernetesApi kubernetes, string name, bool requireHealthy, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            var application = await kubernetes.GetArgoApplicationAsync(name, cancellationToken: cancellationToken);
            if (requireHealthy && !application.IsSyncedAndHealthy)
            {
                problems.Add($"Application {name} is {application.SyncStatus}/{application.HealthStatus}");
            }
        }
        catch (PlatformApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            problems.Add($"Application {name} is missing");
        }
    }

    private static void ExpectLabel(KubernetesNamespace found, string key, string expected, List<string> problems)
    {
        if (!found.Labels.TryGetValue(key, out var actual) || actual != expected)
        {
            problems.Add($"namespace {found.Name}: label {key} is '{actual}', expected '{expected}'");
        }
    }
}
