using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-005, offline half: step <c>guard</c> of a preview pipeline (<c>scripts/preview-guard.ps1</c>) builds a preview
/// only for a pull request that carries the exact label <c>preview</c> and whose head branch lives in the application
/// repo at the triggering commit, so a fork's commit never becomes a preview image. Labels arrive as a JSON array or a
/// comma-separated list; text that is not valid JSON is read as a list.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class PreviewGuardScriptTests
{
    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("preview-guard.ps1");

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (var script in Scripts())
        {
            yield return new TestCaseData(script, "[\"bug\", \"preview\"]", "pr", true, true).SetArgDisplayNames(script, "json names");
            yield return new TestCaseData(script, "[{\"name\":\"bug\"},{\"name\":\" preview \"}]", "pr", true, true).SetArgDisplayNames(script, "json objects");
            yield return new TestCaseData(script, "docs, preview", "pr", true, true).SetArgDisplayNames(script, "comma list");
            yield return new TestCaseData(script, "[\"Preview\", \"previews\"]", "pr", false, false).SetArgDisplayNames(script, "no exact label");
            yield return new TestCaseData(script, "['preview']", "pr", false, false).SetArgDisplayNames(script, "not json");
            yield return new TestCaseData(script, "[\"preview\"]", "fork", true, false).SetArgDisplayNames(script, "head branch elsewhere");
            yield return new TestCaseData(script, "[\"preview\"]", "missing", true, false).SetArgDisplayNames(script, "no head branch");
        }
    }

    /// <summary>PREVIEW_BUILD is true only for the exact label on a same-repository head; VERSION follows the branch.</summary>
    /// <param name="script">A copy of preview-guard.ps1.</param>
    /// <param name="labels">CF_PULL_REQUEST_LABELS.</param>
    /// <param name="head">pr: the head branch at the triggering commit; fork: the branch points elsewhere; missing: no such branch.</param>
    /// <param name="labelled">Whether the labels hold the exact label preview.</param>
    /// <param name="build">The expected PREVIEW_BUILD.</param>
    [TestCaseSource(nameof(Cases))]
    [Capability("CAP-CF-005")]
    public void Should_RunPreviewGuard_LabelAndHead_DecidePreviewBuild(string script, string labels, string head, bool labelled, bool build)
    {
        using var sandbox = AppScriptSandbox.Create();
        var origin = Path.Combine(sandbox.Root, "origin");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", origin);
        sandbox.Commit(origin, "root");
        sandbox.Git(origin, "checkout", "-q", "-b", "pr");
        var revision = sandbox.Commit(origin, "pull request");
        sandbox.Git(origin, "checkout", "-q", "-b", "fork", "master");
        sandbox.Commit(origin, "same branch name, other commit");
        sandbox.Git(origin, "checkout", "-q", "master");
        var clone = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "clone", "-q", "file://" + origin, clone);
        sandbox.Git(clone, "checkout", "-q", "--detach", revision);
        sandbox.CfExport();
        sandbox.Environment["CF_PULL_REQUEST_LABELS"] = labels;
        sandbox.Environment["CF_PULL_REQUEST_HEAD_BRANCH"] = head;
        sandbox.Environment["CF_PULL_REQUEST_NUMBER"] = "17";
        sandbox.Environment["CF_REVISION"] = revision;
        sandbox.Environment["CF_BRANCH"] = head;

        var run = sandbox.RunIn(clone, script);

        var flag = build ? "true" : "false";
        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Exports.Select(line => line.Split('=')[0]).ShouldBe(["PREVIEW_BUILD", "VERSION", "BUILD_BUILDNUMBER"], run.Transcript);
        run.Export("PREVIEW_BUILD").ShouldBe(flag, run.Transcript);
        run.Export("VERSION").ShouldNotBeNull().ShouldEndWith($"-ci.{revision[..7]}");
        run.Export("BUILD_BUILDNUMBER").ShouldBe(run.Export("VERSION"));
        run.Output.ShouldContain($"PR #17: preview label {(labelled ? "true" : "false")}, same repository {(head == "pr" ? "true" : "false")}, build {flag}");
    }
}
