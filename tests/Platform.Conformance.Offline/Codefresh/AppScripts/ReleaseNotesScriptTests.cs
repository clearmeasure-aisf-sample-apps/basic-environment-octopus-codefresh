using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-008, offline half: step <c>octopus_release</c> of <c>workorders/release</c> merges a CI summary into the
/// release notes that buildinfo.ps1 wrote (<c>scripts/release-notes.ps1</c>, with <c>trx-summary.ps1 -PassThru</c>).
/// The <c>app-commit:</c> first line stays; the test counts come only from the TRX files of the build, and a gate that
/// exited early because <c>codefresh/ci</c> passed the same tree is named with a link, never counted.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class ReleaseNotesScriptTests
{
    private const string Script = "codefresh/apps/workorders/scripts/release-notes.ps1";
    private const string BuildUrl = "https://g.codefresh.example.test/build/rel-9";
    private const string CiUrl = "https://g.codefresh.example.test/build/ci-8";
    private const string RepoUrl = "https://github.com/example-org/example-app";
    private const string WebDigest = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string WorkerDigest = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>
    /// A release that reused the CI gates: the notes keep buildinfo.ps1's lines, then list the build's own suites, the
    /// gates CI passed with a link to its build, the skipped acceptance and the digests; a second run replaces the summary.
    /// </summary>
    [Test]
    [Capability("CAP-CF-008")]
    public void Should_RunReleaseNotes_CiTreeVerified_MergesTheSummaryAndCountsOnlyThisBuildsTrx()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox, "Merge pull request #42 from example-org/feature");
        var artifacts = Path.Combine(sandbox.Root, "artifacts");
        Trx(sandbox, "artifacts/build_sql/test/UnitTests/unit.trx", total: 10, passed: 9, failed: 0, notExecuted: 1);
        Trx(sandbox, "artifacts/build_sql/test/IntegrationTests/a.trx", total: 5, passed: 5, failed: 0, notExecuted: 0);
        Trx(sandbox, "artifacts/build_sql/test/IntegrationTests/b.trx", total: 3, passed: 2, failed: 0, notExecuted: 1);
        sandbox.Write("artifacts/image-digests.txt", $"acr.example.test/apps/workorders/ui-server@sha256:{WebDigest}\nacr.example.test/apps/workorders/worker@sha256:{WorkerDigest}\n");
        var notes = sandbox.Write(Path.Combine(repository, "build", "octopus-release-notes.md"), $"app-commit: {head}\nbuild: {BuildUrl}\n");
        SetEnvironment(sandbox, head, artifacts);
        sandbox.Environment["CI_TREE_VERIFIED"] = "true";
        sandbox.Environment["CI_TREE_COMMIT"] = new string('c', 40);
        sandbox.Environment["CI_TREE_URL"] = CiUrl;
        sandbox.Environment["RELEASE_ACCEPTANCE"] = null;

        var first = sandbox.RunIn(repository, Script, "-Notes", "build/octopus-release-notes.md");
        var once = File.ReadAllText(notes);
        var second = sandbox.RunIn(repository, Script, "-Notes", "build/octopus-release-notes.md");

        first.ExitCode.ShouldBe(0, first.Transcript);
        second.ExitCode.ShouldBe(0, second.Transcript);
        var ci = $"[ccccccc]({RepoUrl}/commit/{new string('c', 40)})";
        once.ShouldBe(string.Join('\n',
        [
            $"app-commit: {head}",
            $"build: {BuildUrl}",
            string.Empty,
            "### CI summary",
            "- Version: 2.5.9",
            $"- Commit: [{head[..7]}]({RepoUrl}/commit/{head}), PR [#42]({RepoUrl}/pull/42)",
            $"- Codefresh release build: [rel-9]({BuildUrl})",
            $"- CI: codefresh/ci passed the same tree on {ci}, [CI build]({CiUrl})",
            "- Tests run by this build (TRX):",
            "  - Unit (SQL Server): 9 passed, 0 failed, 1 skipped",
            "  - Integration (SQL Server): 7 passed, 0 failed, 1 skipped",
            $"  - SQLite build, code analysis, Qodana, security scan: not re-run; codefresh/ci passed them on {ci}",
            "  - Acceptance: not run in this build (RELEASE_ACCEPTANCE is not true); the tdd deployment runs it",
            "- Images: ui-server@sha256:111111111111, worker@sha256:222222222222",
            "- TRX files: Codefresh pipeline volume, artifacts/rel-9/ (the 10 newest builds are kept); Octopus releases take no attachments",
            string.Empty,
        ]));
        File.ReadAllText(notes).ShouldBe(once, "a second run replaces the summary instead of appending another");
        once.Split('\n').SkipWhile(line => line != "### CI summary").Count(line => line.Length > 0).ShouldBeLessThan(15);
    }

    /// <summary>
    /// Every gate ran in the release (no CI result reused, acceptance on): each suite of each gate gets its own line,
    /// failures count failed and error results, and no line claims a gate ran elsewhere; a squash subject names the PR.
    /// </summary>
    [Test]
    [Capability("CAP-CF-008")]
    public void Should_RunReleaseNotes_EveryGateRanHere_ListsEachSuiteAndNoReusedGate()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox, "Add the thing (#7)");
        var artifacts = Path.Combine(sandbox.Root, "artifacts");
        Trx(sandbox, "artifacts/build_sqlite/test/UnitTests/unit.trx", total: 4, passed: 4, failed: 0, notExecuted: 0);
        Trx(sandbox, "artifacts/build_sql/test/UnitTests/unit.trx", total: 4, passed: 4, failed: 0, notExecuted: 0);
        Trx(sandbox, "artifacts/acceptance/test/AcceptanceTests/acc.trx", total: 6, passed: 3, failed: 1, notExecuted: 1, error: 1);
        sandbox.Write("artifacts/build_sql/test/IntegrationTests/broken.trx", "not xml");
        SetEnvironment(sandbox, head, artifacts);
        sandbox.Environment["CI_TREE_VERIFIED"] = "false";
        sandbox.Environment["CI_TREE_COMMIT"] = "none";
        sandbox.Environment["CI_TREE_URL"] = "none";
        sandbox.Environment["RELEASE_ACCEPTANCE"] = "true";

        var run = sandbox.RunIn(repository, Script, "-Notes", "build/octopus-release-notes.md");

        run.ExitCode.ShouldBe(0, run.Transcript);
        var lines = File.ReadAllLines(Path.Combine(repository, "build", "octopus-release-notes.md"));
        lines[0].ShouldBe("### CI summary", "without an earlier notes file the summary is the whole file");
        lines.ShouldContain($"- Commit: [{head[..7]}]({RepoUrl}/commit/{head}), PR [#7]({RepoUrl}/pull/7)");
        lines.ShouldContain("- CI: no codefresh/ci result reused; every gate ran in this build");
        lines.Where(line => line.StartsWith("  - ", StringComparison.Ordinal)).ShouldBe(
        [
            "  - Unit (SQL Server): 4 passed, 0 failed, 0 skipped",
            "  - Unit (SQLite): 4 passed, 0 failed, 0 skipped",
            "  - Integration (SQL Server): 0 passed, 0 failed, 0 skipped (1 TRX file(s) unreadable)",
            "  - Acceptance (acceptance step): 3 passed, 2 failed, 1 skipped",
        ]);
        lines.ShouldNotContain(line => line.Contains("not re-run", StringComparison.Ordinal) || line.Contains("not run in this build", StringComparison.Ordinal) || line.StartsWith("- Images", StringComparison.Ordinal));
    }

    /// <summary>Without VERSION the step fails and leaves the notes as buildinfo.ps1 wrote them.</summary>
    [Test]
    [Capability("CAP-CF-008")]
    public void Should_RunReleaseNotes_NoVersion_FailsAndKeepsTheNotes()
    {
        using var sandbox = AppScriptSandbox.Create();
        var (repository, head) = Repository(sandbox, "root change");
        var notes = sandbox.Write(Path.Combine(repository, "build", "octopus-release-notes.md"), $"app-commit: {head}\n");
        SetEnvironment(sandbox, head, Path.Combine(sandbox.Root, "artifacts"));
        sandbox.Environment["VERSION"] = null;

        var run = sandbox.RunIn(repository, Script, "-Notes", "build/octopus-release-notes.md");

        run.ExitCode.ShouldBe(1, run.Transcript);
        run.Error.ShouldContain("VERSION is not set");
        File.ReadAllText(notes).ShouldBe($"app-commit: {head}\n");
    }

    private static (string Repository, string Head) Repository(AppScriptSandbox sandbox, string subject)
    {
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Commit(repository, "root");
        return (repository, sandbox.Commit(repository, subject));
    }

    private static void SetEnvironment(AppScriptSandbox sandbox, string head, string artifacts)
    {
        sandbox.Environment["VERSION"] = "2.5.9";
        sandbox.Environment["CF_REVISION"] = head;
        sandbox.Environment["CF_BUILD_URL"] = BuildUrl;
        sandbox.Environment["CF_BUILD_ID"] = "rel-9";
        sandbox.Environment["CF_REPO_OWNER"] = "example-org";
        sandbox.Environment["CF_REPO_NAME"] = "example-app";
        sandbox.Environment["ARTIFACTS_DIR"] = artifacts;
    }

    private static void Trx(AppScriptSandbox sandbox, string path, int total, int passed, int failed, int notExecuted, int error = 0) =>
        sandbox.Write(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results />
              <ResultSummary outcome="Completed">
                <Counters total="{total}" executed="{total - notExecuted}" passed="{passed}" failed="{failed}" error="{error}" timeout="0" aborted="0" notExecuted="{notExecuted}" />
              </ResultSummary>
            </TestRun>
            """);
}
