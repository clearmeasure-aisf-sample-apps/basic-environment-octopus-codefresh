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
    /// <summary>A dry run at a simulated Sunday night decides to sleep, outside the working window.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    [Category(Categories.NonProd)]
    [CancelAfter(30 * 60 * 1000)]
    public async Task Should_EnvSleepDryRun_OutsideWorkingWindow_DecidesSleep()
    {
        Rest("the sleep window test");
        var sunday = NextSunday(DateTimeOffset.UtcNow).AddHours(9);

        var log = await DryRunAsync(new Dictionary<string, string>
        {
            ["Sleep.DryRun"] = "True",
            ["Sleep.NowOverride"] = sunday.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        });

        log.ShouldContain("Sleep.Decision=sleep Sleep.DryRun=true Environment=infra-nonprod");
        log.ShouldContain("outside the working window");
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

        log.ShouldContain("Sleep.Decision=stay Sleep.DryRun=true Environment=infra-nonprod Reason=busy:");
        await rest.CancelTaskAsync(deployment.TaskId, Token);
    }

    /// <summary>In the last 24 hours env-sleep stopped each app cluster at least once.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    [Category(Categories.NonProd)]
    [Category(Categories.Prod)]
    [CancelAfter(30 * 60 * 1000)]
    public async Task Should_TaskHistory_LastNight_EachClusterSlept()
    {
        Rest("the sleep history test");
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        foreach (var infra in new[] { "infra-nonprod", "infra-prod" })
        {
            var slept = false;
            foreach (var task in (await RunbookTasksAsync(infra, "env-sleep", take: 50)).Where(task => task.CompletedTime >= since && task.FinishedSuccessfully))
            {
                var log = await Octopus.GetTaskLogAsync(task.Id, Token);
                if (log.Contains($"Sleep.Decision=sleep Sleep.DryRun=false Environment={infra}", StringComparison.Ordinal))
                {
                    slept = true;
                    break;
                }
            }

            slept.ShouldBeTrue($"no env-sleep run in {infra} decided to sleep in the last 24 hours");
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

    private static DateTimeOffset NextSunday(DateTimeOffset from)
    {
        var date = from.UtcDateTime.Date.AddDays(1);
        while (date.DayOfWeek != DayOfWeek.Sunday)
        {
            date = date.AddDays(1);
        }

        return new DateTimeOffset(date, TimeSpan.Zero);
    }
}
