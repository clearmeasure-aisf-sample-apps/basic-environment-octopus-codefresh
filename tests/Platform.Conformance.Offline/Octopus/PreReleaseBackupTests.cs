using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-015, offline half: every app process with a pre-release backup inlines octopus/step-templates/db-backup.ps1
/// verbatim, and runs the backup, in prod, before the step that writes the pins; the backup script starts a labelled
/// Job from the backup CronJob, waits for it and stops the release when the Job fails or does not finish (stub
/// kubectl of <see cref="OctopusScriptRunner"/>).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class PreReleaseBackupTests
{
    private const string Process = ".octopus/apps/sandbox/sandbox/deployment_process.ocl";

    /// <summary>Each inline copy of db-backup.ps1 equals the step-template script.</summary>
    [Test]
    [Capability("CAP-OCT-015")]
    public void Should_InlineDbBackup_EveryAppProcess_EqualsTheTemplateScript()
    {
        var canonical = OctopusRepository.CanonicalLines("db-backup");
        var copies = OctopusRepository.AppProcesses()
            .SelectMany(file => OctopusRepository.InlineCopies(OctopusRepository.Read(file), "db-backup").Select(copy => (file, copy)))
            .ToArray();

        copies.ShouldNotBeEmpty("no app process inlines db-backup.ps1");
        foreach (var (file, copy) in copies)
        {
            copy.ShouldBe(canonical, $"{file}: the inline copy of db-backup.ps1 differs from octopus/step-templates/db-backup.ps1");
        }
    }

    /// <summary>In every process with step pre-release-backup, that step is scoped to prod and precedes the pin step.</summary>
    [Test]
    [Capability("CAP-OCT-015")]
    public void Should_PreReleaseBackup_EveryAppProcess_RunsInProdBeforeThePinStep()
    {
        var processes = OctopusRepository.AppProcesses()
            .Select(file => (file, steps: OctopusRepository.Steps(OctopusRepository.Read(file))))
            .Where(process => process.steps.Any(step => step.Slug == "pre-release-backup"))
            .ToArray();

        processes.ShouldNotBeEmpty("no app process has step pre-release-backup");
        foreach (var (file, steps) in processes)
        {
            var slugs = steps.Select(step => step.Slug).ToList();
            var backup = slugs.IndexOf("pre-release-backup");
            var pin = slugs.IndexOf("update-argo-cd-image-tags");
            pin.ShouldBeGreaterThan(backup, $"{file}: the pin step must follow pre-release-backup");
            steps[backup].Text.ShouldContain("environments = [\"prod\"]", Case.Sensitive, $"{file}: pre-release-backup runs in prod only");
        }
    }

    /// <summary>
    /// The inline backup of the sandbox process in prod: a Job from CronJob db-backup-sandbox-prod, labelled
    /// platform/trigger=pre-release and with the release, polled until it succeeds; the output variables and the log line
    /// that the live test reads.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-015")]
    public void Should_DbBackup_JobSucceeds_LabelsTheJobAndSetsTheOutputs()
    {
        using var runner = BackupKubectl(new OctopusScriptRunner(), succeeded: ["", "1"], failed: [""]);

        var result = runner.Run(OctopusScriptRunner.ScriptBody(Process, "pre-release-backup"), Variables());

        result.Failed.ShouldBeFalse(result.ToString());
        var kubectl = result.CallsOf("kubectl").Select(call => call.Line).ToArray();
        kubectl[0].ShouldBe("--namespace platform-backup get cronjob db-backup-sandbox-prod --output name");
        var job = JobName().Match(kubectl[1]);
        job.Success.ShouldBeTrue(kubectl[1]);
        kubectl[1].ShouldBe($"--namespace platform-backup create job {job.Value} --from=cronjob/db-backup-sandbox-prod");
        kubectl[2].ShouldBe($"--namespace platform-backup label job {job.Value} platform/trigger=pre-release platform/release=0.1.5-hotfix.1 --overwrite");
        kubectl.Count(line => line.Contains("status.succeeded", StringComparison.Ordinal)).ShouldBe(2);
        result.Sleeps.ShouldBe(["10"]);
        result.Outputs["DbBackup.JobName"].ShouldBe(job.Value);
        result.Outputs["DbBackup.CompletedAt"].ShouldBe("2026-09-25T02:10:00Z");
        result.Highlights.ShouldBe([$"Database of sandbox in prod backed up by Job platform-backup/{job.Value} at 2026-09-25T02:10:00Z."]);
    }

    /// <summary>A failed Job and a Job that does not finish in time show the Job's log and stop the release before the pin.</summary>
    [TestCase("True", "1800", "failed; the release does not proceed to the pin.", TestName = "{m}(Job failed)")]
    [TestCase("", "0", "did not complete within 0 seconds.", TestName = "{m}(timeout)")]
    [Capability("CAP-OCT-015")]
    public void When_DbBackup_JobFailsOrTimesOut_ShowsTheLogAndStopsTheRelease(string failed, string timeoutSeconds, string ending)
    {
        using var runner = BackupKubectl(new OctopusScriptRunner(), succeeded: [""], failed: [failed]);
        var variables = Variables();
        variables["DbBackup.App"] = "sandbox";
        variables["DbBackup.Environment"] = "prod";
        variables["DbBackup.TimeoutSeconds"] = timeoutSeconds;

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/db-backup.ps1"), variables);

        result.FailMessage.ShouldNotBeNull(result.ToString());
        result.FailMessage.ShouldStartWith("Backup Job platform-backup/db-backup-sandbox-prod-pre-", Case.Sensitive);
        result.FailMessage.ShouldEndWith(ending, Case.Sensitive);
        result.CallsOf("kubectl").ShouldContain(call => call.Line.StartsWith("--namespace platform-backup logs job/db-backup-sandbox-prod-pre-", StringComparison.Ordinal) && call.Line.EndsWith(" --tail=100", StringComparison.Ordinal));
        result.Outputs.ShouldBeEmpty();
        result.Log.ShouldContain("backup: sqlpackage failed");
    }

    /// <summary>tdd (disposable databases) and a missing backup CronJob stop the step before any Job exists.</summary>
    [TestCase("tdd", 0, "tdd has no backup CronJob: tdd databases are disposable (ADR-IR34).", TestName = "{m}(tdd)")]
    [TestCase("prod", 1, "CronJob platform-backup/db-backup-sandbox-prod was not found. The tenant chart renders it for uat and prod when the descriptor declares a database.", TestName = "{m}(CronJob missing)")]
    [Capability("CAP-OCT-015")]
    public void When_DbBackup_TddOrNoCronJob_FailsWithoutAJob(string environment, int cronJobExitCode, string message)
    {
        using var runner = new OctopusScriptRunner()
            .Answer("kubectl", "get cronjob", new StubAnswer(ExitCode: cronJobExitCode, Output: cronJobExitCode == 0 ? "cronjob.batch/db-backup-sandbox-prod\n" : string.Empty));
        var variables = Variables();
        variables["Octopus.Environment.Name"] = environment;

        var result = runner.Run(OctopusScriptRunner.ScriptBody(Process, "pre-release-backup"), variables);

        result.FailMessage.ShouldBe(message, result.ToString());
        result.CallsOf("kubectl").ShouldNotContain(call => call.Line.Contains(" create ", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["Octopus.Environment.Name"] = "prod",
        ["Octopus.Release.Number"] = "0.1.5-hotfix.1",
    };

    private static OctopusScriptRunner BackupKubectl(OctopusScriptRunner runner, string[] succeeded, string[] failed) => runner
        .Answer("kubectl", "get cronjob", new StubAnswer("cronjob.batch/db-backup-sandbox-prod\n"))
        .Answer("kubectl", "create job", new StubAnswer("job.batch/created\n"))
        .Answer("kubectl", "label job", new StubAnswer("job.batch/labeled\n"))
        .Answer("kubectl", "status.succeeded", succeeded.Select(value => new StubAnswer(value)).ToArray())
        .Answer("kubectl", "Failed", failed.Select(value => new StubAnswer(value)).ToArray())
        .Answer("kubectl", "completionTime", new StubAnswer("2026-09-25T02:10:00Z"))
        .Answer("kubectl", " logs ", new StubAnswer("backup: sqlpackage failed\n"));

    [GeneratedRegex(@"db-backup-sandbox-prod-pre-[0-9]{12}")]
    private static partial Regex JobName();
}
