using System.Globalization;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>
/// CAP-KIT-013, the alert: <c>scripts/release/release-stall-check.ps1 -Issues -PullRequestBuilds</c> run whole, in a real
/// <c>pwsh</c>, against a stub of the GitHub API (the GITHUB_API_URL seam), the way
/// <c>.github/workflows/release-stall-check.yml</c> starts it every hour. A pull request whose build did not start gets one
/// issue in the repository of the workflow, a second run adds nothing, the issue gets one comment and is closed when the
/// status arrives or the pull request is merged or closed, and the app repository is only ever read.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PullRequestBuildIssueTests
{
    private const string Repository = PullRequestBuildCheckTests.Repository;
    private const string AppRepository = PullRequestBuildCheckTests.AppRepository;
    private const string Day = "2026-10-08";
    private const string AnHourLater = $"{Day}T05:17:00Z";
    private const string AppTitle = $"Build codefresh/ci of pull request {AppRepository}#84 has not started";
    private const string OwnTitle = $"Build codefresh/env-checks of pull request {Repository}#12 has not started";
    private const string AppMarker = $"<!-- build-not-started repository={AppRepository} pull=84 context=codefresh/ci -->";
    private const string OwnMarker = $"<!-- build-not-started repository={Repository} pull=12 context=codefresh/env-checks -->";
    private const string Footer = "\n\nClosed by the release stall check (`.github/workflows/release-stall-check.yml`).";

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

    /// <summary>
    /// Each pull request whose build did not start gets one issue in the repository of the workflow, whose title names the
    /// context and the pull request and whose body carries the head commit, the wait and what to do; nothing of the pull
    /// request's own text is repeated. A second run opens nothing. The app repository is only read, and every call carries
    /// the workflow's token, which is never printed.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_BuildsNotStarted_OpensOneIssueEachWithTheDetailsAndTheRemedy()
    {
        api.Seed("Unrelated work item", "Nothing about builds.");
        PullRequestBuildCheckTests.RecordedPull(api);
        OwnPull();

        var result = Reconcile(AnHourLater);
        var writes = api.Writes.Count;
        var second = Reconcile($"{Day}T06:17:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"OPENED #2 {OwnTitle}");
        result.Output.ShouldContain($"OPENED #3 {AppTitle}");
        result.Output.ShouldContain($"PASS release-stall issues: 0 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository}");
        result.Output.ShouldContain($"PASS build-start issues: 2 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository}");
        api.Issues.Select(issue => issue.Title).ShouldBe(["Unrelated work item", OwnTitle, AppTitle]);
        var body = api.Issues[2].Body;
        body.ShouldContain($"The head commit of pull request {AppRepository}#84 has no **codefresh/ci** status");
        body.ShouldContain($"| Pull request | {AppRepository}#84 |");
        body.ShouldContain("| Branch | `people/acceptance-setup-retries` |");
        body.ShouldContain($"| Head commit | {PullRequestBuildCheckTests.Unbuilt} |");
        body.ShouldContain($"| Committed at | {Day}T04:18:04Z |");
        body.ShouldContain("| Waiting when reported | 58m (threshold 30 minutes) |");
        body.ShouldContain("push an empty commit to `people/acceptance-setup-retries` (`git commit --allow-empty -m \"Start the build\"`, then `git push`), or start the build of that branch in Codefresh");
        body.ShouldContain("docs/runbooks/demo-commit-to-prod.md#if-a-step-stalls");
        body.ShouldEndWith(AppMarker);
        body.ShouldNotContain("Title of pull request");
        api.Issues[1].Body.ShouldEndWith(OwnMarker);

        writes.ShouldBe(2);
        second.ExitCode.ShouldBe(0, second.Transcript);
        second.Output.ShouldContain($"EXISTS #2 {OwnTitle}");
        second.Output.ShouldContain($"EXISTS #3 {AppTitle}");
        second.Output.ShouldContain("PASS build-start issues: 0 opened, 2 already open, 0 answered earlier, 0 resolved");
        api.Writes.Count.ShouldBe(2, "the second run writes nothing");
        api.Writes.ShouldAllBe(request => request.Method == "POST" && request.PathOnly == $"/repos/{Repository}/issues");
        api.Requests.Where(request => request.Path.Contains(AppRepository, StringComparison.Ordinal)).ShouldAllBe(request => request.Method == "GET");
        api.Requests.ShouldAllBe(request => request.Bearer == StubIssueApi.Token);
        (result.Transcript + second.Transcript).ShouldNotContain(StubIssueApi.Token);
    }

    /// <summary>
    /// When the status arrives, on a new head (the empty commit of the recorded case, still pending) or on the same one
    /// (a build started by hand, already failed), the issue gets one comment naming the state and the head and is closed;
    /// later runs leave it alone.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_StatusArrives_CommentsOnceAndCloses()
    {
        var recorded = PullRequestBuildCheckTests.RecordedPull(api);
        var own = OwnPull();
        Reconcile(AnHourLater).ExitCode.ShouldBe(0);
        api.Issues.Select(issue => issue.Title).ShouldBe([OwnTitle, AppTitle]);

        recorded.Head = PullRequestBuildCheckTests.EmptyCommit;
        recorded.CommittedAt = Moment($"{Day}T05:40:18Z");
        recorded.Statuses.Add(("codefresh/ci", "pending"));
        own.Statuses.Add(("codefresh/env-checks", "failure"));
        var result = Reconcile($"{Day}T06:17:00Z");
        var later = Reconcile($"{Day}T07:17:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"RESOLVED #1 {OwnTitle}: codefresh/env-checks reported failure on head 0000000.");
        result.Output.ShouldContain($"RESOLVED #2 {AppTitle}: codefresh/ci reported pending on head 3cee7c2.");
        result.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 2 resolved");
        later.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 0 resolved");
        api.Issues.ShouldAllBe(issue => issue.State == "closed" && issue.StateReason == "completed" && issue.Comments.Count == 1);
        api.Issues[0].Comments[0].ShouldBe("Resolved: codefresh/env-checks reported failure on head 0000000." + Footer);
        api.Issues[1].Comments[0].ShouldBe("Resolved: codefresh/ci reported pending on head 3cee7c2." + Footer);
        api.Writes.Select(request => request.Method).ShouldBe(["POST", "POST", "POST", "PATCH", "POST", "PATCH"]);
    }

    /// <summary>When the pull request is merged, or closed without a merge, its issue gets one comment saying which and is closed.</summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_PullRequestMergedOrClosed_CommentsWhichAndCloses()
    {
        var recorded = PullRequestBuildCheckTests.RecordedPull(api);
        var own = OwnPull();
        Reconcile(AnHourLater).ExitCode.ShouldBe(0);

        recorded.State = "closed";
        recorded.Merged = true;
        own.State = "closed";
        var result = Reconcile($"{Day}T06:17:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 2 resolved");
        api.Issues.ShouldAllBe(issue => issue.State == "closed" && issue.Comments.Count == 1);
        api.Issues[0].Comments[0].ShouldBe($"Resolved: pull request {Repository}#12 was closed without a merge." + Footer);
        api.Issues[1].Comments[0].ShouldBe($"Resolved: pull request {AppRepository}#84 was merged." + Footer);
        api.Requests.ShouldContain(request => request.Method == "GET" && request.Path == $"/repos/{AppRepository}/pulls/84");
    }

    /// <summary>
    /// An issue is kept while the condition has not cleared: a new head that still waits inside the threshold, and a pull
    /// request turned into a draft. The script touches only issues it wrote about pull requests it can judge: a title of
    /// that shape without the marker, a pull request entry, a marked issue naming a repository or a context that is not
    /// checked, and one naming a number that is no pull request are left alone.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_HeadStillWaitingDraftAndForeignIssues_AreLeftAlone()
    {
        var recorded = PullRequestBuildCheckTests.RecordedPull(api);
        OwnPull().Statuses.Add(("codefresh/env-checks", "success"));
        Reconcile(AnHourLater).ExitCode.ShouldBe(0);
        api.Issues.ShouldHaveSingleItem().Title.ShouldBe(AppTitle);
        api.Seed(OwnTitle, "Written by a person, without the marker.");
        api.Seed(OwnTitle, OwnMarker, pullRequest: true);
        api.Seed("Build codefresh/ci of pull request other-org/other#5 has not started", "<!-- build-not-started repository=other-org/other pull=5 context=codefresh/ci -->");
        api.Seed($"Build codefresh/release of pull request {AppRepository}#84 has not started", $"<!-- build-not-started repository={AppRepository} pull=84 context=codefresh/release -->");
        api.Seed($"Build codefresh/ci of pull request {AppRepository}#999 has not started", $"<!-- build-not-started repository={AppRepository} pull=999 context=codefresh/ci -->");
        api.Seed("Build notes of October");

        recorded.Head = PullRequestBuildCheckTests.Sha(85);
        recorded.CommittedAt = Moment($"{Day}T06:00:00Z");
        var waiting = Reconcile($"{Day}T06:17:00Z");
        recorded.Draft = true;
        var draft = Reconcile($"{Day}T09:17:00Z");

        waiting.ExitCode.ShouldBe(0, waiting.Transcript);
        waiting.Output.ShouldContain("PASS build-start: no pull request waits for a build that did not start");
        waiting.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 0 resolved");
        waiting.Output.ShouldContain($"WARN build-start: GET repos/{AppRepository}/pulls/999 answered HTTP 404: Not Found; the issue of {AppRepository}#999 is left alone");
        draft.ExitCode.ShouldBe(0, draft.Transcript);
        draft.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 0 resolved");
        api.Writes.Count.ShouldBe(1, "only the first run wrote: it opened the issue");
        api.Issues.ShouldAllBe(issue => issue.State == "open" && issue.Comments.Count == 0);
        api.Requests.ShouldNotContain(request => request.Path.Contains("other-org", StringComparison.Ordinal));

        recorded.Draft = false;
        var ready = Reconcile($"{Day}T09:17:00Z");
        ready.Output.ShouldContain($"EXISTS #1 {AppTitle}");
        api.Writes.Count.ShouldBe(1, "the issue of the pull request is still the one of the first run");
    }

    /// <summary>
    /// An issue that someone closed after the wait began is an answer: it is not opened again for that head. A new head
    /// that gets no build either is a new wait, and gets a new issue.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_IssueClosedByAPerson_IsNotOpenedAgainUntilANewHeadWaits()
    {
        var recorded = PullRequestBuildCheckTests.RecordedPull(api);
        api.Seed(AppTitle, "Closed by a person: known, the build was started by hand.", closedAt: Moment($"{Day}T04:55:00Z"));

        var answered = Reconcile(AnHourLater);

        answered.ExitCode.ShouldBe(0, answered.Transcript);
        answered.Output.ShouldContain($"ANSWERED #1 {AppTitle}: closed after the wait began, not opened again");
        answered.Output.ShouldContain("PASS build-start issues: 0 opened, 0 already open, 1 answered earlier, 0 resolved");
        api.Writes.ShouldBeEmpty();
        api.Requests.ShouldContain(request => request.Path.Contains($"state=closed&since={Day}T04:18:07Z", StringComparison.Ordinal));

        recorded.Head = PullRequestBuildCheckTests.Sha(85);
        recorded.CommittedAt = Moment($"{Day}T05:40:18Z");
        var again = Reconcile($"{Day}T06:17:00Z");

        again.ExitCode.ShouldBe(0, again.Transcript);
        again.Output.ShouldContain($"OPENED #2 {AppTitle}");
        api.Issues[1].Body.ShouldContain($"| Head commit | {PullRequestBuildCheckTests.Sha(85)} |");
        api.Issues[0].State.ShouldBe("closed");
    }

    /// <summary>
    /// The title names the pull request, not its head, so an issue of the check about an earlier head is no answer for a
    /// later one: after the check closed the issue because the build started, a head that was committed before that close
    /// and gets no build either gets a new issue. An issue of the check about the same head, closed by a person, is an
    /// answer.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_IssueResolvedForAnEarlierHead_IsOpenedAgainWhenALaterHeadWaits()
    {
        var recorded = PullRequestBuildCheckTests.RecordedPull(api);
        Reconcile(AnHourLater).Output.ShouldContain($"OPENED #1 {AppTitle}");
        recorded.Head = PullRequestBuildCheckTests.EmptyCommit;
        recorded.CommittedAt = Moment($"{Day}T05:40:18Z");
        recorded.Statuses.Add(("codefresh/ci", "pending"));
        Reconcile($"{Day}T06:17:00Z").Output.ShouldContain($"RESOLVED #1 {AppTitle}");
        api.Issues[0].ClosedAt!.Value.ShouldBeGreaterThan(Moment($"{Day}T06:15:00Z"), "the stub closes at the time of the test run");

        recorded.Head = PullRequestBuildCheckTests.Sha(86);
        recorded.CommittedAt = Moment($"{Day}T06:15:00Z");
        recorded.Statuses.RemoveAll(status => status.Context == "codefresh/ci");
        var again = Reconcile($"{Day}T07:17:00Z");

        again.ExitCode.ShouldBe(0, again.Transcript);
        again.Output.ShouldContain($"OPENED #2 {AppTitle}");
        again.Output.ShouldContain("PASS build-start issues: 1 opened, 0 already open, 0 answered earlier, 0 resolved");
        api.Issues[1].Body.ShouldContain($"| Head commit | {PullRequestBuildCheckTests.Sha(86)} |");
        api.Issues[0].State.ShouldBe("closed");
        api.Issues[0].Comments.Count.ShouldBe(1);

        api.Issues[1].State = "closed";
        api.Issues[1].ClosedAt = Moment($"{Day}T07:30:00Z");
        api.Issues[1].UpdatedAt = Moment($"{Day}T07:30:00Z");
        var answered = Reconcile($"{Day}T08:17:00Z");

        answered.ExitCode.ShouldBe(0, answered.Transcript);
        answered.Output.ShouldContain($"ANSWERED #2 {AppTitle}: closed after the wait began, not opened again");
        api.Issues.Count.ShouldBe(2);
    }

    /// <summary>-DryRun reads and prints what it would open and close, and writes nothing.</summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_DryRun_PrintsAndWritesNothing()
    {
        PullRequestBuildCheckTests.RecordedPull(api);
        OwnPull().Statuses.Add(("codefresh/env-checks", "success"));
        api.Seed(OwnTitle, OwnMarker);

        var result = Reconcile(AnHourLater, "-DryRun");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"WOULD OPEN {AppTitle}");
        result.Output.ShouldContain($"WOULD CLOSE #1 {OwnTitle}: codefresh/env-checks reported success on head 0000000.");
        result.Output.ShouldContain($"PASS build-start issues: 0 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository} (dry run, nothing written)");
        api.Writes.ShouldBeEmpty();
        api.Issues.ShouldHaveSingleItem().State.ShouldBe("open");
    }

    /// <summary>
    /// A repository that cannot be read fails the run and names it, and its open issue is left alone (nothing is known
    /// about its pull requests). The release stalls and the pull requests of the other repository are reconciled in the
    /// same run all the same.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-013")]
    public void Should_ReconcileBuildIssues_UnreadableRepository_FailsTheRunKeepsItsIssueAndReconcilesTheRest()
    {
        const string now = "2026-10-06T18:45:00Z";
        api.Unreadable.Add(AppRepository);
        api.Seed(AppTitle, AppMarker);
        api.Pull(Repository, 12, PullRequestBuildCheckTests.Sha(12), "2026-10-06T17:00:00Z");

        var result = ReleaseStallWorkspace.Run(null, Credentials, [.. Arguments(now, ReleaseStallWorkspace.RecordedLog)]);

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"FAIL build-start: {AppRepository} could not be read: GET repos/{AppRepository}/pulls?state=open&sort=created&direction=asc&per_page=100&page=1 answered HTTP 404: Not Found");
        result.Output.ShouldContain("OPENED #2 Release 2.5.771 of workorders has not left tdd");
        result.Output.ShouldContain("OPENED #3 Release 2.5.770 of workorders has not left uat");
        result.Output.ShouldContain($"PASS release-stall issues: 2 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository}");
        result.Output.ShouldContain($"OPENED #4 {OwnTitle}");
        result.Output.ShouldContain($"FAIL build-start issues: 1 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository}; not read, their issues left alone: {AppRepository}");
        result.Output.ShouldNotContain("PASS build-start");
        api.Issues[0].State.ShouldBe("open");
        api.Issues[0].Comments.ShouldBeEmpty();
        api.Issues.Count.ShouldBe(4);
        api.Requests.Count(request => request.Path.Contains(AppRepository, StringComparison.Ordinal)).ShouldBe(1, "after the refused listing nothing more is asked about that repository");
        result.Transcript.ShouldNotContain(StubIssueApi.Token);
    }

    private (string Name, string Value)[] Credentials => [("GITHUB_API_URL", api.Url), ("GITHUB_TOKEN", StubIssueApi.Token), ("GITHUB_REPOSITORY", Repository)];

    private static DateTimeOffset Moment(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private StubPull OwnPull() => api.Pull(Repository, 12, PullRequestBuildCheckTests.Sha(12), $"{Day}T03:00:00Z");

    private string[] Arguments(string now, string log) =>
        ["-Issues", "-PullRequestBuilds", "-BuildContext", PullRequestBuildCheckTests.Contexts, "-Now", now, "-PinLog", log, "-AppsRoot", workspace.AppsRoot];

    private ProcessResult Reconcile(string now, params string[] arguments) =>
        ReleaseStallWorkspace.Run(null, Credentials, [.. Arguments(now, noPins), .. arguments]);
}
