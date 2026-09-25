using System.Text.Json;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-008, offline half: every copy of <c>scripts/buildinfo.ps1</c> writes the Octopus build information of the
/// handoff (commits HEAD^1..HEAD, so a merge brings the pull request's commits, with their messages byte for byte) and
/// the release notes whose first line <c>app-commit: &lt;sha&gt;</c> ties the Octopus release to the built commit.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class BuildInfoScriptTests
{
    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("buildinfo.ps1");

    /// <summary>A merge commit: the merge and the pull request's commits, newest first, and the release notes.</summary>
    /// <param name="script">A copy of buildinfo.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunBuildInfo_MergeCommit_WritesThePullRequestCommitsAndTheReleaseNotes(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Commit(repository, "root");
        sandbox.Git(repository, "checkout", "-q", "-b", "pr");
        var first = sandbox.Commit(repository, "Fix #1234: quotes \" and \\ backslash\n\nBody\twith tab, ünïcödé\n\n");
        var message = sandbox.Write("crlf.txt", "CRLF message\r\nsecond line\r\n");
        sandbox.Git(repository, "commit", "-q", "--allow-empty", "--cleanup=verbatim", "-F", message);
        var second = sandbox.Git(repository, "rev-parse", "HEAD");
        sandbox.Git(repository, "checkout", "-q", "master");
        sandbox.Git(repository, "merge", "-q", "--no-ff", "-m", "Merge pull request #7", "pr");
        var merge = sandbox.Git(repository, "rev-parse", "HEAD");
        sandbox.Environment["CF_BUILD_ID"] = "build-7";
        sandbox.Environment["CF_BUILD_URL"] = "https://g.codefresh.example.test/build/build-7";
        sandbox.Environment["CF_BRANCH"] = "master";
        sandbox.Environment["CF_REPO_OWNER"] = "example-org";
        sandbox.Environment["CF_REPO_NAME"] = "example-app";

        var run = sandbox.RunIn(repository, script, "-Out", "build/octopus-buildinfo.json", "-ReleaseNotesOut", "build/octopus-release-notes.md");

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Error.ShouldContain("wrote build/octopus-buildinfo.json (3 commit(s), range HEAD^1..HEAD)");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "build", "octopus-buildinfo.json")));
        var root = document.RootElement;
        root.GetProperty("BuildEnvironment").GetString().ShouldBe("Codefresh");
        root.GetProperty("BuildNumber").GetString().ShouldBe("build-7");
        root.GetProperty("BuildUrl").GetString().ShouldBe("https://g.codefresh.example.test/build/build-7");
        root.GetProperty("Branch").GetString().ShouldBe("master");
        root.GetProperty("VcsType").GetString().ShouldBe("Git");
        root.GetProperty("VcsRoot").GetString().ShouldBe("https://github.com/example-org/example-app");
        root.GetProperty("VcsCommitNumber").GetString().ShouldBe(merge);
        var commits = root.GetProperty("Commits").EnumerateArray().Select(commit => (Id: commit.GetProperty("Id").GetString(), Comment: commit.GetProperty("Comment").GetString())).ToArray();
        commits.ShouldBe(
        [
            (merge, "Merge pull request #7"),
            (second, "CRLF message\r\nsecond line\r"),
            (first, "Fix #1234: quotes \" and \\ backslash\n\nBody\twith tab, ünïcödé"),
        ]);
        File.ReadAllText(Path.Combine(repository, "build", "octopus-release-notes.md"))
            .ShouldBe($"app-commit: {merge}\nbuild: https://g.codefresh.example.test/build/build-7\n");
    }

    /// <summary>A root commit lists only itself; without -ReleaseNotesOut and CF_BUILD_URL no notes file is written.</summary>
    /// <param name="script">A copy of buildinfo.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunBuildInfo_RootCommit_ListsOnlyItself(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        var only = sandbox.Commit(repository, "the only commit");
        sandbox.Environment["CF_REPO_OWNER"] = "example-org";
        sandbox.Environment["CF_REPO_NAME"] = "example-app";
        var output = Path.Combine(sandbox.Root, "out", "info.json");

        var run = sandbox.RunIn(repository, script, "-Out", output);

        run.ExitCode.ShouldBe(0, run.Transcript);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        document.RootElement.GetProperty("BuildNumber").GetString().ShouldBe("local");
        document.RootElement.GetProperty("BuildUrl").GetString().ShouldBe(string.Empty);
        document.RootElement.GetProperty("Branch").GetString().ShouldBe("master");
        document.RootElement.GetProperty("Commits").EnumerateArray().Select(commit => commit.GetProperty("Id").GetString()).ShouldBe([only]);
        Directory.GetFiles(Path.Combine(sandbox.Root, "out")).Length.ShouldBe(1);
    }

    /// <summary>
    /// The repository URL comes from CF_REPO_OWNER and CF_REPO_NAME: the starters built from app #1 default to its
    /// repository, the others fail when Codefresh did not set them.
    /// </summary>
    /// <param name="script">A copy of buildinfo.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunBuildInfo_WithoutRepositoryVariables_DefaultsOrFails(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var repository = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", repository);
        sandbox.Commit(repository, "root");
        var output = Path.Combine(sandbox.Root, "info.json");

        var run = sandbox.RunIn(repository, script, "-Out", output);

        if (File.ReadAllText(Path.Combine(AppScriptSandbox.RepositoryRoot, script)).Contains("'20260923-001'", StringComparison.Ordinal))
        {
            run.ExitCode.ShouldBe(0, run.Transcript);
            JsonDocument.Parse(File.ReadAllText(output)).RootElement.GetProperty("VcsRoot").GetString()
                .ShouldBe("https://github.com/clearmeasure-aisf-sample-apps/20260923-001");
        }
        else
        {
            run.ExitCode.ShouldBe(1, run.Transcript);
            run.Error.ShouldContain("CF_REPO_OWNER is not set");
            File.Exists(output).ShouldBeFalse();
        }
    }
}
