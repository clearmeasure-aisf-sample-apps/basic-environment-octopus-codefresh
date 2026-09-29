using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half: step <c>prepare</c> of a release (every copy of <c>scripts/ci-tree.ps1</c>) exports
/// <c>CI_TREE_VERIFIED=true</c> only when a commit with a successful <c>codefresh/ci</c> status has the release commit's
/// tree and contains its first parent; the release then skips the static gates that CI already passed on the same files.
/// Everything else fails closed (<c>false</c>: every gate runs). A stub <c>curl</c> answers the GitHub API from files, so
/// the tests never reach GitHub; the origin is a local repository.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class CiTreeScriptTests
{
    private const string Api = "https://api.example.test";
    private const string Repository = "acme/app";

    /// <summary>Answers <c>$CI_TREE_API/&lt;path with / as _&gt;</c> for the last argument (the URL), else fails as curl -f does.</summary>
    private const string Curl = """
        last=""
        for a in "$@"; do last="$a"; done
        path="${last#https://api.example.test/}"
        file="$CI_TREE_API/$(printf '%s' "$path" | tr '/' '_')"
        if [ ! -f "$file" ]; then echo "curl: (22) The requested URL returned error: 404" >&2; exit 22; fi
        cat "$file"
        """;

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("ci-tree.ps1");

    /// <summary>
    /// A merge commit of an up-to-date branch: parent 2 has the tree and a successful codefresh/ci, so the gates are
    /// verified, and the target URL of that status (the CI build) is exported for the release notes.
    /// </summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_MergeOfUpToDateBranchWithGreenCi_ExportsVerified(string script)
    {
        using var sandbox = Sandbox();
        var (clone, head, pullRequestHead) = MergeOfUpToDateBranch(sandbox);
        StatusWithTargets(
            sandbox,
            pullRequestHead,
            ("codefresh/release", "failure", "https://g.codefresh.example.test/build/release-1"),
            ("codefresh/ci", "success", "https://g.codefresh.example.test/build/ci-1"));

        var run = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Export("CI_TREE_VERIFIED").ShouldBe("true", run.Transcript);
        run.Export("CI_TREE_COMMIT").ShouldBe(pullRequestHead);
        run.Export("CI_TREE_URL").ShouldBe("https://g.codefresh.example.test/build/ci-1");
        run.CallsOf("curl").Select(call => call.Arguments[^1]).ShouldBe([$"{Api}/repos/{Repository}/commits/{pullRequestHead}/status"], "the merged parent verifies before any pull request lookup");
        run.CallsOf("cf_export").Select(call => call.ToString()).ShouldBe(["cf_export CI_TREE_VERIFIED", "cf_export CI_TREE_COMMIT", "cf_export CI_TREE_URL"]);
        run.Output.ShouldContain($"release commit {head}");
    }

    /// <summary>The same merge without a successful codefresh/ci on any candidate, or with RELEASE_FULL_GATES=true: every gate runs.</summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_NoGreenCiOrFullGatesForced_ExportsNotVerified(string script)
    {
        using var sandbox = Sandbox();
        var (clone, head, pullRequestHead) = MergeOfUpToDateBranch(sandbox);
        Status(sandbox, pullRequestHead, ("codefresh/ci", "failure"));
        Answer(sandbox, $"repos/{Repository}/commits/{head}/pulls", "[]");

        var red = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);
        Status(sandbox, pullRequestHead, ("codefresh/ci", "success"));
        sandbox.Environment["RELEASE_FULL_GATES"] = "true";
        var forced = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);

        red.ExitCode.ShouldBe(0, red.Transcript);
        red.Export("CI_TREE_VERIFIED").ShouldBe("false", red.Transcript);
        red.Export("CI_TREE_COMMIT").ShouldBe("none");
        red.Export("CI_TREE_URL").ShouldBe("none");
        red.Output.ShouldContain($"{pullRequestHead} (merged parent): codefresh/ci is failure");
        red.Output.ShouldContain($"{head} (the release commit): the commit status API did not answer");
        forced.ExitCode.ShouldBe(0, forced.Transcript);
        forced.Export("CI_TREE_VERIFIED").ShouldBe("false", forced.Transcript);
        forced.CallsOf("curl").ShouldBeEmpty("a forced full gate asks nothing");
    }

    /// <summary>Master moved after CI: the merge tree differs from the pull request head, so nothing is verified.</summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_MasterMovedAfterCi_ExportsNotVerified(string script)
    {
        using var sandbox = Sandbox();
        var origin = Origin(sandbox);
        sandbox.Git(origin, "checkout", "-q", "-b", "feature");
        sandbox.Write(Path.Combine(origin, "src", "feature.cs"), "feature\n");
        var pullRequestHead = sandbox.Commit(origin, "feature");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Write(Path.Combine(origin, "src", "other.cs"), "other\n");
        sandbox.Commit(origin, "another change on master");
        sandbox.Git(origin, "merge", "-q", "--no-ff", "-m", "Merge pull request #2", "feature");
        var clone = Clone(sandbox, origin);
        Status(sandbox, pullRequestHead, ("codefresh/ci", "success"));

        var run = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Export("CI_TREE_VERIFIED").ShouldBe("false", run.Transcript);
        run.Output.ShouldContain($"{pullRequestHead} (merged parent): tree ");
        run.CallsOf("curl").Select(call => call.Arguments[^1]).ShouldNotContain($"{Api}/repos/{Repository}/commits/{pullRequestHead}/status", "a different tree needs no status");
    }

    /// <summary>
    /// The pull request head has the merge tree but does not contain the first parent (the same change landed on master
    /// separately): CI's docs-only diff was not the release's, so nothing is verified.
    /// </summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_SameTreeWithoutTheFirstParent_ExportsNotVerified(string script)
    {
        using var sandbox = Sandbox();
        var origin = Origin(sandbox);
        sandbox.Git(origin, "checkout", "-q", "-b", "feature");
        sandbox.Write(Path.Combine(origin, "src", "same.cs"), "same\n");
        var pullRequestHead = sandbox.Commit(origin, "feature adds same.cs");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Write(Path.Combine(origin, "src", "same.cs"), "same\n");
        sandbox.Commit(origin, "master adds the same file");
        sandbox.Git(origin, "merge", "-q", "--no-ff", "-m", "Merge pull request #3", "feature");
        var clone = Clone(sandbox, origin);
        Status(sandbox, pullRequestHead, ("codefresh/ci", "success"));

        var run = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);

        run.ExitCode.ShouldBe(0, run.Transcript);
        sandbox.Git(clone, "rev-parse", "HEAD^{tree}").ShouldBe(sandbox.Git(clone, "rev-parse", pullRequestHead + "^{tree}"), "the fixture needs equal trees");
        run.Export("CI_TREE_VERIFIED").ShouldBe("false", run.Transcript);
        run.Output.ShouldContain($"{pullRequestHead} (merged parent): does not contain the first parent");
    }

    /// <summary>A squash merge: the pull request head is found through the API, fetched from origin by SHA and verifies the release.</summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_SquashMergeOfGreenPullRequest_FetchesTheHeadAndExportsVerified(string script)
    {
        using var sandbox = Sandbox();
        var origin = Origin(sandbox);
        sandbox.Git(origin, "config", "uploadpack.allowAnySHA1InWant", "true");
        sandbox.Git(origin, "checkout", "-q", "-b", "feature");
        sandbox.Write(Path.Combine(origin, "src", "one.cs"), "one\n");
        sandbox.Commit(origin, "one");
        sandbox.Write(Path.Combine(origin, "src", "two.cs"), "two\n");
        var pullRequestHead = sandbox.Commit(origin, "two");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Git(origin, "merge", "-q", "--squash", "feature");
        var head = sandbox.Commit(origin, "Feature (#4)");
        sandbox.Git(origin, "branch", "-q", "-D", "feature");
        var clone = Clone(sandbox, origin);
        Answer(sandbox, $"repos/{Repository}/commits/{head}/pulls", $$$"""[{"number":4,"merge_commit_sha":"{{{head}}}","head":{"sha":"{{{pullRequestHead}}}"}},{"number":1,"merge_commit_sha":"0000000000000000000000000000000000000000","head":{"sha":"1111111111111111111111111111111111111111"}}]""");
        Status(sandbox, pullRequestHead, ("codefresh/ci", "success"));

        var run = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Export("CI_TREE_VERIFIED").ShouldBe("true", run.Transcript);
        run.Export("CI_TREE_COMMIT").ShouldBe(pullRequestHead);
        run.Export("CI_TREE_URL").ShouldBe("none", "a status without a target URL links no CI build");
        run.CallsOf("curl").Select(call => call.Arguments[^1]).ShouldBe(
            [$"{Api}/repos/{Repository}/commits/{head}/pulls", $"{Api}/repos/{Repository}/commits/{pullRequestHead}/status"],
            "only the pull request merged as this commit is a candidate");
    }

    /// <summary>A failing API, an unreadable answer and a missing repository name all fail closed with exit 0.</summary>
    /// <param name="script">A copy of ci-tree.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunCiTree_ApiFailsOrNoRepository_ExportsNotVerified(string script)
    {
        using var sandbox = Sandbox();
        var (clone, head, pullRequestHead) = MergeOfUpToDateBranch(sandbox);
        Answer(sandbox, $"repos/{Repository}/commits/{pullRequestHead}/status", "not json");
        Answer(sandbox, $"repos/{Repository}/commits/{head}/pulls", "{\"message\":\"API rate limit exceeded\"}");

        var failing = sandbox.RunIn(clone, script, "-Repository", Repository, "-ApiUrl", Api);
        var unnamed = sandbox.RunIn(clone, script, "-ApiUrl", Api);

        failing.ExitCode.ShouldBe(0, failing.Transcript);
        failing.Export("CI_TREE_VERIFIED").ShouldBe("false", failing.Transcript);
        failing.Output.ShouldContain($"{pullRequestHead} (merged parent): the commit status API did not answer");
        unnamed.ExitCode.ShouldBe(0, unnamed.Transcript);
        unnamed.Export("CI_TREE_VERIFIED").ShouldBe("false", unnamed.Transcript);
        unnamed.Output.ShouldContain("no repository (owner/name) given");
        unnamed.CallsOf("curl").ShouldBeEmpty();
    }

    private static AppScriptSandbox Sandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        sandbox.CfExport();
        sandbox.Stub("curl", Curl);
        Directory.CreateDirectory(ApiFolder(sandbox));
        sandbox.Environment["CI_TREE_API"] = ApiFolder(sandbox);
        return sandbox;
    }

    private static string ApiFolder(AppScriptSandbox sandbox) => Path.Combine(sandbox.Root, "api");

    private static void Answer(AppScriptSandbox sandbox, string path, string body) =>
        File.WriteAllText(Path.Combine(ApiFolder(sandbox), path.Replace('/', '_')), body);

    private static void Status(AppScriptSandbox sandbox, string commit, params (string Context, string State)[] statuses)
    {
        var entries = string.Join(',', statuses.Select(status => "{\"context\":\"" + status.Context + "\",\"state\":\"" + status.State + "\"}"));
        Answer(sandbox, $"repos/{Repository}/commits/{commit}/status", "{\"state\":\"pending\",\"sha\":\"" + commit + "\",\"statuses\":[" + entries + "]}");
    }

    private static void StatusWithTargets(AppScriptSandbox sandbox, string commit, params (string Context, string State, string TargetUrl)[] statuses)
    {
        var entries = string.Join(',', statuses.Select(status => "{\"context\":\"" + status.Context + "\",\"state\":\"" + status.State + "\",\"target_url\":\"" + status.TargetUrl + "\"}"));
        Answer(sandbox, $"repos/{Repository}/commits/{commit}/status", "{\"state\":\"pending\",\"sha\":\"" + commit + "\",\"statuses\":[" + entries + "]}");
    }

    /// <summary>An origin on master with one commit.</summary>
    private static string Origin(AppScriptSandbox sandbox)
    {
        var origin = Path.Combine(sandbox.Root, "origin");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", origin);
        sandbox.Write(Path.Combine(origin, "src", "a.cs"), "a\n");
        sandbox.Commit(origin, "root");
        return origin;
    }

    /// <summary>A clone of master only, as the release's main_clone at the merge commit.</summary>
    private static string Clone(AppScriptSandbox sandbox, string origin)
    {
        var clone = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "clone", "-q", "--single-branch", "-b", "master", "file://" + origin, clone);
        return clone;
    }

    /// <summary>master, a branch from its tip, and a no-ff merge of the branch: the merge tree equals the branch head's.</summary>
    private static (string Clone, string Head, string PullRequestHead) MergeOfUpToDateBranch(AppScriptSandbox sandbox)
    {
        var origin = Origin(sandbox);
        sandbox.Git(origin, "checkout", "-q", "-b", "feature");
        sandbox.Write(Path.Combine(origin, "src", "feature.cs"), "feature\n");
        var pullRequestHead = sandbox.Commit(origin, "feature");
        sandbox.Git(origin, "checkout", "-q", "master");
        sandbox.Git(origin, "merge", "-q", "--no-ff", "-m", "Merge pull request #1", "feature");
        var head = sandbox.Git(origin, "rev-parse", "HEAD");
        return (Clone(sandbox, origin), head, pullRequestHead);
    }
}
