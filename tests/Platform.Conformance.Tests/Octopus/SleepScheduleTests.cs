using System.Globalization;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-009: clusters sleep outside working hours or when idle, never while a task runs. env-sleep logs one decision
/// line, <c>Sleep.Decision=&lt;sleep|stay&gt; Sleep.DryRun=&lt;true|false&gt; Environment=&lt;env&gt; Reason=&lt;text&gt;</c>; the dry
/// runs use <c>Sleep.DryRun</c> and <c>Sleep.NowOverride</c>, so nothing is stopped.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SleepScheduleTests : OctopusCapabilityTestBase
{
    /// <summary>
    /// A dry run at a simulated Monday 11:30 UTC decides to sleep because it is outside the working window: that is before
    /// 07:00 in America/Chicago in summer and in winter, but inside 07:00–19:00 UTC, so it also proves the window is read
    /// in Sleep.TimeZone. A "busy" decision (another task in the tier) is retried.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-009")]
    [Category(Categories.NonProd)]
    [CancelAfter(45 * 60 * 1000)]
    public async Task Should_EnvSleepDryRun_OutsideWorkingWindow_DecidesSleep()
    {
        Rest("the sleep window test");
        var monday = NextDay(DateTimeOffset.UtcNow, DayOfWeek.Monday).AddHours(11.5);

        var decision = await DryRunDecisionAsync(new Dictionary<string, string>
        {
            ["Sleep.DryRun"] = "True",
            ["Sleep.NowOverride"] = monday.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        });

        decision.ToString().ShouldStartWith("Sleep.Decision=sleep Sleep.DryRun=true Environment=infra-nonprod Reason=outside the working window: Mon ");
        decision.Reason.ShouldEndWith("America/Chicago", customMessage: "the window is read in Sleep.TimeZone");
    }

    /// <summary>A forced dry run while a sandbox deployment runs decides to stay awake.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(60 * 60 * 1000)]
    public async Task Should_EnvSleepDryRun_WhileTaskRuns_StaysAwake()
    {
        var rest = Rest("the busy-cluster test");
        var release = await LastDeployedReleaseAsync("tdd");
        if (release is null)
        {
            Assert.Inconclusive("no sandbox release has been deployed to tdd yet (P1-11)");
        }

        var deployment = await DeployAsync(release!, "uat");
        CancelAtTeardown(deployment.TaskId, "the busy uat deployment");

        var log = await DryRunAsync(new Dictionary<string, string> { ["Sleep.DryRun"] = "True", ["Sleep.Force"] = "True" });

        await rest.CancelTaskAsync(deployment.TaskId, Token);
        log.ShouldContain("Sleep.Decision=stay Sleep.DryRun=true Environment=infra-nonprod Reason=busy:");
    }

    /// <summary>
    /// In the last 24 hours env-sleep's own rules (not Sleep.Force) put each app cluster to sleep at least once: a real run
    /// decided to sleep outside the working window or when idle, and stopped the cluster or found it stopped.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-009")]
    [Category(Categories.NonProd)]
    [Category(Categories.Prod)]
    [CancelAfter(30 * 60 * 1000)]
    public async Task Should_TaskHistory_LastNight_EachClusterSlept()
    {
        Rest("the sleep history test");
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        foreach (var (infra, cluster) in new[] { ("infra-nonprod", "aks-platform-nonprod"), ("infra-prod", "aks-platform-prod") })
        {
            var slept = false;
            foreach (var task in (await RunbookTasksAsync(infra, "env-sleep", take: 80)).Where(task => task.CompletedTime >= since && task.FinishedSuccessfully))
            {
                var log = await Octopus.GetTaskLogAsync(task.Id, Token);
                if (SleepDecision(log) is { Decision: "sleep", DryRun: false, Forced: false } decision
                    && decision.Environment == infra
                    && (log.Contains($"Stopping {cluster}", StringComparison.Ordinal) || log.Contains($"{cluster} is already stopped", StringComparison.Ordinal)))
                {
                    slept = true;
                    break;
                }
            }

            slept.ShouldBeTrue($"no env-sleep run in {infra} put {cluster} to sleep by its own rules (outside the working window or idle) in the last 24 hours");
        }
    }

    private async Task<SleepDecisionLine> DryRunDecisionAsync(IReadOnlyDictionary<string, string> prompted)
    {
        for (var attempt = 1; ; attempt++)
        {
            var log = await DryRunAsync(prompted);
            var decision = SleepDecision(log);
            decision.ShouldNotBeNull("the env-sleep dry run logged no Sleep.Decision line");
            if (attempt >= 3 || !decision.Reason.StartsWith("busy", StringComparison.Ordinal))
            {
                return decision;
            }

            await Task.Delay(TimeSpan.FromMinutes(1), Token);
        }
    }

    private async Task<string> DryRunAsync(IReadOnlyDictionary<string, string> prompted)
    {
        var run = await Octopus.RunRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = InfrastructureProject,
                Runbook = "env-sleep",
                Environment = "infra-nonprod",
                PromptedVariables = prompted,
                Comments = $"Conformance run {Run.RunId}: env-sleep dry run",
            },
            Settings.TimeLimits.RunbookTimeout,
            Token);
        var log = await Octopus.GetTaskLogAsync(run.Task.Id, Token);
        AttachArtifact($"env-sleep-dry-run-{run.Task.Id}.log", log);
        run.Task.FinishedSuccessfully.ShouldBeTrue($"env-sleep dry run {run.Task}");
        return log;
    }

    private static DateTimeOffset NextDay(DateTimeOffset from, DayOfWeek day)
    {
        var date = from.UtcDateTime.Date.AddDays(1);
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(1);
        }

        return new DateTimeOffset(date, TimeSpan.Zero);
    }
}
