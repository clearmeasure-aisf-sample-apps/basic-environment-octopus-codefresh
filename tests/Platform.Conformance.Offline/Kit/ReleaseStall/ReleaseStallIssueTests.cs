using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>
/// CAP-KIT-012, the alert: <c>scripts/release/release-stall-check.ps1 -Issues</c> run whole, in a real <c>pwsh</c>, against
/// a stub of the GitHub issues API (the GITHUB_API_URL seam), the way <c>.github/workflows/release-stall-check.yml</c>
/// starts it every hour. A stall gets one issue, a second run adds nothing, a release that moved on gets one comment and
/// its issue is closed, and the workflow's token is the only credential and never printed.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ReleaseStallIssueTests
{
    private const string Repository = "example-org/platform-environment";
    private const string App = "workorders";
    private const string StuckInTdd = $"Release 2.5.771 of {App} has not left tdd";
    private const string StuckInUat = $"Release 2.5.770 of {App} has not left uat";
    private const string BothStalled = "2026-10-06T18:45:00Z";

    private ReleaseStallWorkspace workspace = null!;
    private StubIssueApi api = null!;

    /// <summary>Creates the workspace and starts the stub.</summary>
    [SetUp]
    public void CreateWorkspace()
    {
        workspace = new ReleaseStallWorkspace();
        api = new StubIssueApi();
    }

    /// <summary>Stops the stub and deletes the workspace.</summary>
    [TearDown]
    public void DeleteWorkspace()
    {
        api.Dispose();
        workspace.Dispose();
    }

    /// <summary>Each stall of the recorded history gets one issue whose title names release, app and environment and whose body carries the details; every call carries the workflow's token, which is never printed.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_NewStalls_OpensOneIssueEachWithTheDetails()
    {
        api.Seed("Unrelated work item", "Nothing about releases.");

        var result = Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"OPENED #2 {StuckInTdd}");
        result.Output.ShouldContain($"OPENED #3 {StuckInUat}");
        result.Output.ShouldContain($"PASS release-stall issues: 2 opened, 0 already open, 0 answered earlier, 0 resolved in {Repository}");
        api.Issues.Select(issue => issue.Title).ShouldBe(["Unrelated work item", StuckInTdd, StuckInUat]);
        var body = api.Issues[2].Body;
        body.ShouldContain($"Release **2.5.770** of **{App}** was pinned in **uat** and has not been pinned in **prod**.");
        body.ShouldContain("| Pin commit | c6af0d9ba44fcb307ee4714432dfa8e9b194066d |");
        body.ShouldContain("| Deployment | Deployments-55149 |");
        body.ShouldContain("| Pinned at | 2026-10-06T03:38:11Z |");
        body.ShouldContain("| Waiting when reported | 15h 06m (threshold 90 minutes; age 15h 06m) |");
        body.ShouldContain($"<!-- release-stall app={App} version=2.5.770 environment=uat -->");
        api.Requests.ShouldAllBe(request => request.Bearer == StubIssueApi.Token);
        api.Requests.ShouldAllBe(request => request.PathOnly.StartsWith($"/repos/{Repository}/issues", StringComparison.Ordinal));
        api.Writes.Select(request => request.Method).ShouldBe(["POST", "POST"]);
        JsonNode.Parse(api.Writes[0].Body)!["title"]!.GetValue<string>().ShouldBe(StuckInTdd);
        result.Transcript.ShouldNotContain(StubIssueApi.Token);
    }

    /// <summary>A second and a third run over the same stalls open nothing: an open issue with that exact title is enough, also on a later page of the listing.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_RunAgain_DoesNotOpenADuplicate()
    {
        for (var number = 1; number <= 130; number++)
        {
            api.Seed($"Work item {number}");
        }

        Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled).ExitCode.ShouldBe(0);
        var writes = api.Writes.Count;
        var second = Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled);
        var third = Reconcile(ReleaseStallWorkspace.RecordedLog, "2026-10-06T20:45:00Z");

        writes.ShouldBe(2);
        second.ExitCode.ShouldBe(0, second.Transcript);
        second.Output.ShouldContain($"EXISTS #131 {StuckInTdd}");
        second.Output.ShouldContain($"EXISTS #132 {StuckInUat}");
        second.Output.ShouldContain("0 opened, 2 already open, 0 answered earlier, 0 resolved");
        third.Output.ShouldContain("0 opened, 2 already open");
        api.Writes.Count.ShouldBe(2, "no run after the first one writes anything");
        api.Issues.Count(issue => issue.Title.StartsWith("Release ", StringComparison.Ordinal)).ShouldBe(2);
        api.Requests.ShouldContain(request => request.Path.Contains("state=open", StringComparison.Ordinal) && request.Path.Contains("page=2", StringComparison.Ordinal));
    }

    /// <summary>When a newer release takes the place of the stalled ones and goes through, each stall issue gets one comment saying so and is closed; later runs leave it alone.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_ReleaseReplacedByANewerOne_CommentsOnceAndCloses()
    {
        Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled).ExitCode.ShouldBe(0);

        var result = Reconcile(ReleaseStallWorkspace.RecordedLog, "2026-10-06T22:00:00Z");
        var later = Reconcile(ReleaseStallWorkspace.RecordedLog, "2026-10-07T04:00:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"RESOLVED #1 {StuckInTdd}");
        result.Output.ShouldContain($"RESOLVED #2 {StuckInUat}");
        result.Output.ShouldContain("0 opened, 0 already open, 0 answered earlier, 2 resolved");
        later.Output.ShouldContain("0 opened, 0 already open, 0 answered earlier, 0 resolved");
        api.Issues.ShouldAllBe(issue => issue.State == "closed" && issue.StateReason == "completed" && issue.Comments.Count == 1);
        api.Issues[0].Comments[0].ShouldBe(
            "Resolved: 2.5.772 is now the newest release in tdd (pin 17a313b at 2026-10-06T21:02:29Z, Deployments-55262); 2.5.771 itself was never pinned in uat."
            + "\n\nClosed by the release stall check (`.github/workflows/release-stall-check.yml`).");
        api.Issues[1].Comments[0].ShouldStartWith(
            "Resolved: 2.5.772 is now the newest release in uat (pin 238a9e1 at 2026-10-06T21:15:49Z, Deployments-55270); 2.5.770 itself was never pinned in prod.");
        api.Writes.Select(request => request.Method).ShouldBe(["POST", "POST", "POST", "PATCH", "POST", "PATCH"]);
    }

    /// <summary>When the stalled release itself is pinned in the next environment, bypassed by a newer version there, or rolled back, the comment says which and the issue is closed.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_ReleasePromotedBypassedOrRolledBack_CommentsTheReasonAndCloses()
    {
        workspace.App("shop", "tdd", "uat", "prod").App("store", "tdd", "uat", "prod").App("kiosk", "tdd", "uat", "prod");
        string[] stalled =
        [
            ReleaseStallWorkspace.Pin("2026-09-22T08:00:00Z", "shop", "1.0.0", "tdd", 1),
            ReleaseStallWorkspace.Pin("2026-09-22T08:00:00Z", "kiosk", "0.9.0", "tdd", 2),
            ReleaseStallWorkspace.Pin("2026-09-22T08:05:00Z", "kiosk", "1.0.0", "tdd", 3),
            ReleaseStallWorkspace.Pin("2026-09-22T08:00:00Z", "store", "3.0.0", "tdd", 4),
            ReleaseStallWorkspace.Pin("2026-09-22T08:10:00Z", "store", "3.0.0", "uat", 5),
        ];
        string[] moved =
        [
            ReleaseStallWorkspace.Pin("2026-09-22T13:00:00Z", "shop", "1.0.0", "uat", 6),
            ReleaseStallWorkspace.Pin("2026-09-22T13:01:00Z", "kiosk", "0.9.0", "tdd", 7),
            ReleaseStallWorkspace.Pin("2026-09-22T13:02:00Z", "store", "3.0.1", "prod", 8),
        ];
        Reconcile(workspace.Log(stalled), "2026-09-22T12:00:00Z").ExitCode.ShouldBe(0);
        api.Issues.Select(issue => issue.Title).ShouldBe(
            ["Release 1.0.0 of kiosk has not left tdd", "Release 1.0.0 of shop has not left tdd", "Release 3.0.0 of store has not left uat"]);

        var result = Reconcile(workspace.Log([.. stalled, .. moved]), "2026-09-22T13:10:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Issues.Take(3).ShouldAllBe(issue => issue.State == "closed" && issue.Comments.Count == 1);
        api.Issues[0].Comments[0].ShouldStartWith($"Resolved: tdd was rolled back to 0.9.0 by {ReleaseStallWorkspace.Short(moved[1])} at 2026-09-22T13:01:00Z (Deployments-7).");
        api.Issues[1].Comments[0].ShouldStartWith($"Resolved: 1.0.0 was pinned in uat by {ReleaseStallWorkspace.Short(moved[0])} at 2026-09-22T13:00:00Z (Deployments-6).");
        api.Issues[2].Comments[0].ShouldStartWith($"Resolved: prod already holds the newer version 3.0.1, pinned by {ReleaseStallWorkspace.Short(moved[2])} at 2026-09-22T13:02:00Z (Deployments-8).");
        api.Issues.Count.ShouldBe(3, "the promoted release waits in uat for ten minutes only, which is no stall yet");
    }

    /// <summary>
    /// An issue is kept while its release still waits (also during the prod freeze, when it is no stall), and the script
    /// touches only issues it wrote about releases it knows: a stall-shaped title without the marker, another title, a pull
    /// request, and a marked issue naming a version or an app the pin history does not know are left alone.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_FrozenReleaseAndForeignIssues_AreLeftAlone()
    {
        Reconcile(ReleaseStallWorkspace.RecordedLog, "2026-10-02T12:00:00Z").ExitCode.ShouldBe(0);
        api.Issues.Select(issue => issue.Title).ShouldBe([$"Release 2.5.762 of {App} has not left uat"]);
        api.Seed($"Release 2.5.759 of {App} has not left uat", "Written by a person, without the marker.");
        api.Seed($"Release 2.5.758 of {App} has not left uat", $"<!-- release-stall app={App} version=2.5.758 environment=uat -->", pullRequest: true);
        api.Seed("Release notes of October");
        api.Seed($"Release 9.9.9 of {App} has not left uat", $"A version the history does not know. <!-- release-stall app={App} version=9.9.9 environment=uat -->");
        api.Seed("Release 1.0.0 of @example/team has not left uat", "A title that is no stall title. <!-- release-stall app=@example/team version=1.0.0 environment=uat -->");
        api.Seed("Release 1.0.0 of unknown has not left uat", "An app the history does not know. <!-- release-stall app=unknown version=1.0.0 environment=uat -->");

        var frozen = Reconcile(ReleaseStallWorkspace.RecordedLog, "2026-10-03T06:00:00Z");

        frozen.ExitCode.ShouldBe(0, frozen.Transcript);
        frozen.Output.ShouldContain("PASS release-stall: no stalled release");
        frozen.Output.ShouldContain("0 opened, 0 already open, 0 answered earlier, 0 resolved");
        api.Writes.Count.ShouldBe(1, "only the first run wrote: it opened the issue");
        api.Issues.ShouldAllBe(issue => issue.State == "open" && issue.Comments.Count == 0);
    }

    /// <summary>When an app is no longer checked (here: excluded), its open stall issue is closed with a comment saying so.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_AppNoLongerChecked_CommentsAndCloses()
    {
        workspace.App("sandbox", "tdd", "uat", "prod");
        var log = workspace.Log(ReleaseStallWorkspace.Pin("2026-09-22T10:00:00Z", "sandbox", "0.1.32", "tdd"));
        Reconcile(log, "2026-09-22T12:00:00Z", "-ExcludeApp", string.Empty).ExitCode.ShouldBe(0);
        api.Issues.ShouldHaveSingleItem().Title.ShouldBe("Release 0.1.32 of sandbox has not left tdd");

        var result = Reconcile(log, "2026-09-22T13:00:00Z");

        result.ExitCode.ShouldBe(0, result.Transcript);
        var issue = api.Issues.ShouldHaveSingleItem();
        issue.State.ShouldBe("closed");
        issue.Comments.ShouldHaveSingleItem().ShouldStartWith("Resolved: sandbox is no longer checked (excluded, or it has no environment folder).");
    }

    /// <summary>A stall issue that someone closed after the pin is an answer: it is not opened again. One closed before the pin (an earlier episode) does not count.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_IssueClosedByAPersonAfterThePin_IsNotOpenedAgain()
    {
        api.Seed(StuckInUat, "Closed by a person: known, waiting for a fix.", closedAt: DateTimeOffset.Parse("2026-10-06T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        api.Seed(StuckInTdd, "An earlier episode, closed before this pin.", closedAt: DateTimeOffset.Parse("2026-10-06T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        api.Issues[1].UpdatedAt = DateTimeOffset.Parse("2026-10-06T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var result = Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"ANSWERED #1 {StuckInUat}: closed after the pin, not opened again");
        result.Output.ShouldContain($"OPENED #3 {StuckInTdd}");
        result.Output.ShouldContain("1 opened, 0 already open, 1 answered earlier, 0 resolved");
        api.Writes.Count.ShouldBe(1);
        api.Requests.ShouldContain(request => request.Path.Contains("state=closed&since=2026-10-06T03:38:11Z", StringComparison.Ordinal));
    }

    /// <summary>-DryRun reads and prints what it would open and close, and writes nothing.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_DryRun_PrintsAndWritesNothing()
    {
        api.Seed($"Release 2.5.767 of {App} has not left uat", $"<!-- release-stall app={App} version=2.5.767 environment=uat -->");

        var result = Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled, "-DryRun");

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain($"WOULD OPEN {StuckInTdd}");
        result.Output.ShouldContain($"WOULD OPEN {StuckInUat}");
        result.Output.ShouldContain($"WOULD CLOSE #1 Release 2.5.767 of {App} has not left uat: 2.5.770 is now the newest release in uat");
        result.Output.ShouldContain("(dry run, nothing written)");
        api.Writes.ShouldBeEmpty();
        api.Issues.ShouldHaveSingleItem().State.ShouldBe("open");
    }

    /// <summary>A refused write fails the run with the HTTP status and without the token; without a token or a repository the script stops before any call.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReconcileIssues_RefusedOrMissingCredential_FailsWithoutPrintingTheToken()
    {
        api.RefuseWrites = true;
        var refused = Reconcile(ReleaseStallWorkspace.RecordedLog, BothStalled);
        refused.ExitCode.ShouldBe(1, refused.Transcript);
        refused.Output.ShouldContain($"FAIL release-stall issues: POST repos/{Repository}/issues answered HTTP 403: Resource not accessible by integration");
        refused.Transcript.ShouldNotContain(StubIssueApi.Token);

        var wrongToken = Run([("GITHUB_TOKEN", "not-the-token-0001"), ("GITHUB_REPOSITORY", Repository)], BothStalled);
        wrongToken.ExitCode.ShouldBe(1, wrongToken.Transcript);
        wrongToken.Output.ShouldContain("answered HTTP 401: Bad credentials");
        wrongToken.Transcript.ShouldNotContain("not-the-token-0001");

        var calls = api.Requests.Count;
        var noToken = Run([("GITHUB_REPOSITORY", Repository)], BothStalled);
        noToken.ExitCode.ShouldBe(2, noToken.Transcript);
        noToken.Output.ShouldContain("-Issues needs the token of the workflow in the environment variable GITHUB_TOKEN");
        var noRepository = Run([("GITHUB_TOKEN", StubIssueApi.Token)], BothStalled);
        noRepository.ExitCode.ShouldBe(2, noRepository.Transcript);
        noRepository.Output.ShouldContain("-Issues needs -Repository <owner>/<repo> (or GITHUB_REPOSITORY)");
        api.Requests.Count.ShouldBe(calls, "a usage error makes no call");
    }

    private ProcessResult Reconcile(string log, string now, params string[] arguments) =>
        Run([("GITHUB_TOKEN", StubIssueApi.Token), ("GITHUB_REPOSITORY", Repository)], now, ["-PinLog", log, .. arguments]);

    private ProcessResult Run((string Name, string Value)[] set, string now, params string[] arguments)
    {
        string[] log = arguments.Contains("-PinLog") ? [] : ["-PinLog", ReleaseStallWorkspace.RecordedLog];
        return ReleaseStallWorkspace.Run(null, [("GITHUB_API_URL", api.Url), .. set], ["-Issues", "-Now", now, "-AppsRoot", workspace.AppsRoot, .. log, .. arguments]);
    }
}
