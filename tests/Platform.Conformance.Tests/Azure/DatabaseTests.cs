using System.Net;
using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-009: scheduled backups reach Blob storage. The tenant chart's CronJob <c>db-backup-sandbox-uat</c> in
/// <c>platform-backup</c> runs nightly, or at the next wake when the cluster slept through the schedule. The test wakes
/// nonprod, waits for any catch-up run, and expects a successful Job of the CronJob within the last 26 hours that wrote
/// to container <c>sandbox-uat</c> (<c>BACKUP DATABASE … TO URL</c> fails the Job unless the blob was written). The
/// restore test (CAP-AZ-010) proves that the newest blob restores. Inconclusive while a new CronJob's first nightly run
/// is still ahead (the sandbox was onboarded after the latest scheduled time): no backup is due yet.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class BackupTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-009")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(120 * 60 * 1000)]
    public async Task Should_ReadBackupCronJob_SandboxUat_CompleteANewBackup()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var name = AzurePlatform.BackupCronJob(AzurePlatform.Sandbox, "uat");
        var window = TimeSpan.FromHours(26);
        await EnsureAwakeAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);

        var cronJob = await WaitForBackupCatchUpAsync(cluster, name, cancellationToken);
        if (!cronJob.Suspended && cronJob.FirstRunAhead(DateTimeOffset.UtcNow) is { } firstRun)
        {
            throw new PlatformPrerequisiteException($"{cronJob}: created at {cronJob.Created:u}, after the latest scheduled time; its first backup runs at {firstRun:u} or at the first wake after it. Run CAP-AZ-009 after that.");
        }

        var settled = await ObserveAsync(
            token => BackupSchedule.ReadAsync(cluster, name, token),
            observed => observed.Suspended || (observed.ActiveJobs == 0 && observed.LastSuccessfulTime > DateTimeOffset.UtcNow - window),
            TimeSpan.FromMinutes(60),
            cancellationToken);
        var jobs = await cluster.ListCustomObjectsAsync(BackupSchedule.Jobs, AzurePlatform.BackupNamespace, cancellationToken);
        var succeeded = jobs.Where(job => BackupSchedule.IsSuccessfulRunOf(job, name, settled.LastSuccessfulTime)).ToArray();

        cronJob.Suspended.ShouldBeFalse($"{name} is suspended (a frozen app); the sandbox must keep its backups");
        settled.LastSuccessfulTime.ShouldNotBeNull($"{settled}: no backup of sandbox-uat ever completed");
        settled.LastSuccessfulTime.GetValueOrDefault().ShouldBeGreaterThan(DateTimeOffset.UtcNow - window, $"{settled}: the newest backup is older than {window.TotalHours} hours");
        succeeded.ShouldNotBeEmpty($"no succeeded Job of {name} matches its last successful time ({settled})");
        settled.Environment.GetValueOrDefault("BACKUP_CONTAINER").ShouldBe("sandbox-uat", $"{name} must write to container sandbox-uat");
        settled.Environment.GetValueOrDefault("BACKUP_ACCOUNT").ShouldNotBeNullOrWhiteSpace($"{name} names no backup account");
    }
}

/// <summary>
/// CAP-AZ-010: a backup restores. The test reads the canary of <c>sandbox-uat</c>, which the newest backup holds, changes
/// it, restores the latest backup with the sandbox's runbook <c>db-restore</c> in uat (approved as conformance:&lt;run-id&gt;)
/// and expects the old value back. It runs before the rebuild (<c>[Order(1)]</c>), while the CronJob still knows its last
/// run, and first waits for any catch-up backup, so the latest backup is the one it reasons about.
/// </summary>
[TestFixture]
[Order(1)]
[Category(Categories.Live)]
public class RestoreTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-010")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(120 * 60 * 1000)]
    public async Task Should_RestoreLatestBackup_ChangedCanary_RestoreTheRow()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var name = AzurePlatform.BackupCronJob(AzurePlatform.Sandbox, "uat");
        await EnsureAwakeAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var cronJob = await WaitForBackupCatchUpAsync(cluster, name, cancellationToken);
        var sandbox = await SandboxAsync("uat", cancellationToken);

        // Right after a wake the ingress answers 404 until the app is routed, which reads as "no canary": wait for health first.
        var health = await ObserveAsync(sandbox.GetHealthAsync, status => status == HttpStatusCode.OK, TimeSpan.FromMinutes(15), cancellationToken);
        health.ShouldBe(HttpStatusCode.OK, $"{sandbox.BaseUri}healthz did not answer 200 after the wake");
        var original = await ReadCanaryAsync(sandbox, TimeSpan.FromMinutes(15), cancellationToken);
        if (original is null)
        {
            await sandbox.PutCanaryAsync($"seed-{Run.RunId}", cancellationToken);
            throw new PlatformPrerequisiteException($"{sandbox.BaseUri} had no canary; one was written now, and the next backup of {name} makes it restorable.");
        }

        if (cronJob.LastSuccessfulTime is not { } backedUp || original.UpdatedAtUtc > backedUp)
        {
            throw new PlatformPrerequisiteException(
                $"The canary of sandbox-uat changed at {original.UpdatedAtUtc:u}, after the newest backup ({cronJob}); the next backup makes the test meaningful.");
        }

        var changed = $"restore-{Run.RunId}";
        await sandbox.PutCanaryAsync(changed, cancellationToken);
        var beforeRestore = await ReadCanaryAsync(sandbox, TimeSpan.FromMinutes(5), cancellationToken);

        var restore = await RunRunbookAsync(AzurePlatform.Sandbox, "db-restore", "uat", null, Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.RunbookTimeout, approve: true, cancellationToken);
        var restored = await ObserveAsync(sandbox.GetCanaryAsync, observed => observed?.Value == original.Value, TimeSpan.FromMinutes(15), cancellationToken);

        beforeRestore?.Value.ShouldBe(changed, "the canary did not change before the restore, so the restore proves nothing");
        restore.Task.FinishedSuccessfully.ShouldBeTrue($"{restore}: {restore.Task.ErrorMessage}");
        restored.ShouldNotBeNull($"{sandbox.BaseUri}data/canary has no row after the restore");
        restored.Value.ShouldBe(original.Value, $"the restore of the latest backup ({cronJob.LastSuccessfulTime:u}) did not bring back the canary");
    }
}

/// <summary>
/// CAP-AZ-011: database passwords rotate without breaking the app. The test writes a canary to <c>sandbox-tdd</c>, runs
/// <c>rotate-db-passwords</c> for the sandbox in infra-nonprod (its three logins, sa last, in tdd and uat), then expects both environments
/// healthy and their data readable with the new passwords (ESO refresh and restart are part of the runbook).
/// </summary>
[TestFixture]
[Order(2)]
[Category(Categories.Live)]
public class PasswordRotationTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-011")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(90 * 60 * 1000)]
    public async Task Should_RunRotateDbPasswords_Sandbox_KeepTheAppHealthy()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var expected = $"rotation-{Run.RunId}";
        await EnsureAwakeAsync(PlatformTier.NonProd, cancellationToken);
        var tdd = await SandboxAsync("tdd", cancellationToken);
        var uat = await SandboxAsync("uat", cancellationToken);
        await ObserveAsync(async token =>
        {
            await tdd.PutCanaryAsync(expected, token);
            return true;
        }, _ => true, TimeSpan.FromMinutes(10), cancellationToken);

        var rotation = await RunInfrastructureRunbookAsync(
            "rotate-db-passwords",
            PlatformTier.NonProd,
            new Dictionary<string, string> { ["App.Name"] = AzurePlatform.Sandbox },
            Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.WakeTimeout,
            approve: false,
            cancellationToken);
        var canary = await ObserveAsync(tdd.GetCanaryAsync, observed => observed?.Value == expected, TimeSpan.FromMinutes(15), cancellationToken);
        var health = await ObserveAsync(tdd.GetHealthAsync, status => status == HttpStatusCode.OK, TimeSpan.FromMinutes(5), cancellationToken);
        var uatReadable = await ObserveAsync(async token =>
        {
            await uat.GetCanaryAsync(token);
            return true;
        }, _ => true, TimeSpan.FromMinutes(15), cancellationToken);

        rotation.Task.FinishedSuccessfully.ShouldBeTrue($"{rotation}");
        canary.ShouldNotBeNull($"{tdd.BaseUri}data/canary has no row after the rotation");
        canary.Value.ShouldBe(expected, $"sandbox-tdd lost or could not read its data after the rotation ({tdd.BaseUri})");
        health.ShouldBe(HttpStatusCode.OK, $"{tdd.BaseUri}healthz after the rotation");
        uatReadable.ShouldBeTrue($"{uat.BaseUri}data/canary must answer after the rotation");
    }
}

/// <summary>Reads of the backup CronJobs and their Jobs in <c>platform-backup</c>.</summary>
public static class BackupSchedule
{
    /// <summary><c>batch/v1</c> CronJobs.</summary>
    public static CustomResourceKind CronJobs { get; } = new("batch", "v1", "cronjobs");

    /// <summary><c>batch/v1</c> Jobs.</summary>
    public static CustomResourceKind Jobs { get; } = new("batch", "v1", "jobs");

    /// <summary>Reads a backup CronJob; fails the test when it does not exist.</summary>
    /// <param name="cluster">The nonprod cluster.</param>
    /// <param name="name">CronJob name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<BackupCronJob> ReadAsync(IKubernetesApi cluster, string name, CancellationToken cancellationToken)
    {
        var cronJob = await cluster.GetCustomObjectAsync(CronJobs, AzurePlatform.BackupNamespace, name, cancellationToken);
        cronJob.ShouldNotBeNull($"CronJob {AzurePlatform.BackupNamespace}/{name} does not exist; the tenant chart renders it for uat when apps/sandbox.yaml declares a database");
        return BackupCronJob.From(cronJob.GetValueOrDefault());
    }

    /// <summary><c>true</c> for a succeeded Job owned by the CronJob that completed at its last successful time (within a minute).</summary>
    /// <param name="job">A Job object.</param>
    /// <param name="cronJob">CronJob name.</param>
    /// <param name="lastSuccessfulTime">The CronJob's last successful time.</param>
    public static bool IsSuccessfulRunOf(JsonElement job, string cronJob, DateTimeOffset? lastSuccessfulTime)
    {
        var owned = job.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("ownerReferences", out var owners)
            && owners.ValueKind == JsonValueKind.Array
            && owners.EnumerateArray().Any(owner => ArmReader.Text(owner, "kind") == "CronJob" && ArmReader.Text(owner, "name") == cronJob);
        var succeeded = int.TryParse(ArmReader.Text(job, "status", "succeeded"), out var count) && count > 0;
        var completed = DateTimeOffset.TryParse(ArmReader.Text(job, "status", "completionTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
            && lastSuccessfulTime is { } expected
            && (time - expected).Duration() <= TimeSpan.FromMinutes(1);
        return owned && succeeded && completed;
    }
}
