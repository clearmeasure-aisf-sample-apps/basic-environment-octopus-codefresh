using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-005, live part: AppProjects fence each app. The AppProject <c>app-&lt;app&gt;</c> of every descriptor, read from
/// the cluster, admits only the environment repository and the app's own namespaces of that cluster, no cluster-scoped
/// kind, none of the platform-owned namespaced kinds, and every Application of the app uses it. The offline part
/// (Platform.Conformance.Offline.GitOps.AppProjectFenceTests) checks the chart render and its refusals.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class AppProjectFenceTests : GitOpsTestBase
{
    private static readonly CustomResourceKind AppProjects = new("argoproj.io", "v1alpha1", "appprojects");

    [Test]
    [Capability("CAP-GIT-005")]
    [Category(Categories.NonProd)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Fence_AppProjectsOnNonprod_DenyForbiddenKindsAndDestinations()
    {
        var problems = await FenceProblemsAsync(PlatformTier.NonProd, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    [Capability("CAP-GIT-005")]
    [Category(Categories.Prod)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Fence_AppProjectsOnProd_DenyForbiddenKindsAndDestinations()
    {
        var problems = await FenceProblemsAsync(PlatformTier.Prod, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private async Task<List<string>> FenceProblemsAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var tierKey = GitOpsNames.Key(tier);
        var values = TenantValues(tier);
        var envRepoUrl = PlatformSettings.IsMissing(values.EnvRepoUrl) ? null : values.EnvRepoUrl;
        var apps = Apps();
        var cluster = await ClusterAsync(tier, cancellationToken);
        var kubernetes = await KubernetesAsync(tier, cancellationToken);
        var applications = await kubernetes.ListCustomObjectsAsync(CustomResourceKind.ArgoApplication, GitOpsNames.ArgoNamespace, cancellationToken);
        var problems = new List<string>();
        foreach (var app in apps.Where(candidate => candidate.EnvironmentsOf(tierKey).Count > 0))
        {
            var appProject = await cluster.GetAsync(AppProjects, GitOpsNames.ArgoNamespace, app.AppProject, cancellationToken);
            if (appProject is null)
            {
                problems.Add($"AppProject {app.AppProject} is missing on {cluster.ClusterName}");
                continue;
            }

            problems.AddRange(AppProjectFence.Read(appProject.Value).Violations(app, tierKey, envRepoUrl, apps.Select(other => other.Name)));
            var own = applications.Where(application => GitOpsCluster.Text(application, "metadata", "labels", "platform/app") == app.Name
                && GitOpsCluster.Text(application, "metadata", "name") != app.TenantApplication);
            problems.AddRange(own
                .Where(application => GitOpsCluster.Text(application, "spec", "project") != app.AppProject)
                .Select(application => $"Application {GitOpsCluster.Text(application, "metadata", "name")} uses project {GitOpsCluster.Text(application, "spec", "project")}, not {app.AppProject}"));
        }

        return problems;
    }
}
