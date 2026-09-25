using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// env-sleep's scripts under the stub Octopus runtime. CAP-OCT-009 (offline half): step Decide sleep applies the decision
/// table in order (Sleep.Enabled, a queued or executing task, Sleep.Force, the working window, idle time; Sleep.DryRun and
/// Sleep.NowOverride simulate) and logs one parsable decision line. CAP-OCT-010 (offline half): step Stop cluster mutes
/// the alerts, reads the task list again and stops the cluster, and unmutes them on any exit before the stop is accepted.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class EnvSleepScriptTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/env-sleep.ocl";
    private const string Busy = "Queued,Executing,Cancelling";
    private const string Done = "Success,Failed,Canceled,TimedOut";
    private const string Monday1600 = "2026-09-28T16:00:00Z";
    private const string AksState = @"^az aks show --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --query \[powerState\.code, provisioningState\] --output tsv$";
    private const string RuleShow = "^az monitor alert-processing-rule show --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --output none$";
    private const string RuleOn = "^az monitor alert-processing-rule update --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --enabled true --output none --only-show-errors$";
    private const string RuleOff = "^az monitor alert-processing-rule update --resource-group rg-platform-nonprod-aks --name apr-sleep-nonprod --enabled false --output none --only-show-errors$";
    private const string AksStop = "^az aks stop --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --no-wait$";
    private const string Reason = "outside the working window: Sat 0100 America/Chicago";

    private static readonly Dictionary<string, Action<RunbookScript>> Arrangements = new(StringComparer.Ordinal);

    /// <summary>The decision table, each row a scenario with its decision, reason and warnings.</summary>
    public static IEnumerable<TestCaseData> Decisions()
    {
        yield return Row("disabled, force ignored", "stay", "Sleep.Enabled is 'false'", ["Sleep.Force is ignored: Sleep.Enabled is 'false' in infra-nonprod."],
            script => script.With("Sleep.Enabled", "False").With("Sleep.Force", "true"));
        yield return Row("busy wins over force", "stay", "busy: ServerTasks-7 (Executing: Deploy release 1.4.0 to tdd)", [],
            script => script.With("Sleep.Force", "True").TaskList("tdd", Busy, 100,
                OctopusReplies.Task("ServerTasks-900", "Executing", "Run env-sleep in infra-nonprod"),
                OctopusReplies.Task("ServerTasks-7", "Executing", "Deploy release 1.4.0 to tdd", OctopusReplies.MinutesFromNow(-3))));
        yield return Row("queued within 15 minutes is busy", "stay", "busy: ServerTasks-8 (Queued: Deploy release 1.4.0 to uat)", [],
            script => script.TaskList("uat", Busy, 100, OctopusReplies.Task("ServerTasks-8", "Queued", "Deploy release 1.4.0 to uat", OctopusReplies.MinutesFromNow(10))));
        yield return Row("queued later is not busy, forced by user name", "sleep", "forced by jane", ["Sleep.Force set by jane: working-window and idle checks skipped."],
            script => script.With("Sleep.Force", "true").With("Octopus.Deployment.CreatedBy.Username", "jane").With("Octopus.Deployment.CreatedBy.DisplayName", "Jane Doe")
                .TaskList("tdd", Busy, 100, OctopusReplies.Task("ServerTasks-9", "Queued", "Scheduled deployment", OctopusReplies.MinutesFromNow(60))));
        yield return Row("forced by display name", "sleep", "forced by Jane Doe", ["Sleep.Force set by Jane Doe: working-window and idle checks skipped."],
            script => script.With("Sleep.Force", "true").With("Octopus.Deployment.CreatedBy.DisplayName", "Jane Doe"));
        yield return Row("forced by nobody known", "sleep", "forced by an unidentified user", ["Sleep.Force set by an unidentified user: working-window and idle checks skipped."],
            script => script.With("Sleep.Force", "true"));
        yield return Row("outside the window in Sleep.TimeZone", "sleep", "outside the working window: Mon 0630 America/Chicago", ["Dry run with the simulated time 2026-09-28T11:30:00Z."],
            script => script.With("Sleep.DryRun", "True").With("Sleep.NowOverride", "2026-09-28T11:30:00Z"));
        yield return Row("weekend", "sleep", "outside the working window: Sat 1200 America/Chicago", ["Dry run with the simulated time 2026-09-26T17:00:00Z."],
            script => script.With("Sleep.DryRun", "true").With("Sleep.NowOverride", "2026-09-26T17:00:00Z"));
        yield return Row("active inside the window", "stay", "active 60 minutes ago, limit 120; last: ServerTasks-50 (Success: Deploy release 1.4.0 to tdd)", [$"Dry run with the simulated time {Monday1600}."],
            script => script.With("Sleep.DryRun", "true").With("Sleep.NowOverride", Monday1600)
                .TaskList("tdd", Done, 50, OctopusReplies.Task("ServerTasks-50", "Success", "Deploy release 1.4.0 to tdd", completed: "2026-09-28T15:00:00.000+00:00")));
        yield return Row("idle inside the window", "sleep", "idle for 121 minutes, limit 120; last: ServerTasks-51 (Failed: Run env-wake in infra-nonprod)", [$"Dry run with the simulated time {Monday1600}."],
            script => script.With("Sleep.DryRun", "true").With("Sleep.NowOverride", Monday1600)
                .TaskList("infra-nonprod", Done, 50,
                    OctopusReplies.Task("ServerTasks-52", "Success", "Run env-sleep in infra-nonprod", completed: "2026-09-28T15:59:00.000+00:00"),
                    OctopusReplies.Task("ServerTasks-51", "Failed", "Run env-wake in infra-nonprod", completed: "2026-09-28T13:59:00.000+00:00")));
        yield return Row("no completed task counts", "sleep", "idle: no completed task among the last 50 of each environment", [$"Dry run with the simulated time {Monday1600}."],
            script => script.With("Sleep.DryRun", "true").With("Sleep.NowOverride", Monday1600)
                .TaskList("uat", Done, 50,
                    OctopusReplies.Task("ServerTasks-53", "Success", "Run ENV-SLEEP by hand", completed: "2026-09-28T15:30:00.000+00:00"),
                    OctopusReplies.Task("ServerTasks-54", "Success", "Completed after the simulated now", completed: "2026-09-28T16:30:00.000+00:00"),
                    OctopusReplies.Task("ServerTasks-55", "Canceled", "Never completed")));
        yield return Row("time zone missing: idle rule only", "stay", "active 10 minutes ago, limit 120; last: ServerTasks-56 (Success: Deploy)",
            [$"Dry run with the simulated time {Monday1600}.", "Time zone Mars/Olympus is not installed in the step container; only the idle rule applies."],
            script => script.With("Sleep.DryRun", "true").With("Sleep.NowOverride", Monday1600).With("Sleep.TimeZone", "Mars/Olympus")
                .TaskList("tdd", Done, 50, OctopusReplies.Task("ServerTasks-56", "Success", "Deploy", completed: "2026-09-28T15:50:00.000+00:00")));
        yield return Row("simulated time ignored without a dry run", "sleep", "outside the working window: * America/Chicago", ["Sleep.NowOverride is ignored: it applies only when Sleep.DryRun is true."],
            script => script.With("Sleep.NowOverride", Monday1600).With("Sleep.WorkdayStart", "00:00").With("Sleep.WorkdayEnd", "00:00"));
    }

    /// <summary>Every row of the decision table: one Sleep.Decision line, the output variables and the warnings.</summary>
    /// <param name="row">Name of the row.</param>
    /// <param name="decision">Expected Sleep.Decision.</param>
    /// <param name="reason">Expected Sleep.Reason; <c>*</c> stands for the clock of a real run.</param>
    /// <param name="warnings">Expected warnings, in order.</param>
    [TestCaseSource(nameof(Decisions))]
    [Capability("CAP-OCT-009")]
    public void Should_DecideSleep_DecisionTable_LogsOneDecisionLine(string row, string decision, string reason, string[] warnings)
    {
        if (!Arrangements.ContainsKey(row))
        {
            _ = Decisions().ToArray();
        }

        var script = Decide();
        Arrangements[row](script);
        var run = Quiet(script).Run();

        var dryRun = run.Outputs.GetValueOrDefault("Sleep.DryRun");
        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Outputs["Sleep.Decision"].ShouldBe(decision, run.Transcript);
        run.Outputs["Sleep.Reason"].ShouldMatch("^" + Regex.Escape(reason).Replace(@"\*", @"\S+ \S+", StringComparison.Ordinal) + "$", run.Transcript);
        run.Highlights.ShouldBe([$"Sleep.Decision={decision} Sleep.DryRun={dryRun} Environment=infra-nonprod Reason={run.Outputs["Sleep.Reason"]}"], run.Transcript);
        run.Warnings.ShouldBe(warnings, run.Transcript);
        dryRun.ShouldBe(script.Parameters["Sleep.DryRun"].Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>Invalid settings or an unreadable task list fail the step before any decision.</summary>
    [TestCase("Sleep.NowOverride", "not a time", "Sleep.NowOverride 'not a time' is not a date and time (use ISO 8601, for example 2026-09-24T23:30:00-05:00).")]
    [TestCase("Sleep.WorkdayStart", "7:00", "Sleep.WorkdayStart and Sleep.WorkdayEnd must look like 07:00 and 19:00.")]
    [TestCase("Sleep.IdleMinutes", "2h", "Sleep.IdleMinutes must be a whole number of minutes; got '2h'.")]
    [TestCase("Environment.Class", "test", "Environment.Class 'test' must be nonprod or prod.")]
    [TestCase("Platform.OctopusApiKey", "", "Platform.OctopusApiKey is empty in this step: octopus/terraform scopes it to the REST-calling steps of platform-infrastructure (S5).")]
    [TestCase("task list", "unreadable", "Cannot read the Octopus task list; nothing is stopped.")]
    [Capability("CAP-OCT-009")]
    public void Should_DecideSleep_InvalidInput_FailsTheStep(string variable, string value, string failure)
    {
        var script = Decide().With("Sleep.DryRun", "true").With("Sleep.NowOverride", Monday1600);
        if (variable == "task list")
        {
            script.Api("GET", $@"/api/Spaces-1/tasks\?environment=Environments-2&states={Busy}&take=100", exitCode: 22);
        }
        else
        {
            script.With(variable, value);
        }

        var run = Quiet(script).Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Outputs.ShouldNotContainKey("Sleep.Decision", run.Transcript);
    }

    /// <summary>A sleep decision mutes the alerts, reads the task list again, then stops the cluster without waiting.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_StopCluster_SleepDecision_MutesAlertsRechecksThenStops()
    {
        var run = Stop().Reply(AksState, "Running\nSucceeded\n").Environments().Reply(RuleShow).Reply(RuleOn).Reply(AksStop).AnyTaskList().Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        var mute = run.IndexOf("--enabled true");
        var recheck = run.IndexOf($"states={Busy}");
        var stop = run.IndexOf("aks stop");
        mute.ShouldBeGreaterThan(0, run.Transcript);
        recheck.ShouldBeGreaterThan(mute, run.Transcript);
        stop.ShouldBeGreaterThan(recheck, run.Transcript);
        run.CallsMatching("--enabled false").ShouldBeEmpty(run.Transcript);
        run.Highlights.ShouldBe([$"Stopping aks-platform-nonprod: {Reason}"], run.Transcript);
        run.Log.ShouldContain("Alert processing rule apr-sleep-nonprod enabled.", run.Transcript);
        run.ShouldKeepTheKeyOffCommandLines();
    }

    /// <summary>A task queued since the decision keeps the cluster running and unmutes its alerts.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_StopCluster_BusyAtRecheck_UnmutesAndKeepsRunning()
    {
        var run = Stop().Reply(AksState, "Running\nSucceeded\n").Environments().Reply(RuleShow).Reply(RuleOn).Reply(RuleOff)
            .TaskList("uat", Busy, 100, OctopusReplies.Task("ServerTasks-60", "Executing", "Run env-wake in infra-nonprod"))
            .AnyTaskList()
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("aks stop").ShouldBeEmpty(run.Transcript);
        run.IndexOf("--enabled false").ShouldBeGreaterThan(run.IndexOf("--enabled true"), run.Transcript);
        run.Highlights.ShouldBe(["Not stopping aks-platform-nonprod: busy: ServerTasks-60 (Executing: Run env-wake in infra-nonprod)"], run.Transcript);
        run.Log.ShouldContain("Alert processing rule apr-sleep-nonprod disabled again: aks-platform-nonprod keeps running.", run.Transcript);
    }

    /// <summary>A stop that AKS rejects fails the step and unmutes the alerts.</summary>
    [Test]
    [Capability("CAP-OCT-010")]
    public void Should_StopCluster_StopRejected_UnmutesAndFails()
    {
        var run = Stop().Reply(AksState, "Running\nFailed\n").Environments().Reply(RuleShow).Reply(RuleOn).Reply(RuleOff)
            .Reply(AksStop, exitCode: 1, error: "ERROR: (OperationNotAllowed) the webhook denied the request\n")
            .AnyTaskList()
            .Run();

        run.Failure.ShouldBe("Stopping aks-platform-nonprod was rejected (see above).", run.Transcript);
        run.IndexOf("--enabled false").ShouldBeGreaterThan(run.IndexOf("aks stop"), run.Transcript);
        run.Log.ShouldContain("Alert processing rule apr-sleep-nonprod disabled again: aks-platform-nonprod keeps running.", run.Transcript);
    }

    /// <summary>No sleep decision, a dry run, or a cluster that is not Running and settled changes nothing.</summary>
    [TestCase("stay", "Staying awake: busy: ServerTasks-7 (Executing: Deploy)", null)]
    [TestCase("dry-run", null, "Dry run: the cluster would stop now (outside the working window: Sat 0100 America/Chicago); nothing is changed.")]
    [TestCase("stopped", "aks-platform-nonprod is already stopped.", null)]
    [TestCase("updating", null, null)]
    [TestCase("missing", null, null)]
    [Capability("CAP-OCT-010")]
    public void Should_StopCluster_NothingToStop_ChangesNothing(string scenario, string? log, string? highlight)
    {
        var script = Stop();
        switch (scenario)
        {
            case "stay":
                script.With("Octopus.Action[Decide sleep].Output.Sleep.Decision", "stay").With("Octopus.Action[Decide sleep].Output.Sleep.Reason", "busy: ServerTasks-7 (Executing: Deploy)");
                break;
            case "dry-run":
                script.With("Octopus.Action[Decide sleep].Output.Sleep.DryRun", "true");
                break;
            case "stopped":
                script.Reply(AksState, "Stopped\nSucceeded\n");
                break;
            case "updating":
                script.Reply(AksState, "Running\nUpdating\n");
                break;
            default:
                script.Reply(AksState, exitCode: 3, error: "ERROR: (ResourceNotFound)\n");
                break;
        }

        var run = script.Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("alert-processing-rule update|aks stop").ShouldBeEmpty(run.Transcript);
        run.Calls.Where(call => call.Tool == "curl").ShouldBeEmpty(run.Transcript);
        if (log is not null)
        {
            run.Log.ShouldContain(log, run.Transcript);
        }

        run.Highlights.ShouldBe(highlight is null ? [] : [highlight], run.Transcript);
        run.Warnings.ShouldBe(scenario switch
        {
            "updating" => ["aks-platform-nonprod is Running/Updating; the next hourly run decides again."],
            "missing" => ["Cluster aks-platform-nonprod was not found in rg-platform-nonprod-aks; nothing to stop."],
            _ => [],
        }, run.Transcript);
    }

    private static TestCaseData Row(string name, string decision, string reason, string[] warnings, Action<RunbookScript> arrange)
    {
        Arrangements[name] = arrange;
        return new TestCaseData(name, decision, reason, warnings);
    }

    private static RunbookScript Decide() => RunbookScript.Of(Runbook, "decide-sleep").InTier()
        .With("Sleep.Enabled", "true").With("Sleep.Force", "false").With("Sleep.DryRun", "false").With("Sleep.NowOverride", string.Empty)
        .With("Sleep.TimeZone", "America/Chicago").With("Sleep.WorkDays", "Mon,Tue,Wed,Thu,Fri")
        .With("Sleep.WorkdayStart", "07:00").With("Sleep.WorkdayEnd", "19:00").With("Sleep.IdleMinutes", "120");

    private static RunbookScript Stop() => RunbookScript.Of(Runbook, "stop-cluster").InTier()
        .With("Octopus.Action[Decide sleep].Output.Sleep.Decision", "sleep")
        .With("Octopus.Action[Decide sleep].Output.Sleep.Reason", Reason)
        .With("Octopus.Action[Decide sleep].Output.Sleep.DryRun", "false");

    /// <summary>The environment listings, then an empty task list for any list a row does not reply to itself.</summary>
    private static RunbookScript Quiet(RunbookScript script) => script.Environments().AnyTaskList();
}

/// <summary>Task-list replies of the env-sleep tests.</summary>
internal static class SleepReplies
{
    /// <summary>An empty task list for every task-list call that no earlier reply answers.</summary>
    /// <param name="script">The script.</param>
    public static RunbookScript AnyTaskList(this RunbookScript script) =>
        script.Api("GET", @"/api/Spaces-1/tasks\?environment=Environments-[0-9]+&states=[A-Za-z,]+&take=[0-9]+", new { Items = Array.Empty<object>() });
}
