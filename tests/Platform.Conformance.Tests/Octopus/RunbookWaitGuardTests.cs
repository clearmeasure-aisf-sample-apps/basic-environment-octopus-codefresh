using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-011: app runbooks wait for a sleeping cluster and fail with guidance. An app runbook holds no platform key, so
/// its first step waits up to <c>Wake.WaitMinutes</c> for the app to answer and then names both ways to wake the cluster.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class RunbookWaitGuardTests : OctopusCapabilityTestBase
{
    /// <summary>db-restore of sandbox in uat, with nonprod asleep and Wake.WaitMinutes=1, fails and says how to wake it.</summary>
    [Test]
    [Capability("CAP-OCT-011")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public async Task Should_DbRestore_ClusterAsleepWithShortWait_FailsWithGuidance()
    {
        Rest("the runbook wait guard test");
        await ForceSleepAsync(PlatformTier.NonProd);

        var run = await Octopus.RunRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = SandboxProject,
                Runbook = "db-restore",
                Environment = "uat",
                PromptedVariables = new Dictionary<string, string> { ["Wake.WaitMinutes"] = "1" },
                Comments = $"Conformance run {Run.RunId}: wait guard",
            },
            Settings.TimeLimits.RunbookTimeout,
            Token);

        run.Task.FinishedSuccessfully.ShouldBeFalse($"db-restore ran against a sleeping cluster: {run.Task}");
        var log = await Octopus.GetTaskLogAsync(run.Task.Id, Token);
        AttachArtifact($"db-restore-wait-guard-{run.Task.Id}.log", log);
        log.ShouldContain("did not answer");
        log.ShouldContain("within 1 minutes");
        log.ShouldContain("deploy the latest platform-wake release to uat, or run runbook env-wake of platform-infrastructure in infra-nonprod");
    }
}
