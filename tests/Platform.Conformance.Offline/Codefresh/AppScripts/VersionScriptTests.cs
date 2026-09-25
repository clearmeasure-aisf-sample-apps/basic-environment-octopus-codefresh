using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-008, offline half: every copy of <c>scripts/version.ps1</c> (the starters and the apps) mints the one version
/// string that names the images, the Octopus packages and the Octopus release: <c>MAJOR.MINOR.&lt;first-parent
/// height&gt;</c> on the release branch and <c>…-ci.&lt;sha7&gt;</c> elsewhere, whatever the tags; a shallow clone is
/// unshallowed or fails; a part above 65534 fails. Real temporary git repositories, no stubs.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class VersionScriptTests
{
    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("version.ps1");

    /// <summary>The release branch counts first-parent commits only; another branch appends -ci and the short commit ID.</summary>
    /// <param name="script">A copy of version.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunVersion_ReleaseAndOtherBranch_PrintFirstParentHeightAndCiSuffix(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = MergedHistory(sandbox);
        var versionFile = sandbox.Write("version.env", "# parts\nMAJOR=3\nMINOR=7\n");
        sandbox.Environment["RELEASE_BRANCH"] = "master";

        sandbox.Environment["CF_BRANCH"] = "master";
        var release = sandbox.RunIn(repository, script, "-VersionFile", versionFile);
        sandbox.Environment["CF_BRANCH"] = "feature/x";
        var branch = sandbox.RunIn(repository, script, "-VersionFile", versionFile);
        sandbox.Environment["CF_BRANCH"] = null;
        sandbox.Git(repository, "checkout", "-q", "--detach", "HEAD");
        var detached = sandbox.RunIn(repository, script, "-VersionFile", versionFile);

        var head = sandbox.Git(repository, "rev-parse", "HEAD");
        release.ExitCode.ShouldBe(0, release.Transcript);
        release.OutputLines.ShouldBe(["3.7.4"], release.Transcript);
        branch.OutputLines.ShouldBe([$"3.7.4-ci.{head[..7]}"], branch.Transcript);
        detached.OutputLines.ShouldBe([$"3.7.4-ci.{head[..7]}"], "detached HEAD is never the release branch: " + detached.Transcript);
    }

    /// <summary>A shallow clone is unshallowed before the count; with -NoFetch it fails instead of minting a wrong version.</summary>
    /// <param name="script">A copy of version.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunVersion_ShallowClone_UnshallowsOrFailsWithNoFetch(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var origin = MergedHistory(sandbox);
        var versionFile = sandbox.Write("version.env", "MAJOR=3\nMINOR=7\n");
        var shallow = Path.Combine(sandbox.Root, "shallow");
        var noFetch = Path.Combine(sandbox.Root, "shallow-nofetch");
        sandbox.Git(sandbox.Root, "clone", "-q", "--depth", "1", "file://" + origin, shallow);
        sandbox.Git(sandbox.Root, "clone", "-q", "--depth", "1", "file://" + origin, noFetch);

        var unshallowed = sandbox.RunIn(shallow, script, "-VersionFile", versionFile, "-Branch", "master");
        var refused = sandbox.RunIn(noFetch, script, "-VersionFile", versionFile, "-Branch", "master", "-NoFetch");

        unshallowed.OutputLines.ShouldBe(["3.7.4"], unshallowed.Transcript);
        unshallowed.Error.ShouldContain("shallow clone; fetching the full history");
        refused.ExitCode.ShouldBe(1, refused.Transcript);
        refused.Output.ShouldBeEmpty();
        refused.Error.ShouldContain("shallow clone: the first-parent count would be wrong");
    }

    /// <summary>MAJOR and MINOR come only from KEY=digits lines, and no part may exceed 65534 (AssemblyVersion).</summary>
    /// <param name="script">A copy of version.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunVersion_InvalidOrOversizedParts_Fail(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = MergedHistory(sandbox);
        var oversized = sandbox.Write("oversized.env", "MAJOR=3\nMINOR=65535\n");
        var notNumeric = sandbox.Write("text.env", "MAJOR=3\nMINOR=seven\n");
        var twice = sandbox.Write("twice.env", "MAJOR=3\nMINOR=7\nMAJOR=4\n");

        var runs = new[] { oversized, notNumeric, twice }.Select(file => sandbox.RunIn(repository, script, "-VersionFile", file, "-Branch", "master")).ToArray();

        runs.ShouldAllBe(run => run.ExitCode == 1 && run.Output.Length == 0);
        runs[0].Error.ShouldContain("version part 65535 exceeds 65534");
        runs[1].Error.ShouldContain("MINOR is missing or not numeric");
        runs[2].Error.ShouldContain("MAJOR is set more than once");
    }

    /// <summary>
    /// master: root, a tag, one commit, a feature branch of two commits merged with --no-ff, one more commit. First-parent
    /// height 4, six commits in all.
    /// </summary>
    private static string MergedHistory(AppScriptSandbox sandbox)
    {
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Commit(repository, "root");
        sandbox.Git(repository, "tag", "v1.0");
        sandbox.Commit(repository, "second");
        sandbox.Git(repository, "checkout", "-q", "-b", "feature/x");
        sandbox.Commit(repository, "feature one");
        sandbox.Commit(repository, "feature two");
        sandbox.Git(repository, "checkout", "-q", "master");
        sandbox.Git(repository, "merge", "-q", "--no-ff", "-m", "Merge pull request #1", "feature/x");
        sandbox.Commit(repository, "after the merge");
        return repository;
    }
}
