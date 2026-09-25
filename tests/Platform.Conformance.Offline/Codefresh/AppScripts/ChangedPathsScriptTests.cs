using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half: every copy of <c>scripts/changed-paths.ps1</c> lists what the commit under test changes, so
/// the app's docs-only classifier can skip the gates only for a docs-only change: the first-parent diff on the release
/// branch (a merge brings in the whole pull request), the diff from the merge base with origin/&lt;release branch&gt; on
/// other branches, and a code-like sentinel path (every gate runs) whenever the list cannot be computed; it always exits 0.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class ChangedPathsScriptTests
{
    private const string Sentinel = "changed-paths-unavailable";

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("changed-paths.ps1");

    /// <summary>On the release branch a merge commit lists every path of the pull request; renames list both paths.</summary>
    /// <param name="script">A copy of changed-paths.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunChangedPaths_ReleaseBranchMerge_ListsThePullRequestPaths(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var (_, clone) = Repositories(sandbox);
        sandbox.Environment["CF_BRANCH"] = "master";

        var run = sandbox.RunIn(clone, script);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.OutputLines.ShouldBe(["docs/a.md", "docs/guide.md", "src/a.cs"], run.Transcript);
    }

    /// <summary>On another branch the list is the diff from the merge base with origin/master, fetched first.</summary>
    /// <param name="script">A copy of changed-paths.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunChangedPaths_Branch_ListsTheDiffFromTheMergeBase(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var (origin, clone) = Repositories(sandbox);
        sandbox.Git(origin, "checkout", "-q", "-b", "topic");
        sandbox.Write(Path.Combine(origin, "docs", "topic.md"), "topic\n");
        sandbox.Commit(origin, "topic docs");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Write(Path.Combine(origin, "src", "later.cs"), "later\n");
        sandbox.Commit(origin, "later master commit");
        sandbox.Git(clone, "fetch", "-q", "origin", "topic");
        sandbox.Git(clone, "checkout", "-q", "-b", "topic", "FETCH_HEAD");
        sandbox.Environment["CF_BRANCH"] = "topic";

        var run = sandbox.RunIn(clone, script);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.OutputLines.ShouldBe(["docs/topic.md"], "later master commits are not the branch's changes: " + run.Transcript);
    }

    /// <summary>A root commit, a missing origin branch or no repository at all print the sentinel and exit 0.</summary>
    /// <param name="script">A copy of changed-paths.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunChangedPaths_NothingToCompare_PrintsTheSentinelAndExitsZero(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var root = Path.Combine(sandbox.Root, "root");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", root);
        sandbox.Commit(root, "only commit");
        var plain = Directory.CreateDirectory(Path.Combine(sandbox.Root, "plain")).FullName;

        sandbox.Environment["CF_BRANCH"] = "master";
        var rootCommit = sandbox.RunIn(root, script);
        sandbox.Environment["CF_BRANCH"] = "topic";
        var noOrigin = sandbox.RunIn(root, script);
        var noRepository = sandbox.RunIn(plain, script);

        foreach (var (run, reason) in new[] { (rootCommit, "HEAD has no parent"), (noOrigin, "origin/master is unavailable"), (noRepository, "not inside a git repository") })
        {
            run.ExitCode.ShouldBe(0, run.Transcript);
            run.OutputLines.ShouldBe([Sentinel], run.Transcript);
            run.Error.ShouldContain(reason + "; failing open");
        }
    }

    /// <summary>
    /// origin: master with src/a.cs, then a pull request (a docs file and the rename of src/a.cs to docs/a.md) merged
    /// with --no-ff; clone: a full clone at the merge commit.
    /// </summary>
    private static (string Origin, string Clone) Repositories(AppScriptSandbox sandbox)
    {
        var origin = Path.Combine(sandbox.Root, "origin");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", origin);
        sandbox.Write(Path.Combine(origin, "src", "a.cs"), "a\n");
        sandbox.Write(Path.Combine(origin, "README.md"), "readme\n");
        sandbox.Commit(origin, "root");
        sandbox.Git(origin, "checkout", "-q", "-b", "docs-pr");
        sandbox.Write(Path.Combine(origin, "docs", "guide.md"), "guide\n");
        sandbox.Git(origin, "mv", "src/a.cs", "docs/a.md");
        sandbox.Commit(origin, "docs pull request");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Git(origin, "merge", "-q", "--no-ff", "-m", "Merge pull request #2", "docs-pr");
        var clone = Path.Combine(sandbox.Root, "clone");
        sandbox.Git(sandbox.Root, "clone", "-q", "file://" + origin, clone);
        return (origin, clone);
    }
}
