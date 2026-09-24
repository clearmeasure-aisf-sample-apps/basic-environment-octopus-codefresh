using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-015: a prod release backs up the app database before the pin. Step "Back up database" (platform-db-backup)
/// creates a Job from CronJob db-backup-sandbox-prod, labelled platform/trigger=pre-release, and waits for it; only then
/// does "Update Argo CD image tags" commit the prod pin.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public partial class PreReleaseBackupTests : OctopusCapabilityTestBase
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>The backup Job completes before the pin step runs and before any prod pin commit.</summary>
    [Test]
    [Capability("CAP-OCT-015")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(3 * 60 * 60 * 1000)]
    public async Task Should_ProdDeployment_BacksUpDatabase_BeforeThePin()
    {
        Rest("the pre-release backup test", gitHub: true);
        RequireTier(PlatformTier.Prod, "the pre-release backup test");
        var repository = Settings.EnvRepo!;
        var release = await ReleaseReadyForProdAsync("Default");
        var before = await GitHub.GetBranchHeadAsync(repository, "main", Token);

        var task = await DeployAndCompleteAsync(release, "prod");

        var log = await Octopus.GetTaskLogAsync(task.Id, Token);
        var backup = BackupLine().Match(log);
        backup.Success.ShouldBeTrue("the task log has no 'backed up by Job' line of step Back up database");
        var pinStep = log.IndexOf("Update Argo CD image tags", backup.Index, StringComparison.Ordinal);
        pinStep.ShouldBeGreaterThan(backup.Index, "the pin step ran before the backup completed");
        var completedAt = DateTimeOffset.Parse(backup.Groups["at"].Value, CultureInfo.InvariantCulture);
        var comparison = await GitHub.CompareAsync(repository, before, "main", Token);
        foreach (var commit in comparison.Commits.Where(commit => commit.CommittedAt is not null))
        {
            // The log order above is the proof; the commit time (the worker's clock, whole seconds) may trail the cluster's by a little.
            commit.CommittedAt!.Value.ShouldBeGreaterThanOrEqualTo(completedAt - ClockSkew, $"pin commit {commit.Sha} precedes the backup ({completedAt:O})");
        }

        var cluster = await KubernetesAsync(PlatformTier.Prod, Token);
        var jobs = await cluster.ListCustomObjectsAsync(new CustomResourceKind("batch", "v1", "jobs"), "platform-backup", Token);
        var job = jobs.SingleOrDefault(item => Text(item, "metadata", "name") == backup.Groups["job"].Value);
        job.ValueKind.ShouldBe(JsonValueKind.Object, $"Job platform-backup/{backup.Groups["job"].Value} does not exist");
        Text(job, "metadata", "labels", "platform/trigger").ShouldBe("pre-release", $"label platform/trigger of Job platform-backup/{backup.Groups["job"].Value}");
        (job.TryGetProperty("status", out var status) && status.TryGetProperty("succeeded", out var succeeded) && succeeded.TryGetInt32(out var count) ? count : 0)
            .ShouldBeGreaterThanOrEqualTo(1, $"Job platform-backup/{backup.Groups["job"].Value} did not succeed");
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    [GeneratedRegex(@"Database of sandbox in prod backed up by Job platform-backup/(?<job>[a-z0-9-]+) at (?<at>\S+)\.")]
    private static partial Regex BackupLine();
}
