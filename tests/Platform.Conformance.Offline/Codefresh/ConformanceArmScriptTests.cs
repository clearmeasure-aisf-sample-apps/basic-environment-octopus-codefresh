using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-HARNESS-008 for the pipeline scripts that start and end a run: conformance-arm.ps1 mints the run ID, passes it to
/// every runbook run, sandbox commit and the queued suite, and exports it, and holds the hourly env-sleep for the run
/// (sleep-hold, held by conformance:&lt;run id&gt;); conformance-teardown.ps1 releases that hold and force-sleeps what the
/// run woke, with the run ID in its notes, and never fails the build.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceArmScriptTests
{
    private const string GitHubToken = "github-token-for-tests";
    private const string CodefreshKey = "codefresh-key-for-tests";

    /// <summary>The whole arm: forced sleep in both tiers, the hold, the run's sandbox commits, the rerun and the suite queued with the run ID.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_AllSystemsAnswer_SleepsBothTiersPushesTheRunCommitsAndQueuesTheSuiteWithTheRunId()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep().WithSleepHold().WithClusters());
        var bare = harness.SeedRepository("sandbox", resultsBranch: true, "conformance/rold/green", "conformance/rold/failing-test", "feature/keep");

        var result = harness.Run("conformance-arm.ps1");

        result.ExitCode.ShouldBe(0, result.Transcript);
        var failing = harness.Git(harness.Root, "--git-dir", bare, "rev-parse", "refs/heads/conformance/r1-test.x/failing-test").Trim();
        var green = harness.Git(harness.Root, "--git-dir", bare, "rev-parse", "refs/heads/conformance/r1-test.x/green").Trim();
        var release = harness.Git(harness.Root, "--git-dir", bare, "rev-parse", "refs/heads/main").Trim();
        harness.Exports.ShouldBe(
        [
            "PLATFORM_RUN_ID=r1-test.x",
            $"CONFORMANCE_FAILING_SHA={failing}",
            $"CONFORMANCE_GREEN_SHA={green}",
            $"CONFORMANCE_RELEASE_SHA={release}",
        ]);
        harness.Git(harness.Root, "--git-dir", bare, "show", $"{failing}:toggles/failing-test").ShouldBe("run-id: r1-test.x\n");
        harness.Git(harness.Root, "--git-dir", bare, "show", $"{green}:conformance/run-id").ShouldBe("run-id: r1-test.x\n");
        harness.Git(harness.Root, "--git-dir", bare, "show", $"{release}:conformance/last-run").ShouldBe("run-id: r1-test.x\n");
        harness.Git(harness.Root, "--git-dir", bare, "log", "-1", "--format=%an <%ae> %s", release).Trim()
            .ShouldBe("platform-conformance <platform-conformance@users.noreply.github.com> conformance r1-test.x: release canary");
        harness.Git(harness.Root, "--git-dir", bare, "for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldBe(
            ["conformance-results", "conformance/r1-test.x/failing-test", "conformance/r1-test.x/green", "feature/keep", "main"], ignoreOrder: true);

        var runs = harness.EnvSleepRuns();
        runs.Select(run => run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1", "Environments-2"], ignoreOrder: true);
        runs.ShouldAllBe(run => run["Notes"]!.GetValue<string>() == "conformance:r1-test.x force-sleep" && run["Runs"]![0]!["FormValues"]!.ToJsonString() == """{"d1e2f3":"true"}""");
        var holds = harness.SleepHoldRuns();
        holds.Select(run => run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1", "Environments-2"]);
        holds.ShouldAllBe(run => run["Notes"]!.GetValue<string>() == "conformance:r1-test.x hold" && run["Runs"]![0]!["FormValues"]!.ToJsonString() == """{"h1":"480","h2":"conformance:r1-test.x"}""");

        var codefresh = harness.Calls("curl").Where(call => call.Url?.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal) == true).ToArray();
        codefresh.Select(call => $"{call.Method} {call.Url}").ShouldBe(
        [
            "GET https://g.codefresh.io/api/pipelines/sandbox%2Frelease",
            "POST https://g.codefresh.io/api/pipelines/run/sandbox%2Frelease",
            "POST https://g.codefresh.io/api/pipelines/run/platform-env%2Fconformance",
        ]);
        codefresh.ShouldAllBe(call => call.Headers.SequenceEqual(new[] { $"Authorization: {CodefreshKey}", "Content-Type: application/json" }));
        codefresh.SelectMany(call => call.HeaderFiles).ShouldAllBe(file => file.Private);
        JsonNode.Parse(codefresh[1].Body!)!.ToJsonString().ShouldBe($$"""{"branch":"main","sha":"{{release}}","trigger":"trig-123"}""");
        JsonNode.Parse(codefresh[2].Body!)!.ToJsonString().ShouldBe(
            $$$"""{"branch":"main","variables":{"PLATFORM_RUN_ID":"r1-test.x","CONFORMANCE_FAILING_SHA":"{{{failing}}}","CONFORMANCE_GREEN_SHA":"{{{green}}}","CONFORMANCE_RELEASE_SHA":"{{{release}}}","CONFORMANCE_RERUN_BUILD_ID":"6600000000000000000000aa","CONFORMANCE_ARM_STOPPED":"nonprod,prod","TEST_FILTER":"FullyQualifiedName~Azure"}}""");

        var calls = harness.Calls();
        var lastRunbookCall = calls.Select((call, index) => (call, index)).Last(entry => entry.call.Url?.StartsWith(PlatformStubRoutes.Api, StringComparison.Ordinal) == true).index;
        var firstPush = calls.Select((call, index) => (call, index)).First(entry => entry.call.Tool == "git" && entry.call.Arguments.Contains("push")).index;
        var firstCodefresh = calls.Select((call, index) => (call, index)).First(entry => entry.call.Url?.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal) == true).index;
        var lastSleep = calls.Select((call, index) => (call, index)).Last(entry => entry.call.Url?.Contains("/runbooks/Runbooks-7/", StringComparison.Ordinal) == true).index;
        var firstHold = calls.Select((call, index) => (call, index)).First(entry => entry.call.Url?.Contains("/runbooks/Runbooks-11/", StringComparison.Ordinal) == true).index;
        lastSleep.ShouldBeLessThan(firstHold, "the hold is set after the forced sleep");
        lastRunbookCall.ShouldBeLessThan(firstPush, "the clusters sleep and the hold is set before the sandbox commits");
        firstPush.ShouldBeLessThan(firstCodefresh, "the commits exist before the suite is queued");
        calls.SelectMany(call => call.Arguments).ShouldNotContain(argument =>
            argument.Contains(GitHubToken, StringComparison.Ordinal) || argument.Contains(CodefreshKey, StringComparison.Ordinal) || argument.Contains(PlatformStubRoutes.OctopusKey, StringComparison.Ordinal));
    }

    /// <summary>env-sleep fails in prod: the arm stops before any sandbox commit and names the tier.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_EnvSleepFailsInProd_StopsBeforeAnySandboxCommitNamingTheTier()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep(prodTask: PlatformStubRoutes.Failed).WithSleepHold().WithClusters());
        harness.SeedRepository("sandbox", resultsBranch: false);

        var result = harness.Run("conformance-arm.ps1");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Error.ShouldContain("env-sleep did not finish successfully in infra-prod (octopus-runbook exit 1)");
        harness.Exports.ShouldBe(["PLATFORM_RUN_ID=r1-test.x"]);
        harness.Calls("git").ShouldBeEmpty();
        harness.Calls("curl").ShouldNotContain(call => call.Url != null && call.Url.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal));
        harness.SleepHoldRuns().ShouldBeEmpty();
    }

    /// <summary>
    /// sleep-hold fails in prod: a run without the hold is the bug the hold fixes, so the arm stops before any sandbox
    /// commit, naming the tier.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_SleepHoldFailsInProd_StopsBeforeAnySandboxCommitNamingTheTier()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep().WithSleepHold(prodTask: PlatformStubRoutes.Failed).WithClusters());
        harness.SeedRepository("sandbox", resultsBranch: false);

        var result = harness.Run("conformance-arm.ps1");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Error.ShouldContain("platform-infrastructure/sleep-hold in infra-prod did not finish successfully (task ServerTasks-902, state Failed)");
        result.Error.ShouldContain("sleep-hold did not finish successfully in infra-prod (octopus-runbook exit 1): without the hold the hourly env-sleep can stop a cluster during the run");
        harness.Exports.ShouldBe(["PLATFORM_RUN_ID=r1-test.x"]);
        harness.Calls("git").ShouldBeEmpty();
        harness.Calls("curl").ShouldNotContain(call => call.Url != null && call.Url.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal));
    }

    /// <summary>
    /// CONFORMANCE_SKIP_SLEEP=true skips only the forced sleep: both tiers are still held, for CONFORMANCE_HOLD_MINUTES
    /// capped at 720.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_SkipSleepAndLongHold_StillHoldsBothTiersForAtMost720Minutes()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep().WithSleepHold().WithClusters())
            .With("CONFORMANCE_SKIP_SLEEP", "true").With("CONFORMANCE_HOLD_MINUTES", "900");
        harness.SeedRepository("sandbox", resultsBranch: false);

        var result = harness.Run("conformance-arm.ps1");

        result.ExitCode.ShouldBe(0, result.Transcript);
        harness.EnvSleepRuns().ShouldBeEmpty();
        var holds = harness.SleepHoldRuns();
        holds.Select(run => run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1", "Environments-2"]);
        holds.ShouldAllBe(run => run["Runs"]![0]!["FormValues"]!.ToJsonString() == """{"h1":"720","h2":"conformance:r1-test.x"}""");
        result.Error.ShouldContain("conformance-arm: env-sleep held for 720 minute(s) in infra-nonprod and infra-prod");
    }

    /// <summary>
    /// The teardown releases the hold in both tiers first, then force-sleeps only the tier that was not Running before the
    /// run, with the run ID in the notes.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenTeardown_NonprodWasRunning_ForceSleepsOnlyProdAndExitsZero()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep().WithSleepHold().With("PLATFORM_RUN_ID", "r1-test");
        var results = Directory.CreateDirectory(Path.Combine(harness.Volume, "conformance", "6600000000000000000000aa")).FullName;
        File.WriteAllText(Path.Combine(results, "power-before.txt"), "nonprod=Running/Succeeded\nprod=Stopped/Succeeded\n");

        var result = harness.Run("conformance-teardown.ps1", "-ResultsDirectory", results);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Error.ShouldContain("conformance-teardown: nonprod was Running before the run; left up");
        result.Error.ShouldContain("conformance-teardown: env-sleep finished in infra-prod");
        var run = harness.EnvSleepRuns().Single();
        run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>().ShouldBe("Environments-2");
        run["Notes"]!.GetValue<string>().ShouldBe("conformance:r1-test teardown");
        run["Runs"]![0]!["FormValues"]!.ToJsonString().ShouldBe("""{"d1e2f3":"true"}""");
        var releases = harness.SleepHoldRuns();
        releases.Select(release => release["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1", "Environments-2"]);
        releases.ShouldAllBe(release => release["Notes"]!.GetValue<string>() == "conformance:r1-test release" && release["Runs"]![0]!["FormValues"]!.ToJsonString() == """{"h1":"0","h2":"conformance:r1-test"}""");
        result.Error.ShouldContain("conformance-teardown: sleep hold released in infra-nonprod");
        result.Error.ShouldContain("conformance-teardown: sleep hold released in infra-prod");
        var calls = harness.Calls("curl");
        var lastRelease = calls.Select((call, index) => (call, index)).Last(entry => entry.call.Url?.Contains("/runbooks/Runbooks-11/", StringComparison.Ordinal) == true).index;
        var firstSleep = calls.Select((call, index) => (call, index)).First(entry => entry.call.Url?.Contains("/runbooks/Runbooks-7/", StringComparison.Ordinal) == true).index;
        lastRelease.ShouldBeLessThan(firstSleep, "the hold is released before the forced sleep");
    }

    /// <summary>
    /// A runbook that fails is a warning: the teardown still exits 0; CONFORMANCE_SLEEP_AFTER=false runs no env-sleep but
    /// still releases the hold.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenTeardown_RunbookFailsOrSleepAfterIsFalse_NeverFailsTheBuild()
    {
        using var failing = PlatformScriptHarness.Create("curl").WithEnvSleep(nonprodTask: PlatformStubRoutes.Failed).WithSleepHold();
        using var kept = PlatformScriptHarness.Create("curl").WithEnvSleep().WithSleepHold().With("CONFORMANCE_SLEEP_AFTER", "false");

        var slept = failing.Run("conformance-teardown.ps1", "-ResultsDirectory", string.Empty);
        var skipped = kept.Run("conformance-teardown.ps1", "-ResultsDirectory", string.Empty);

        slept.ExitCode.ShouldBe(0, slept.Transcript);
        slept.Error.ShouldContain("WARN env-sleep did not finish successfully in infra-nonprod (octopus-runbook exit 1); the hourly env-sleep will retry");
        slept.Error.ShouldContain("env-sleep finished in infra-prod");
        failing.EnvSleepRuns().ShouldAllBe(run => run["Notes"]!.GetValue<string>() == "conformance:unknown teardown");
        skipped.ExitCode.ShouldBe(0, skipped.Transcript);
        skipped.Error.ShouldContain("CONFORMANCE_SLEEP_AFTER=false; the clusters stay up");
        kept.EnvSleepRuns().ShouldBeEmpty();
        kept.SleepHoldRuns().Select(release => release["Runs"]![0]!["FormValues"]!.ToJsonString()).ShouldBe(["""{"h1":"0","h2":"conformance:unknown"}""", """{"h1":"0","h2":"conformance:unknown"}"""]);
    }

    /// <summary>
    /// A tier the arm stopped is slept after the run even when power-before.txt reads Running: the sandbox builds between
    /// the arm and the suite woke it (the early env-wake of the release), not the operator.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void Should_Teardown_ArmStoppedTierWokenBeforeTheRecord_ForceSleepsItAndLeavesTheOtherTiersRule()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep().WithSleepHold().With("PLATFORM_RUN_ID", "r1-test").With("CONFORMANCE_ARM_STOPPED", "nonprod");
        var results = Directory.CreateDirectory(Path.Combine(harness.Volume, "conformance", "6600000000000000000000aa")).FullName;
        File.WriteAllText(Path.Combine(results, "power-before.txt"), "nonprod=Running/Succeeded\nprod=Running/Succeeded\n");

        var result = harness.Run("conformance-teardown.ps1", "-ResultsDirectory", results);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Error.ShouldContain("conformance-teardown: nonprod was stopped by conformance-arm and woken before power-before.txt was written; sleeping it");
        result.Error.ShouldContain("conformance-teardown: prod was Running before the run; left up");
        harness.EnvSleepRuns().Select(run => run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1"]);
    }

    /// <summary>A release that fails is only a warning: the teardown still force-sleeps and exits 0.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenTeardown_ReleaseFails_WarnsAndStillForceSleeps()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep().WithSleepHold(nonprodTask: PlatformStubRoutes.Failed).With("PLATFORM_RUN_ID", "r1-test");

        var result = harness.Run("conformance-teardown.ps1", "-ResultsDirectory", string.Empty);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Error.ShouldContain("conformance-teardown: WARN sleep-hold did not finish successfully in infra-nonprod (octopus-runbook exit 1); the hold ends by itself");
        result.Error.ShouldContain("conformance-teardown: sleep hold released in infra-prod");
        harness.EnvSleepRuns().Select(run => run["Runs"]![0]!["EnvironmentId"]!.GetValue<string>()).ShouldBe(["Environments-1", "Environments-2"], ignoreOrder: true);
    }

    private static PlatformScriptHarness Arm(PlatformScriptHarness harness)
    {
        harness.RecordRealGit();
        harness.With("PLATFORM_RUN_ID", "R1-TEST.x").With("CONFORMANCE_STOP_GRACE_MINUTES", "0").With("TEST_FILTER", "FullyQualifiedName~Azure")
            .With("GITHUB_TOKEN", GitHubToken).With("SANDBOX_APP_REPO", "example-org/platform-sandbox").With("SANDBOX_GIT_URL", Path.Combine(harness.Root, "sandbox.git"))
            .With("CF_API_KEY", CodefreshKey)
            .With("GIT_AUTHOR_DATE", "2026-09-25T01:00:00Z").With("GIT_COMMITTER_DATE", "2026-09-25T01:00:00Z");
        harness.Route("curl", ["https://g.codefresh.io/api/pipelines/sandbox%2Frelease"], """{"spec": {"triggers": [{"name": "pr", "id": "trig-000"}, {"name": "main-push", "id": "trig-123"}]}}""");
        harness.Route("curl", ["https://g.codefresh.io/api/pipelines/run/sandbox%2Frelease"], "\"6600000000000000000000aa\"");
        harness.Route("curl", ["https://g.codefresh.io/api/pipelines/run/platform-env%2Fconformance"], "\"6600000000000000000000bb\"");
        return harness;
    }
}
