using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-HARNESS-008 for the pipeline scripts that start and end a run: conformance-arm.ps1 mints the run ID, passes it to
/// every runbook run, sandbox commit and the queued suite, and exports it; conformance-teardown.ps1 force-sleeps what the
/// run woke, with the run ID in its notes, and never fails the build.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceArmScriptTests
{
    private const string GitHubToken = "github-token-for-tests";
    private const string CodefreshKey = "codefresh-key-for-tests";

    /// <summary>The whole arm: forced sleep in both tiers, the run's sandbox commits, the rerun and the suite queued with the run ID.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_AllSystemsAnswer_SleepsBothTiersPushesTheRunCommitsAndQueuesTheSuiteWithTheRunId()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep().WithClusters());
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
            $$$"""{"branch":"main","variables":{"PLATFORM_RUN_ID":"r1-test.x","CONFORMANCE_FAILING_SHA":"{{{failing}}}","CONFORMANCE_GREEN_SHA":"{{{green}}}","CONFORMANCE_RELEASE_SHA":"{{{release}}}","CONFORMANCE_RERUN_BUILD_ID":"6600000000000000000000aa","TEST_FILTER":"FullyQualifiedName~Azure"}}""");

        var calls = harness.Calls();
        var lastRunbookCall = calls.Select((call, index) => (call, index)).Last(entry => entry.call.Url?.StartsWith(PlatformStubRoutes.Api, StringComparison.Ordinal) == true).index;
        var firstPush = calls.Select((call, index) => (call, index)).First(entry => entry.call.Tool == "git" && entry.call.Arguments.Contains("push")).index;
        var firstCodefresh = calls.Select((call, index) => (call, index)).First(entry => entry.call.Url?.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal) == true).index;
        lastRunbookCall.ShouldBeLessThan(firstPush, "the clusters sleep before the sandbox commits");
        firstPush.ShouldBeLessThan(firstCodefresh, "the commits exist before the suite is queued");
        calls.SelectMany(call => call.Arguments).ShouldNotContain(argument =>
            argument.Contains(GitHubToken, StringComparison.Ordinal) || argument.Contains(CodefreshKey, StringComparison.Ordinal) || argument.Contains(PlatformStubRoutes.OctopusKey, StringComparison.Ordinal));
    }

    /// <summary>env-sleep fails in prod: the arm stops before any sandbox commit and names the tier.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenArm_EnvSleepFailsInProd_StopsBeforeAnySandboxCommitNamingTheTier()
    {
        using var harness = Arm(PlatformScriptHarness.Create("curl", "cf_export").WithEnvSleep(prodTask: PlatformStubRoutes.Failed).WithClusters());
        harness.SeedRepository("sandbox", resultsBranch: false);

        var result = harness.Run("conformance-arm.ps1");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Error.ShouldContain("env-sleep did not finish successfully in infra-prod (octopus-runbook exit 1)");
        harness.Exports.ShouldBe(["PLATFORM_RUN_ID=r1-test.x"]);
        harness.Calls("git").ShouldBeEmpty();
        harness.Calls("curl").ShouldNotContain(call => call.Url != null && call.Url.StartsWith("https://g.codefresh.io/", StringComparison.Ordinal));
    }

    /// <summary>The teardown force-sleeps only the tier that was not Running before the run, with the run ID in the notes.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenTeardown_NonprodWasRunning_ForceSleepsOnlyProdAndExitsZero()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithEnvSleep().With("PLATFORM_RUN_ID", "r1-test");
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
    }

    /// <summary>A runbook that fails is a warning: the teardown still exits 0; CONFORMANCE_SLEEP_AFTER=false runs no runbook.</summary>
    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenTeardown_RunbookFailsOrSleepAfterIsFalse_NeverFailsTheBuild()
    {
        using var failing = PlatformScriptHarness.Create("curl").WithEnvSleep(nonprodTask: PlatformStubRoutes.Failed);
        using var kept = PlatformScriptHarness.Create("curl").WithEnvSleep().With("CONFORMANCE_SLEEP_AFTER", "false");

        var slept = failing.Run("conformance-teardown.ps1", "-ResultsDirectory", string.Empty);
        var skipped = kept.Run("conformance-teardown.ps1", "-ResultsDirectory", string.Empty);

        slept.ExitCode.ShouldBe(0, slept.Transcript);
        slept.Error.ShouldContain("WARN env-sleep did not finish successfully in infra-nonprod (octopus-runbook exit 1); the hourly env-sleep will retry");
        slept.Error.ShouldContain("env-sleep finished in infra-prod");
        failing.EnvSleepRuns().ShouldAllBe(run => run["Notes"]!.GetValue<string>() == "conformance:unknown teardown");
        skipped.ExitCode.ShouldBe(0, skipped.Transcript);
        skipped.Error.ShouldContain("CONFORMANCE_SLEEP_AFTER=false; the clusters stay up");
        kept.Calls().ShouldBeEmpty();
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
