using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// Step restore-database of the app runbook db-restore under the stub Octopus runtime. The Job comes from the suspended
/// CronJob db-restore-&lt;app&gt;-&lt;env&gt;; <c>kubectl set env</c> runs only when a backup is named, because the template's
/// RESTORE_BLOB is already empty and <c>set env</c> prints nothing for a no-op change, which left create with no object.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class DbRestoreScriptTests
{
    private const string Namespace = "platform-backup";
    private const string CronJob = "db-restore-sandbox-uat";

    /// <summary>Each copy of db-restore restores the newest backup without set env and waits for the Job.</summary>
    /// <param name="runbook">A copy of db-restore.</param>
    [TestCase(".octopus/apps/sandbox/sandbox/runbooks/db-restore.ocl")]
    [TestCase(".octopus/apps/workorders/workorders/runbooks/db-restore.ocl")]
    [TestCase("octopus/templates/db-runbooks/runbooks/db-restore.ocl")]
    [Capability("CAP-OCT-011")]
    public void Should_RestoreDatabase_NoBackupNamed_CreatesTheJobWithoutSetEnv(string runbook)
    {
        var run = Restore(runbook, string.Empty).Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("^kubectl set env").ShouldBeEmpty(run.Transcript);
        run.CallsMatching($"^kubectl --namespace {Namespace} create --filename -$").Count.ShouldBe(1, run.Transcript);
    }

    /// <summary>A named backup reaches the Job through set env.</summary>
    [Test]
    [Capability("CAP-OCT-011")]
    public void Should_RestoreDatabase_BackupNamed_SetsRestoreBlob()
    {
        var run = Restore(".octopus/apps/sandbox/sandbox/runbooks/db-restore.ocl", "sandbox/2026-09-26.bak")
            .Reply("^kubectl set env --local --filename - --output yaml RESTORE_BLOB=sandbox/2026-09-26.bak$", "apiVersion: batch/v1\nkind: Job\n")
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("^kubectl set env --local --filename - --output yaml RESTORE_BLOB=sandbox/2026-09-26.bak$").Count.ShouldBe(1, run.Transcript);
        run.CallsMatching($"^kubectl --namespace {Namespace} create --filename -$").Count.ShouldBe(1, run.Transcript);
    }

    private static RunbookScript Restore(string runbook, string backup)
    {
        var script = RunbookScript.Of(runbook, "restore-database")
            .With("Octopus.Environment.Name", "uat")
            .With("Octopus.RunbookRun.Git.Ref", "refs/heads/main")
            .With("Restore.BackupName", backup)
            .Reply($"^kubectl --namespace {Namespace} get cronjob db-restore-[A-Za-z_]+-uat --output name$", $"cronjob.batch/{CronJob}")
            .Reply($"^kubectl --namespace {Namespace} create job db-restore-[A-Za-z_]+-uat-[0-9]{{12}} --from=cronjob/db-restore-[A-Za-z_]+-uat --dry-run=client --output yaml$", "apiVersion: batch/v1\nkind: Job\n")
            .Reply($"^kubectl --namespace {Namespace} create --filename -$", "job.batch/created")
            .Reply($"^kubectl --namespace {Namespace} label job .*", string.Empty)
            .Reply($"^kubectl --namespace {Namespace} get job .* --output jsonpath=\\{{\\.status\\.succeeded\\}}$", "1")
            .Reply($"^kubectl --namespace {Namespace} get job .* --output jsonpath=.*Failed.*$", string.Empty);
        return script;
    }
}
