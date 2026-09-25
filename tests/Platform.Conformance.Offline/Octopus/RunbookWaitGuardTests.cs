using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-011, offline half: the first step of every in-cluster app runbook and of the db-runbooks starter waits up to
/// <c>Wake.WaitMinutes</c> for the app to answer (any HTTP answer), warns once while it waits, and fails naming both ways
/// to wake the cluster. The step body runs under the stub Octopus runtime of <see cref="OctopusScriptRunner"/> with a stub
/// curl that cannot connect.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class RunbookWaitGuardTests
{
    /// <summary>A cluster that does not answer within the wait fails the runbook with the guidance for its environment.</summary>
    [TestCase(".octopus/apps/sandbox/sandbox/runbooks/db-restore.ocl", "uat", "infra-nonprod", TestName = "{m}(sandbox db-restore in uat)")]
    [TestCase(".octopus/apps/workorders/workorders/runbooks/db-restore.ocl", "prod", "infra-prod", TestName = "{m}(workorders db-restore in prod)")]
    [TestCase(".octopus/apps/workorders/workorders/runbooks/run-acceptance-tests.ocl", "tdd", "infra-nonprod", TestName = "{m}(workorders run-acceptance-tests in tdd)")]
    [TestCase("octopus/templates/db-runbooks/runbooks/db-restore.ocl", "uat", "infra-nonprod", TestName = "{m}(db-runbooks starter in uat)")]
    [Capability("CAP-OCT-011")]
    public void Should_WaitGuard_ClusterAsleep_FailsWithGuidance(string runbook, string environment, string infrastructure)
    {
        using var runner = new OctopusScriptRunner().Answer("curl", string.Empty, new StubAnswer(ExitCode: 7));
        var baseUrl = $"https://app-{environment}.apps.example";
        var guidance = $"Wake it: deploy the latest platform-wake release to {environment}, or run runbook env-wake of platform-infrastructure in {infrastructure} (SRE On-call), then run this runbook again.";

        var result = runner.Run(OctopusScriptRunner.ScriptBody(runbook, "wake-environment"), Variables(environment, baseUrl, "0"));

        result.FailMessage.ShouldBe($"The cluster of {environment} did not answer at {baseUrl} within 0 minutes. {guidance}", result.ToString());
        result.Warnings.ShouldBe([$"The cluster of {environment} does not answer at {baseUrl} (asleep?). Waiting up to 0 minutes. {guidance}"]);
        result.CallsOf("curl").Single().Line.ShouldBe($"--silent --output /dev/null --max-time 10 {baseUrl}/");
    }

    /// <summary>A cluster that answers after a while lets the runbook go on: one warning, a wait of at most 30 seconds between tries.</summary>
    [Test]
    [Capability("CAP-OCT-011")]
    public void Should_WaitGuard_ClusterWakes_GoesOnAfterWaiting()
    {
        using var runner = new OctopusScriptRunner().Answer("curl", string.Empty, new StubAnswer(ExitCode: 7), new StubAnswer(ExitCode: 28), new StubAnswer());
        var baseUrl = "https://sandbox-uat.apps.example";

        var result = runner.Run(OctopusScriptRunner.ScriptBody(".octopus/apps/sandbox/sandbox/runbooks/db-restore.ocl", "wake-environment"), Variables("uat", baseUrl, "30"));

        result.Failed.ShouldBeFalse(result.ToString());
        result.Warnings.Count.ShouldBe(1);
        result.Sleeps.ShouldBe(["30", "30"]);
        result.CallsOf("curl").Count.ShouldBe(3);
        result.Log.ShouldContain($"The cluster of uat answers at {baseUrl}.");
    }

    private static Dictionary<string, string> Variables(string environment, string baseUrl, string waitMinutes) => new(StringComparer.Ordinal)
    {
        ["Octopus.Environment.Name"] = environment,
        ["App.BaseUrl"] = baseUrl,
        ["Wake.WaitMinutes"] = waitMinutes,
    };
}
