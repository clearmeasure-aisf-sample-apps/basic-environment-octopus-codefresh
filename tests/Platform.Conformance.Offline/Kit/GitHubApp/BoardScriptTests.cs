using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit.GitHubApp;

/// <summary>
/// CAP-KIT-010, integration half: the feature-loop helper <c>board.ps1</c> and the stall watchdog <c>Check-StalledLanes.ps1</c>
/// run whole, in a real <c>pwsh</c>, against a stub GitHub API (the GITHUB_API_URL seam), with the real
/// <c>.claude/factory-loop.json</c>. They resolve their GitHub token in the documented order (a pre-minted App token, a token
/// minted from the App's key, then the GitHub CLI), send it as the Bearer of every call, retry a call the App cannot make
/// with the CLI token, ignore the retired personal access token variable, and never print a token, key or JWT.
/// The same runs hold which commit <c>board.ps1 wait ci</c> answers for (#102): the pull request's head at that poll, and with
/// <c>-Head</c> that commit alone.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class BoardScriptTests
{
    private const string BoardScript = ".claude/skills/feature-loop/board.ps1";
    private const string WatchdogScript = ".claude/skills/feature-loop-dispatch/Check-StalledLanes.ps1";
    private const string EnvRepo = "clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh";
    private const string DecoyPat = "decoy-personal-access-token-0004";
    private const string AppRepo = "clearmeasure-aisf-sample-apps/20260923-001";

    // #102: the head a push replaced (its build had failed) and the head that push made.
    private const string PreviousHead = "ea088d69200e4f1a9b7c5d3e2f1a0b9c8d7e6f5a";
    private const string PushedHead = "3cee7c2b5a4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f";

    // A wait polls every 0.3 s here; the production interval is minutes.
    private static readonly string[] FastPoll = ["-IntervalMinutes", "0.005"];

    /// <summary>With only the App key variables set, the helper mints an installation token narrowed by factory-loop.json and uses it for every later call; the decoy personal access token is never sent.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_OnlyTheKeyVariablesSet_MintsTheAppTokenThenUsesItOnEveryCall()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["status", "45"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"PR {EnvRepo}#45 open head=0123456789ab mergeable=clean");
        result.Output.ShouldContain("codefresh/env-checks success");
        var requests = api.Requests;
        var mint = requests[0];
        mint.Method.ShouldBe("POST");
        mint.PathOnly.ShouldBe("/app/installations/166366113/access_tokens");
        key.Verifies(mint.Bearer).ShouldBeTrue("the exchange is authenticated by a JWT signed with the App key");
        var body = JsonNode.Parse(mint.Body)!.AsObject();
        body["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["basic-environment-octopus-codefresh", "20260923-001"]);
        var permissions = body["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal);
        permissions.ShouldBe(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["organization_projects"] = "write", ["issues"] = "read", ["pull_requests"] = "read", ["metadata"] = "read" },
            ignoreOrder: true);
        requests.Skip(1).ShouldNotBeEmpty();
        requests.Skip(1).ShouldAllBe(request => request.Bearer == StubGitHubApi.AppToken, "every call after the mint carries the minted token");
        requests.ShouldNotContain(request => request.Authorization.Contains(DecoyPat, StringComparison.Ordinal));
        AssertNoSecrets(result, key, mint.Bearer);
    }

    /// <summary>A pre-minted token (AISF_BOARD_APP_TOKEN) wins: no token is minted and no key is needed.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_PreMintedAppToken_IsUsedWithoutMinting()
    {
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["status", "45"], ("AISF_BOARD_APP_TOKEN", StubGitHubApi.PreMintedAppToken), ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldNotContain(request => request.PathOnly.StartsWith("/app/", StringComparison.Ordinal));
        api.Requests.ShouldAllBe(request => request.Bearer == StubGitHubApi.PreMintedAppToken);
        result.Transcript.ShouldNotContain(StubGitHubApi.PreMintedAppToken);
    }

    /// <summary>Without any App variable the GitHub CLI token is used, and the decoy personal access token is ignored.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_NoAppVariables_UsesTheGitHubCliTokenAndIgnoresTheRetiredVariable()
    {
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["status", "45"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldAllBe(request => request.Bearer == StubGitHubApi.CliToken);
        result.Transcript.ShouldNotContain(StubGitHubApi.CliToken);
        result.Transcript.ShouldNotContain(DecoyPat);
    }

    /// <summary>A commit without statuses (the merge commit of this repository, whose pushes to main start no check) is reported as such, and status still exits 0.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_MergedPullRequestWhoseMergeCommitHasNoStatuses_ReportsNoStatusesAndExitsZero()
    {
        using var api = new StubGitHubApi { Merged = true };
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["status", "45"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"PR {EnvRepo}#45 merged head=0123456789ab");
        result.Output.ShouldContain("head 0123456789ab: codefresh/env-checks success");
        result.Output.ShouldContain($"merge commit {StubGitHubApi.MergeSha}");
        result.Output.ShouldContain("merge fedcba987654: no statuses");
    }

    /// <summary>The App has no Commit statuses: read; a refused read is retried once with the GitHub CLI token and the command still succeeds.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_AppRefusedByTheStatusesEndpoint_RetriesWithTheCliToken()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { AppMayReadStatuses = false };
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["status", "45"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain("board: the App token was refused (HTTP 403 on Get repos/");
        result.Output.ShouldContain("codefresh/env-checks success");
        var statusCalls = api.Requests.Where(request => request.PathOnly.EndsWith("/status", StringComparison.Ordinal)).ToArray();
        statusCalls.Select(request => request.Bearer).ShouldBe([StubGitHubApi.AppToken, StubGitHubApi.CliToken]);
        AssertNoSecrets(result, key, api.Requests[0].Bearer, StubGitHubApi.CliToken);
    }

    /// <summary>The App cannot dispatch (no Contents: write): move retries the dispatch with the GitHub CLI token, which lands (204), and posts no fallback comment.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardMove_DispatchRefusedWithTheAppToken_RetriesWithTheCliTokenAndMoves()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["move", "45", "In Progress"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"MOVED {EnvRepo}#45 -> In Progress (dispatch 204)");
        var dispatches = api.Requests.Where(request => request.PathOnly.EndsWith("/dispatches", StringComparison.Ordinal)).ToArray();
        dispatches.Select(request => request.Bearer).ShouldBe([StubGitHubApi.AppToken, StubGitHubApi.CliToken]);
        var payload = JsonNode.Parse(dispatches[1].Body)!;
        payload["event_type"]!.GetValue<string>().ShouldBe("board-status");
        payload["client_payload"]!["status"]!.GetValue<string>().ShouldBe("In Progress");
        payload["client_payload"]!["issue"]!.GetValue<int>().ShouldBe(45);
        api.Requests.ShouldNotContain(request => request.PathOnly.EndsWith("/comments", StringComparison.Ordinal));
        AssertNoSecrets(result, key, api.Requests[0].Bearer, StubGitHubApi.CliToken);
    }

    /// <summary>When the dispatch is refused for the GitHub CLI token too, the board-status fallback comment is posted (through the same retry) and move exits 1.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardMove_DispatchRefusedEverywhere_PostsTheFallbackCommentAndExitsOne()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { DispatchStatusForCli = 403 };
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["move", "45", "Done"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"REFUSED {EnvRepo}#45 -> Done (dispatch HTTP 403)");
        result.Output.ShouldContain($"FALLBACK comment posted on {EnvRepo}#45");
        var comments = api.Requests.Where(request => request.PathOnly.EndsWith("/issues/45/comments", StringComparison.Ordinal)).ToArray();
        comments.Select(request => request.Bearer).ShouldBe([StubGitHubApi.AppToken, StubGitHubApi.CliToken]);
        JsonNode.Parse(comments[1].Body)!["body"]!.GetValue<string>().ShouldStartWith("board-status: Done");
        AssertNoSecrets(result, key, api.Requests[0].Bearer, StubGitHubApi.CliToken);
    }

    /// <summary>With no GitHub CLI token to retry with, a dispatch refused for the App token is reported as refused, and the failed fallback comment is reported too.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardMove_AppOnlyAndNoCliToken_ReportsBothRefusals()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["move", "45", "Done"]);

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"REFUSED {EnvRepo}#45 -> Done (dispatch HTTP 403)");
        result.Output.ShouldContain("FALLBACK comment refused too (HTTP 403)");
        api.Requests.ShouldNotContain(request => request.Bearer == StubGitHubApi.CliToken);
    }

    /// <summary>A key that the API refuses to exchange falls back to the GitHub CLI token and says so without a key, JWT or token in the output.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_MintRefused_FallsBackToTheCliToken()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { MintStatus = 401 };
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, key, ["status", "45"], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain("github-app: GitHub App 5130401: no installation token (HTTP 401). Falling back to the GitHub CLI token.");
        api.Requests.Skip(1).ShouldAllBe(request => request.Bearer == StubGitHubApi.CliToken);
        AssertNoSecrets(result, key, api.Requests[0].Bearer, StubGitHubApi.CliToken);
    }

    /// <summary>With no token at all the helper exits 2 and names the variables that would do.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardStatus_NoTokenAtAll_ExitsTwoNamingTheAppVariables()
    {
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["status", "45"]);

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Output.ShouldContain("AISF_BOARD_APP_TOKEN");
        result.Output.ShouldContain("AISF_BOARD_APP_PRIVATE_KEY_PATH");
        api.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// #102 as it happened (app repository, <c>codefresh/ci</c>): the wait starts right after a push, the API still names the
    /// previous head, whose build had failed, then the pushed head with a pending and later a successful build. With the
    /// pushed commit as <c>-Head</c> the wait never reads the previous head's result and returns the pushed head's success.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_ExpectedHeadWhileTheApiStillNamesThePreviousHead_ReturnsThePushedHeadsSuccess()
    {
        using var api = new StubGitHubApi { Heads = [PreviousHead, PreviousHead, PushedHead], StatusContext = "codefresh/ci" };
        api.States[PreviousHead] = ["failure"];
        api.States[PushedHead] = ["pending", "success"];
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", $"{AppRepo}#45", "-Head", PushedHead[..7], .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        WaitLines(result).ShouldBe(
        [
            "waiting for head 3cee7c2: the pull request names head ea088d69200e, whose codefresh/ci result is not read",
            "pull request head changed from ea088d69200e to 3cee7c2b5a4d: the wait decides on 3cee7c2b5a4d",
            $"codefresh/ci pending on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
            $"codefresh/ci success on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
        ]);
        StatusReads(api, PreviousHead).ShouldBe(0, "the result of a head other than -Head is never read");
        api.Requests.ShouldAllBe(request => request.PathOnly.StartsWith($"/repos/{AppRepo}/", StringComparison.Ordinal));
    }

    /// <summary>While the API names only another head, a wait with <c>-Head</c> keeps waiting until its deadline (exit 4) and never returns that head's finished failure.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_ExpectedHeadNeverNamed_TimesOutWithoutReturningTheOtherHeadsFailure()
    {
        using var api = new StubGitHubApi { Heads = [PreviousHead] };
        api.States[PreviousHead] = ["failure"];
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", "45", "-Head", PushedHead, "-TimeoutMinutes", "0.02", .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(4, result.Transcript);
        WaitLines(result).ShouldBe(
        [
            "waiting for head 3cee7c2b5a4d: the pull request names head ea088d69200e, whose codefresh/env-checks result is not read",
            "TIMEOUT after 0.02 min: waiting for head 3cee7c2b5a4d: the pull request names head ea088d69200e, whose codefresh/env-checks result is not read",
        ]);
        StatusReads(api, PreviousHead).ShouldBe(0);
    }

    /// <summary>
    /// The race without <c>-Head</c>: the API names the previous head, its failed result is read, and the pull request names the
    /// pushed head when the head is read again. The failure does not count; the wait decides on the pushed head and returns its success.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_HeadMovesWhileThePreviousHeadsFailureIsRead_ReturnsThePushedHeadsSuccess()
    {
        using var api = new StubGitHubApi { Heads = [PreviousHead, PushedHead] };
        api.States[PreviousHead] = ["failure"];
        api.States[PushedHead] = ["pending", "success"];
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", "45", .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        WaitLines(result).ShouldBe(
        [
            "codefresh/env-checks failure on ea088d69200e does not count: the pull request head moved to 3cee7c2b5a4d while it was read",
            $"codefresh/env-checks pending on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
            $"codefresh/env-checks success on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
        ]);
    }

    /// <summary>A head that changes between two polls restarts the decision: the answer (here a failure, exit 1) is the new head's, and the output says the head changed.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_HeadChangesBetweenPolls_DecidesOnTheNewHead()
    {
        using var api = new StubGitHubApi { Heads = [PreviousHead, PushedHead] };
        api.States[PreviousHead] = ["pending"];
        api.States[PushedHead] = ["pending", "failure"];
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", "45", .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(1, result.Transcript);
        WaitLines(result).ShouldBe(
        [
            $"codefresh/env-checks pending on head ea088d69200e {StubGitHubApi.BuildUrl(PreviousHead)}",
            "pull request head changed from ea088d69200e to 3cee7c2b5a4d: the wait decides on 3cee7c2b5a4d",
            $"codefresh/env-checks pending on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
            $"codefresh/env-checks failure on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
        ]);
    }

    /// <summary>
    /// With <c>-Head</c>, the API names the expected head, then another head (whose finished failure would end the wait with
    /// exit 1), then the expected head again. The other head's result is never read: the wait says the head changed, waits,
    /// and returns the expected head's success.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_ApiNamesAnotherHeadInTheMiddleOfTheWait_NeverReadsItsResultAndReturnsTheExpectedHeadsSuccess()
    {
        using var api = new StubGitHubApi { Heads = [PushedHead, PreviousHead, PreviousHead, PushedHead] };
        api.States[PreviousHead] = ["failure"];
        api.States[PushedHead] = ["pending", "success"];
        using var fakeGh = new FakeGhCli();

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", "45", "-Head", PushedHead, .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(0, result.Transcript);
        WaitLines(result).ShouldBe(
        [
            $"codefresh/env-checks pending on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
            "pull request head changed from 3cee7c2b5a4d to ea088d69200e",
            "waiting for head 3cee7c2b5a4d: the pull request names head ea088d69200e, whose codefresh/env-checks result is not read",
            "pull request head changed from ea088d69200e to 3cee7c2b5a4d: the wait decides on 3cee7c2b5a4d",
            $"codefresh/env-checks success on head 3cee7c2b5a4d {StubGitHubApi.BuildUrl(PushedHead)}",
        ]);
        StatusReads(api, PreviousHead).ShouldBe(0, "the result of a head other than -Head is never read");
    }

    /// <summary>
    /// A head that does not move keeps the old answer, with or without a matching <c>-Head</c> (a prefix in any case): one line,
    /// exit 0 for success and 1 for failure or error. Without <c>-Head</c> this is also the limit of the wait: the head the API
    /// names is the only one it can know, so after a push the pushed commit is passed.
    /// </summary>
    /// <param name="state">The finished state of the head's status.</param>
    /// <param name="expectHead">Pass the head as <c>-Head</c>.</param>
    /// <param name="exitCode">The expected exit code.</param>
    [TestCase("success", false, 0)]
    [TestCase("success", true, 0)]
    [TestCase("failure", false, 1)]
    [TestCase("failure", true, 1)]
    [TestCase("error", false, 1)]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoardWaitCi_HeadStaysWithAFinishedResult_ReturnsItAtOnce(string state, bool expectHead, int exitCode)
    {
        using var api = new StubGitHubApi();
        api.States[StubGitHubApi.HeadSha] = [state];
        using var fakeGh = new FakeGhCli();
        string[] head = expectHead ? ["-Head", StubGitHubApi.HeadSha[..12].ToUpperInvariant()] : [];

        var result = RunBoard(api, fakeGh, null, ["wait", "ci", "45", .. head, .. FastPoll], ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

        result.ExitCode.ShouldBe(exitCode, result.Transcript);
        WaitLines(result).ShouldBe([$"codefresh/env-checks {state} on head 0123456789ab {StubGitHubApi.BuildUrl(StubGitHubApi.HeadSha)}"]);
        StatusReads(api, StubGitHubApi.HeadSha).ShouldBe(1);
    }

    /// <summary>
    /// <c>-Head</c> that is no commit SHA, or given to anything but <c>wait ci &lt;pr&gt;</c>, is a usage error (exit 2) before any
    /// call. An empty value is one of them (a variable that was never set): it does not become a wait without the head.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunBoard_HeadThatIsNoShaOrGoesWithAnotherCommand_ExitsTwoBeforeAnyCall()
    {
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();
        var cases = new (string[] Arguments, string Message)[]
        {
            (["wait", "ci", "45", "-Head", "not-a-sha"], "board: -Head 'not-a-sha' is not a commit SHA (7-40 hex characters)"),
            (["wait", "ci", "45", "-Head", "3cee7"], "board: -Head '3cee7' is not a commit SHA (7-40 hex characters)"),
            (["wait", "ci", "45", "-Head", string.Empty], "board: -Head '' is not a commit SHA (7-40 hex characters)"),
            (["wait", "release", "45", "-Head", "3cee7c2"], "board: -Head goes with wait ci <pr> only"),
            (["wait", "ci", PushedHead, "-Head", "3cee7c2"], "board: -Head goes with wait ci <pr> only"),
            (["status", "45", "-Head", "3cee7c2"], "board: -Head goes with wait ci <pr> only"),
        };

        foreach (var (arguments, message) in cases)
        {
            var result = RunBoard(api, fakeGh, null, arguments, ("FAKE_GH_TOKEN", StubGitHubApi.CliToken));

            result.ExitCode.ShouldBe(2, result.Transcript);
            result.Output.ShouldContain(message);
        }

        api.Requests.ShouldBeEmpty();
    }

    /// <summary>The helper's own help, SKILL.md and reference.md name the expected-head argument, and reference.md says what a wait without it cannot know.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReadFeatureLoopFiles_TheExpectedHeadArgumentOfWaitCiIsDocumented()
    {
        File.ReadAllText(GitHubScriptHost.Script(BoardScript)).ShouldContain("wait ci <pr> [-Head <sha>]");
        File.ReadAllText(GitHubScriptHost.Script(".claude/skills/feature-loop/SKILL.md")).ShouldContain("wait ci <pr> -Head <sha>");
        var reference = File.ReadAllText(GitHubScriptHost.Script(".claude/skills/feature-loop/reference.md"));
        reference.ShouldContain("## Waiting on the right head");
        reference.ShouldContain("wait ci <pr> -Head <sha>");
    }

    /// <summary>The stall watchdog resolves its token the same way: it mints from the key, then reads with the App token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunCheckStalledLanes_OnlyTheKeyVariablesSet_MintsThenReadsWithTheAppToken()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = GitHubScriptHost.Run(
            GitHubScriptHost.Script(WatchdogScript),
            GitHubScriptHost.Environment(api, fakeGh, ("AISF_BOARD_APP_PRIVATE_KEY_PATH", key.Path), ("FAKE_GH_TOKEN", StubGitHubApi.CliToken), (RetiredCredentialGuard.SampleAppsPat, DecoyPat)));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"PASS no stalled work items in {EnvRepo}.");
        api.Requests[0].PathOnly.ShouldBe("/app/installations/166366113/access_tokens");
        api.Requests.Skip(1).ShouldNotBeEmpty();
        api.Requests.Skip(1).ShouldAllBe(request => request.Bearer == StubGitHubApi.AppToken);
        AssertNoSecrets(result, key, api.Requests[0].Bearer);
    }

    /// <summary>The stall watchdog with nothing but the GitHub CLI token uses it, and ignores the retired variable.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_RunCheckStalledLanes_OnlyTheCliToken_UsesItAndIgnoresTheRetiredVariable()
    {
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();

        var result = GitHubScriptHost.Run(
            GitHubScriptHost.Script(WatchdogScript),
            GitHubScriptHost.Environment(api, fakeGh, ("FAKE_GH_TOKEN", StubGitHubApi.CliToken), (RetiredCredentialGuard.SampleAppsPat, DecoyPat)));

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldNotBeEmpty();
        api.Requests.ShouldAllBe(request => request.Bearer == StubGitHubApi.CliToken);
    }

    /// <summary>The scripts, the configuration and the skills name no retired token variable and document the App order.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReadFeatureLoopFiles_NoRetiredVariableAndTheAppOrderIsDocumented()
    {
        foreach (var relative in new[] { BoardScript, WatchdogScript, ".claude/factory-loop.json", ".claude/skills/feature-loop/SKILL.md", ".claude/skills/feature-loop-dispatch/SKILL.md", ".claude/skills/feature-loop/reference.md" })
        {
            var text = File.ReadAllText(GitHubScriptHost.Script(relative));
            text.ShouldNotContain(RetiredCredentialGuard.SampleAppsPat, customMessage: relative);
            text.ShouldNotContain(RetiredCredentialGuard.ProjectsPat, customMessage: relative);
        }

        var config = JsonNode.Parse(File.ReadAllText(GitHubScriptHost.Script(".claude/factory-loop.json")))!;
        config["githubApp"]!["appId"]!.GetValue<long>().ShouldBe(5130401);
        config["githubApp"]!["installationId"]!.GetValue<long>().ShouldBe(166366113);
        config["boardMoves"]!["dispatch"]!["workflowSecrets"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["BOARD_APP_ID", "BOARD_APP_PRIVATE_KEY"]);
        config["helper"]!["tokenOrder"]!.AsArray().Count.ShouldBe(3);
    }

    private static ProcessResult RunBoard(StubGitHubApi api, FakeGhCli fakeGh, TestAppKey? key, string[] arguments, params (string Name, string Value)[] set)
    {
        var variables = new List<(string Name, string Value)>(set) { (RetiredCredentialGuard.SampleAppsPat, DecoyPat) };
        if (key is not null)
        {
            variables.Add(("AISF_BOARD_APP_PRIVATE_KEY_PATH", key.Path));
        }

        return GitHubScriptHost.Run(GitHubScriptHost.Script(BoardScript), GitHubScriptHost.Environment(api, fakeGh, [.. variables]), arguments);
    }

    /// <summary>The lines a wait printed, without the <c>[HH:mm]</c> clock in front of a progress line.</summary>
    private static string[] WaitLines(ProcessResult result) =>
    [
        .. result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Length > 8 && line[0] == '[' && line[6] == ']' ? line[8..] : line),
    ];

    /// <summary>How often the combined status of a commit was read.</summary>
    private static int StatusReads(StubGitHubApi api, string sha) =>
        api.Requests.Count(request => request.PathOnly.EndsWith($"/commits/{sha}/status", StringComparison.Ordinal));

    private static void AssertNoSecrets(ProcessResult result, TestAppKey key, params string[] secrets)
    {
        var transcript = result.Transcript;
        transcript.ShouldNotContain("BEGIN");
        transcript.ShouldNotContain(key.BodyFragment);
        transcript.ShouldNotContain(StubGitHubApi.AppToken);
        transcript.ShouldNotContain(DecoyPat);
        foreach (var secret in secrets.Where(secret => secret.Length > 0))
        {
            transcript.ShouldNotContain(secret);
        }
    }
}
