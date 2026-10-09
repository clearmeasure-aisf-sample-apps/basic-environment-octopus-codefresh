using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>
/// CAP-KIT-013, the decision: <c>scripts/release/release-stall-check.ps1 -PullRequestBuilds</c> run whole, in a real
/// <c>pwsh</c>, against a stub of the GitHub pull request, commit and status API (the GITHUB_API_URL seam). The recorded
/// case is pull request 84 of the app repository on 2026-10-08: head 4ca02cb, pushed at 04:18 UTC, had no
/// <c>codefresh/ci</c> status an hour later, and the empty commit 3cee7c2 at 05:40 started the build (#101). The other
/// cases (a status in any state, the threshold, drafts, forks, closed pull requests, two repositories, an unreadable
/// repository) use small synthetic pull requests.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PullRequestBuildCheckTests
{
    /// <summary>The repository of the issues and of <c>codefresh/env-checks</c>.</summary>
    internal const string Repository = "example-org/platform-environment";

    /// <summary>The repository of <c>codefresh/ci</c>.</summary>
    internal const string AppRepository = "example-org/work-app";

    /// <summary>The <c>-BuildContext</c> of every run.</summary>
    internal const string Contexts = $"{Repository}=codefresh/env-checks,{AppRepository}=codefresh/ci";

    /// <summary>The head of the recorded pull request whose build never started.</summary>
    internal const string Unbuilt = "4ca02cb5b686a5d03491fe6ecd617bab2bef0beb";

    /// <summary>The empty commit that started the build of the recorded pull request.</summary>
    internal const string EmptyCommit = "3cee7c2553da01f506876b221a5acb4b540fe1dd";

    private const string Day = "2026-10-08";

    private ReleaseStallWorkspace workspace = null!;
    private StubIssueApi api = null!;
    private string noPins = null!;

    /// <summary>Creates the workspace and starts the stub.</summary>
    [SetUp]
    public void CreateWorkspace()
    {
        workspace = new ReleaseStallWorkspace();
        api = new StubIssueApi();
        noPins = workspace.Log("# no pin commit");
    }

    /// <summary>Stops the stub and deletes the workspace.</summary>
    [TearDown]
    public void DeleteWorkspace()
    {
        api.Dispose();
        workspace.Dispose();
    }

    /// <summary>The recorded pull request as it was at 04:18 UTC: head 4ca02cb, and a status in another context only.</summary>
    /// <param name="api">The stub.</param>
    internal static StubPull RecordedPull(StubIssueApi api)
    {
        var pull = api.Pull(AppRepository, 84, Unbuilt, $"{Day}T04:18:04Z", $"{Day}T04:18:07Z", branch: "people/acceptance-setup-retries");
        pull.Statuses.Add(("codefresh/preview", "success"));
        return pull;
    }

    /// <summary>A commit name made of a number.</summary>
    /// <param name="number">The number.</param>
    internal static string Sha(int number) => number.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The recorded case: an hour after the push the head has no status in the required context (one in another context
    /// does not count), so the pull request is reported with one line and one object; once the empty commit carries a
    /// pending status nothing is reported. The check only reads, with the workflow's token, which is never printed.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_RecordedPullRequestWithoutAStatus_WritesTheLineAndTheObjectUntilTheBuildStarts()
    {
        var pull = RecordedPull(api);

        var result = Check($"{Day}T05:17:00Z");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"NOBUILD {AppRepository}#84: no codefresh/ci status on head 4ca02cb, committed at {Day}T04:18:04Z; waiting 58m");
        result.Output.ShouldContain($"FAIL build-start: 1 pull request(s) whose build did not start; 1 open pull request(s) in 2 of 2 repositories, as of {Day}T05:17:00Z, threshold 30 minutes");
        result.Output.ShouldContain("PASS release-stall: no stalled release");
        var row = ReleaseStallWorkspace.Rows(result).ShouldHaveSingleItem();
        ReleaseStallWorkspace.Text(row, "Repository").ShouldBe(AppRepository);
        ReleaseStallWorkspace.Number(row, "PullRequest").ShouldBe(84);
        ReleaseStallWorkspace.Text(row, "Context").ShouldBe("codefresh/ci");
        ReleaseStallWorkspace.Text(row, "State").ShouldBe("NotStarted");
        ReleaseStallWorkspace.Text(row, "Head").ShouldBe(Unbuilt);
        ReleaseStallWorkspace.Text(row, "Branch").ShouldBe("people/acceptance-setup-retries");
        ReleaseStallWorkspace.Text(row, "BuildState").ShouldBeEmpty();
        ReleaseStallWorkspace.Text(row, "CommittedAt").ShouldBe($"{Day}T04:18:04Z");
        ReleaseStallWorkspace.Text(row, "WaitingSince").ShouldBe($"{Day}T04:18:07Z");
        ReleaseStallWorkspace.Number(row, "WaitingMinutes").ShouldBe(58);
        ReleaseStallWorkspace.Number(row, "ThresholdMinutes").ShouldBe(30);
        ReleaseStallWorkspace.Text(row, "Title").ShouldBe($"Build codefresh/ci of pull request {AppRepository}#84 has not started");

        pull.Head = EmptyCommit;
        pull.CommittedAt = DateTimeOffset.Parse($"{Day}T05:40:18Z", System.Globalization.CultureInfo.InvariantCulture);
        pull.Statuses.Add(("codefresh/ci", "pending"));
        var started = Check($"{Day}T06:17:00Z", "-All");

        started.ExitCode.ShouldBe(0, started.Transcript);
        States(started).ShouldBe([$"{AppRepository}#84 Started"]);
        started.Output.ShouldContain($"started {AppRepository}#84 codefresh/ci: codefresh/ci reported pending on head 3cee7c2");
        started.Output.ShouldContain("PASS build-start: no pull request waits for a build that did not start; 1 open pull request(s) in 2 of 2 repositories");
        api.Requests.ShouldAllBe(request => request.Method == "GET" && request.Bearer == StubIssueApi.Token);
        (result.Transcript + started.Transcript).ShouldNotContain(StubIssueApi.Token);
    }

    /// <summary>A status in the required context means the build started, whatever its state (pending, success, failure, error) and however old the head is; the context compares without case, as on GitHub.</summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_StatusInAnyState_MeansTheBuildStarted()
    {
        string[] states = ["pending", "success", "failure", "error"];
        for (var index = 0; index < states.Length; index++)
        {
            api.Pull(Repository, index + 1, Sha(index + 1), "2026-10-01T09:00:00Z").Statuses.Add(("codefresh/env-checks", states[index]));
        }

        api.Pull(Repository, 5, Sha(5), "2026-10-01T09:00:00Z").Statuses.Add(("Codefresh/Env-Checks", "success"));

        var result = Check($"{Day}T12:00:00Z", "-All");

        result.ExitCode.ShouldBe(0, result.Transcript);
        States(result).ShouldBe(Enumerable.Range(1, 5).Select(number => $"{Repository}#{number} Started").ToArray());
        ReleaseStallWorkspace.Rows(result).Select(row => ReleaseStallWorkspace.Text(row, "BuildState")).ShouldBe([.. states, "success"]);
        Regex.Matches(result.Output, "(?m)^NOBUILD ").Count.ShouldBe(0, result.Transcript);
        ReleaseStallWorkspace.Rows(Check($"{Day}T12:00:00Z")).ShouldBeEmpty();
    }

    /// <summary>
    /// A head without a status is reported only after it waited longer than the threshold: 30 minutes by default, strict,
    /// and -BuildThresholdMinutes changes it. The wait starts at the head commit, or at the opening of the pull request
    /// when that is later, so a commit made a day before its push is not reported minutes after the push.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_Threshold_IsThirtyMinutesByDefaultAndCountsFromTheLaterOfCommitAndOpening()
    {
        api.Pull(Repository, 1, Sha(1), $"{Day}T10:00:00Z", $"{Day}T10:00:03Z");

        States(Check($"{Day}T10:30:03Z", "-All")).ShouldBe([$"{Repository}#1 Waiting"]);
        States(Check($"{Day}T10:31:03Z", "-All")).ShouldBe([$"{Repository}#1 NotStarted"]);
        States(Check($"{Day}T11:00:03Z", "-All", "-BuildThresholdMinutes", "60")).ShouldBe([$"{Repository}#1 Waiting"]);
        var hour = Check($"{Day}T11:01:03Z", "-BuildThresholdMinutes", "60");
        hour.ExitCode.ShouldBe(1, hour.Transcript);
        ReleaseStallWorkspace.Number(ReleaseStallWorkspace.Rows(hour).ShouldHaveSingleItem(), "ThresholdMinutes").ShouldBe(60);
        var waiting = Check($"{Day}T10:20:00Z", "-All");
        waiting.ExitCode.ShouldBe(0, waiting.Transcript);
        waiting.Output.ShouldContain($"waiting {Repository}#1 codefresh/env-checks: head 0000000 waits 19m for codefresh/env-checks; the threshold is 30 minutes");

        api.Pull(AppRepository, 2, Sha(2), "2026-10-07T09:00:00Z", $"{Day}T10:20:00Z");

        States(Check($"{Day}T10:50:00Z", "-All")).ShouldBe([$"{Repository}#1 NotStarted", $"{AppRepository}#2 Waiting"]);
        var late = ReleaseStallWorkspace.Rows(Check($"{Day}T10:51:00Z")).Single(row => ReleaseStallWorkspace.Number(row, "PullRequest") == 2);
        ReleaseStallWorkspace.Text(late, "State").ShouldBe("NotStarted");
        ReleaseStallWorkspace.Text(late, "CommittedAt").ShouldBe("2026-10-07T09:00:00Z");
        ReleaseStallWorkspace.Text(late, "WaitingSince").ShouldBe($"{Day}T10:20:00Z");
        ReleaseStallWorkspace.Number(late, "WaitingMinutes").ShouldBe(31);
    }

    /// <summary>
    /// A draft, a pull request from a fork (Codefresh starts no build for one; its statuses are not even read) and a closed
    /// or merged pull request are never reported, however old the head. A draft that is marked ready is reported.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_DraftForkAndClosedPullRequests_AreNeverReported()
    {
        const string old = "2026-10-01T09:00:00Z";
        var draft = api.Pull(Repository, 1, Sha(1), old, draft: true);
        api.Pull(Repository, 2, Sha(2), old, headRepository: "someone/platform-environment");
        api.Pull(Repository, 3, Sha(3), old, headRepository: string.Empty);
        api.Pull(Repository, 4, Sha(4), old).State = "closed";
        var merged = api.Pull(AppRepository, 5, Sha(5), old);
        merged.State = "closed";
        merged.Merged = true;

        var result = Check($"{Day}T12:00:00Z", "-All");

        result.ExitCode.ShouldBe(0, result.Transcript);
        States(result).ShouldBe([$"{Repository}#1 Draft", $"{Repository}#2 Fork", $"{Repository}#3 Fork"]);
        result.Output.ShouldContain("PASS build-start: no pull request waits for a build that did not start; 3 open pull request(s) in 2 of 2 repositories");
        ReleaseStallWorkspace.Rows(Check($"{Day}T12:00:00Z")).ShouldBeEmpty();
        api.Requests.ShouldNotContain(request => request.Path.Contains(Sha(2), StringComparison.Ordinal) || request.Path.Contains(Sha(3), StringComparison.Ordinal));
        api.Requests.ShouldNotContain(request => request.Path.Contains(Sha(4), StringComparison.Ordinal) || request.Path.Contains(Sha(5), StringComparison.Ordinal));

        draft.Draft = false;
        var ready = Check($"{Day}T12:00:00Z");
        ready.ExitCode.ShouldBe(1, ready.Transcript);
        States(ready).ShouldBe([$"{Repository}#1 NotStarted"]);
    }

    /// <summary>
    /// Each repository has its own required context: the status of the other repository's context does not count. Every
    /// page of the open pull requests is read.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_TwoRepositories_EachNeedsItsOwnContextOnEveryPage()
    {
        const string old = "2026-10-07T09:00:00Z";
        api.Pull(Repository, 7, Sha(7), old).Statuses.Add(("codefresh/ci", "success"));
        api.Pull(Repository, 8, Sha(8), old).Statuses.Add(("codefresh/env-checks", "success"));
        for (var number = 100; number < 200; number++)
        {
            api.Pull(AppRepository, number, Sha(number), old, headRepository: "someone/work-app");
        }

        api.Pull(AppRepository, 9, Sha(9), old).Statuses.Add(("codefresh/env-checks", "success"));
        api.Pull(AppRepository, 10, Sha(10), old).Statuses.Add(("codefresh/ci", "failure"));

        var result = Check($"{Day}T12:00:00Z");

        result.ExitCode.ShouldBe(1, result.Transcript);
        ReleaseStallWorkspace.Rows(result).Select(row => ReleaseStallWorkspace.Text(row, "Title")).ShouldBe(
        [
            $"Build codefresh/env-checks of pull request {Repository}#7 has not started",
            $"Build codefresh/ci of pull request {AppRepository}#9 has not started",
        ]);
        result.Output.ShouldContain("FAIL build-start: 2 pull request(s) whose build did not start; 104 open pull request(s) in 2 of 2 repositories");
        api.Requests.ShouldContain(request => request.Path.StartsWith($"/repos/{AppRepository}/pulls?state=open", StringComparison.Ordinal) && request.Path.EndsWith("page=2", StringComparison.Ordinal));
    }

    /// <summary>The script's default -BuildContext names every repository of .claude/factory-loop.json with its pull request context, and the default threshold is 30 minutes.</summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_BuildContextDefaults_ScriptAndFactoryLoop_NameTheSameRepositoriesAndContexts()
    {
        var script = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ReleaseStallWorkspace.Script));
        var loop = JsonNode.Parse(File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ".claude", "factory-loop.json")))!;

        var block = Regex.Match(script, @"\$BuildContext = @\(([^)]*)\)");
        block.Success.ShouldBeTrue("the script declares -BuildContext with a default");
        var defaults = Regex.Matches(block.Groups[1].Value, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        var expected = loop["repos"]!.AsObject().Select(repository => $"{repository.Key}={repository.Value!["ci"]!["prContext"]!.GetValue<string>()}").ToArray();

        expected.Length.ShouldBe(2, "the board tracks this repository and the app repository");
        defaults.ShouldBe(expected, ignoreOrder: true);
        defaults.ShouldContain($"{loop["defaultRepo"]!.GetValue<string>()}=codefresh/env-checks");
        Regex.Match(script, @"\$BuildThresholdMinutes = (\d+)").Groups[1].Value.ShouldBe("30");
    }

    /// <summary>The build check is asked for: without -PullRequestBuilds no call is made. A -BuildContext that is no repository and context pair, or an empty one, is a usage error before any call.</summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_NotAskedForOrUnreadableArguments_MakesNoCall()
    {
        api.Pull(Repository, 1, Sha(1), "2026-10-01T09:00:00Z");

        var notAsked = Run($"{Day}T12:00:00Z", "-BuildContext", Contexts);
        notAsked.ExitCode.ShouldBe(0, notAsked.Transcript);
        notAsked.Output.ShouldNotContain("build-start");
        ReleaseStallWorkspace.Rows(notAsked).ShouldBeEmpty();

        var nonsense = Run($"{Day}T12:00:00Z", "-PullRequestBuilds", "-BuildContext", $"{Repository}=codefresh/env-checks,nonsense");
        nonsense.ExitCode.ShouldBe(2, nonsense.Transcript);
        nonsense.Output.ShouldContain("FAIL release-stall: -BuildContext 'nonsense' is not <owner>/<repo>=<status context>");
        var none = Run($"{Day}T12:00:00Z", "-PullRequestBuilds", "-BuildContext", string.Empty);
        none.ExitCode.ShouldBe(2, none.Transcript);
        none.Output.ShouldContain("-PullRequestBuilds needs at least one -BuildContext <owner>/<repo>=<status context>");
        api.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// A repository that cannot be read (404, as a private repository answers a token without access) fails the run with
    /// the call and its HTTP status, and the other repository is still checked: a finding there is still reported.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReportBuilds_UnreadableRepository_FailsTheRunAndStillChecksTheOther()
    {
        api.Unreadable.Add(AppRepository);
        api.Pull(AppRepository, 84, Unbuilt, $"{Day}T04:18:04Z");
        var own = api.Pull(Repository, 1, Sha(1), $"{Day}T04:00:00Z");

        var result = Check($"{Day}T05:17:00Z");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"FAIL build-start: {AppRepository} could not be read: GET repos/{AppRepository}/pulls?state=open&sort=created&direction=asc&per_page=100&page=1 answered HTTP 404: Not Found");
        result.Output.ShouldContain($"NOBUILD {Repository}#1: no codefresh/env-checks status on head 0000000");
        result.Output.ShouldContain("FAIL build-start: 1 pull request(s) whose build did not start; 1 open pull request(s) in 1 of 2 repositories");
        States(result).ShouldBe([$"{Repository}#1 NotStarted"]);

        own.Statuses.Add(("codefresh/env-checks", "success"));
        var quiet = Check($"{Day}T05:17:00Z");
        quiet.ExitCode.ShouldBe(1, quiet.Transcript);
        quiet.Output.ShouldContain("FAIL build-start: 1 repositories could not be read; 1 open pull request(s) in 1 of 2 repositories");
        quiet.Output.ShouldNotContain("PASS build-start");
        result.Transcript.ShouldNotContain(StubIssueApi.Token);
    }

    /// <summary>Each object of a run with <c>-Json</c> as <c>repository#number State</c>.</summary>
    /// <param name="result">The run.</param>
    internal static string[] States(ProcessResult result) =>
        ReleaseStallWorkspace.Rows(result).Select(row => $"{ReleaseStallWorkspace.Text(row, "Repository")}#{ReleaseStallWorkspace.Number(row, "PullRequest")} {ReleaseStallWorkspace.Text(row, "State")}").ToArray();

    private ProcessResult Check(string now, params string[] arguments) => Run(now, ["-PullRequestBuilds", "-BuildContext", Contexts, .. arguments]);

    private ProcessResult Run(string now, params string[] arguments) =>
        ReleaseStallWorkspace.Run(
            null,
            [("GITHUB_API_URL", api.Url), ("GITHUB_TOKEN", StubIssueApi.Token)],
            ["-Now", now, "-PinLog", noPins, "-AppsRoot", workspace.AppsRoot, "-Json", .. arguments]);
}
