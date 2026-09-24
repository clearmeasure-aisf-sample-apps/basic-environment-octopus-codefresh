using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-008: the database runs before the app's first pin. The tenant chart gives <c>&lt;app&gt;-db-&lt;env&gt;</c> sync
/// wave 0 and the workload Applications wave 5, and the Argo CD configuration's Application health check makes
/// <c>tenant-&lt;app&gt;</c> wait for the database Application to be Healthy before it creates the workloads. Observed on
/// each cluster, for every app with a database: the health check is configured, the waves are in that order, the
/// database Application is Healthy, it was created before every workload Application, and its first recorded sync
/// precedes theirs.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class DatabaseOrderingTests : GitOpsTestBase
{
    private const string WaveAnnotation = "argocd.argoproj.io/sync-wave";
    private const string HealthCheckKey = "resource.customizations.health.argoproj.io_Application";

    [Test]
    [Capability("CAP-GIT-008")]
    [Category(Categories.NonProd)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Order_DatabaseApplicationsOnNonprod_ComeBeforeTheirWorkloads()
    {
        var problems = await OrderingProblemsAsync(PlatformTier.NonProd, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    [Capability("CAP-GIT-008")]
    [Category(Categories.Prod)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Order_DatabaseApplicationsOnProd_ComeBeforeTheirWorkloads()
    {
        var problems = await OrderingProblemsAsync(PlatformTier.Prod, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private async Task<List<string>> OrderingProblemsAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var tierKey = GitOpsNames.Key(tier);
        var cluster = await ClusterAsync(tier, cancellationToken);
        var problems = new List<string>();
        var configuration = await cluster.ConfigMapAsync(GitOpsNames.ArgoNamespace, "argocd-cm", cancellationToken);
        if (configuration?.Data is null || !configuration.Data.ContainsKey(HealthCheckKey))
        {
            problems.Add($"argocd-cm on {cluster.ClusterName} lacks {HealthCheckKey}: sync waves would not wait for the database");
        }

        var apps = Apps().Where(app => app.HasDatabase && app.EnvironmentsOf(tierKey).Count > 0).ToArray();
        if (apps.Length == 0)
        {
            Unobservable($"no app with a database runs on {tierKey}");
        }

        foreach (var app in apps)
        {
            foreach (var environment in app.EnvironmentsOf(tierKey))
            {
                var database = await cluster.GetAsync(CustomResourceKind.ArgoApplication, GitOpsNames.ArgoNamespace, app.DatabaseApplication(environment), cancellationToken);
                if (database is null)
                {
                    problems.Add($"Application {app.DatabaseApplication(environment)} is missing");
                    continue;
                }

                if (GitOpsCluster.Text(database.Value, "status", "health", "status") != "Healthy")
                {
                    problems.Add($"Application {app.DatabaseApplication(environment)} is {GitOpsCluster.Text(database.Value, "status", "health", "status")}, not Healthy");
                }

                foreach (var deployable in app.Deployables)
                {
                    var name = app.Application(deployable, environment);
                    var workload = await cluster.GetAsync(CustomResourceKind.ArgoApplication, GitOpsNames.ArgoNamespace, name, cancellationToken);
                    if (workload is null)
                    {
                        problems.Add($"Application {name} is missing");
                        continue;
                    }

                    problems.AddRange(Compare(app, database.Value, workload.Value));
                }
            }
        }

        return problems;
    }

    private static IEnumerable<string> Compare(GitOpsApp app, JsonElement database, JsonElement workload)
    {
        var databaseName = GitOpsCluster.Text(database, "metadata", "name");
        var workloadName = GitOpsCluster.Text(workload, "metadata", "name");
        var databaseWave = int.TryParse(GitOpsCluster.Text(database, "metadata", "annotations", WaveAnnotation), out var dbWave) ? dbWave : 0;
        var workloadWave = int.TryParse(GitOpsCluster.Text(workload, "metadata", "annotations", WaveAnnotation), out var appWave) ? appWave : 0;
        if (!app.IsFrozen && databaseWave >= workloadWave)
        {
            yield return $"{databaseName} (wave {databaseWave}) does not precede {workloadName} (wave {workloadWave})";
        }

        var databaseCreated = GitOpsCluster.Timestamp(GitOpsCluster.Text(database, "metadata", "creationTimestamp"));
        var workloadCreated = GitOpsCluster.Timestamp(GitOpsCluster.Text(workload, "metadata", "creationTimestamp"));
        if (databaseCreated is not null && workloadCreated is not null && databaseCreated > workloadCreated)
        {
            yield return $"{databaseName} was created at {databaseCreated:u}, after {workloadName} ({workloadCreated:u})";
        }

        var databaseFirstSync = FirstDeployment(database);
        var workloadFirstSync = FirstDeployment(workload);
        if (databaseFirstSync is null && workloadFirstSync is not null)
        {
            yield return $"{workloadName} has synced ({workloadFirstSync:u}) but {databaseName} never has";
        }
        else if (databaseFirstSync is not null && workloadFirstSync is not null && HistoryIsComplete(workload) && databaseFirstSync > workloadFirstSync)
        {
            yield return $"{workloadName} first synced at {workloadFirstSync:u}, before {databaseName} ({databaseFirstSync:u})";
        }
    }

    private static DateTimeOffset? FirstDeployment(JsonElement application)
    {
        var history = GitOpsCluster.Child(application, "status", "history");
        return history.ValueKind == JsonValueKind.Array
            ? history.EnumerateArray().Select(entry => GitOpsCluster.Timestamp(GitOpsCluster.Text(entry, "deployedAt"))).Where(time => time is not null).Min()
            : null;
    }

    private static bool HistoryIsComplete(JsonElement application)
    {
        var history = GitOpsCluster.Child(application, "status", "history");
        var limit = GitOpsCluster.Child(application, "spec", "revisionHistoryLimit");
        var kept = limit.ValueKind == JsonValueKind.Number ? limit.GetInt32() : 10;
        return history.ValueKind == JsonValueKind.Array && history.GetArrayLength() < kept;
    }
}
