using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half: step <c>prepare</c> (every copy of <c>scripts/prepare.ps1</c>) hands the gates their inputs
/// through <c>cf_export</c>: the version, the docs-only decision of the app's own classifier (fail open), a masked
/// throwaway SQL password, the per-build artifact folder (the ten newest kept) and one worktree per parallel gate. Values
/// travel in the environment, never on the <c>cf_export</c> command line. A stub <c>cf_export</c> writes
/// <c>env_vars_to_export</c> as Codefresh's does.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public partial class PrepareScriptTests
{
    private const string BuildId = "build-0042";

    private const string Classifier = """
        #!/usr/bin/env bash
        # The app's docs-only classifier, reduced: code=false when every listed path is Markdown or under docs/.
        [ "$1 $2" = "--from-list -" ] || exit 2
        [ -f .github/scripts/fail ] && { echo "code=false"; exit 3; }
        code=false
        while IFS= read -r p; do case "$p" in *.md|docs/*) ;; *) code=true ;; esac; done
        echo "code=$code"
        """;

    private static readonly string[] Worktrees = ["sqlite", "analysis", "qodana", "security", "acceptance"];

    private static IEnumerable<string> GateScripts() => AppScriptSandbox.Copies("prepare.ps1").Where(HasGates);

    private static IEnumerable<string> PlainScripts() => AppScriptSandbox.Copies("prepare.ps1").Where(script => !HasGates(script));

    [GeneratedRegex("^Cf1-[A-Za-z0-9]{24}$")]
    private static partial Regex Password();

    /// <summary>
    /// A docs-only release commit: VERSION and BUILD_BUILDNUMBER, IS_RELEASE=true, CODE_CHANGED=false, a masked password,
    /// ARTIFACTS_DIR; ten build folders are kept and the gate worktrees are recreated.
    /// </summary>
    /// <param name="script">A copy of prepare.ps1 of a starter or app with gates.</param>
    [TestCaseSource(nameof(GateScripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunPrepare_DocsOnlyReleaseCommit_ExportsTheGateInputs(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var clone = AppCheckout(sandbox);
        var artifacts = OldBuildFolders(sandbox, 11);
        Directory.CreateDirectory(Path.Combine(sandbox.Volume, "wt", "sqlite", "stale"));
        sandbox.CfExport();
        sandbox.Environment["CF_BRANCH"] = "master";

        var run = sandbox.RunIn(clone, script, "-Release");

        var version = ExpectedVersion(script, 2);
        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Exports.Select(line => line.Split('=')[0]).ShouldBe(["VERSION", "BUILD_BUILDNUMBER", "IS_RELEASE", "CODE_CHANGED", "CI_SQL_SA_PASSWORD", "ARTIFACTS_DIR"], run.Transcript);
        run.Export("VERSION").ShouldBe(version);
        run.Export("BUILD_BUILDNUMBER").ShouldBe(version);
        run.Export("IS_RELEASE").ShouldBe("true");
        run.Export("CODE_CHANGED").ShouldBe("false");
        run.Export("ARTIFACTS_DIR").ShouldBe($"{sandbox.Volume}/artifacts/{BuildId}");
        var password = run.Export("CI_SQL_SA_PASSWORD").ShouldNotBeNull();
        password.ShouldMatch(Password().ToString());
        run.CallsOf("cf_export").Select(call => call.ToString()).ShouldBe(
            ["cf_export VERSION", "cf_export BUILD_BUILDNUMBER", "cf_export IS_RELEASE", "cf_export CODE_CHANGED", "cf_export CI_SQL_SA_PASSWORD --mask", "cf_export ARTIFACTS_DIR"],
            "names only: the values travel in the environment");
        (run.Output + run.Error).ShouldNotContain(password);
        run.Output.ShouldContain($"Version {version}; code changed: false");
        Directory.GetDirectories(artifacts).Select(folder => new DirectoryInfo(folder).Name).Order(StringComparer.Ordinal).ToArray()
            .ShouldBe([BuildId, .. Enumerable.Range(3, 9).Select(index => $"old{index:00}")], "the ten newest build folders stay");
        Directory.Exists(Path.Combine(sandbox.Volume, ".nuget", "packages")).ShouldBeTrue();
        var listed = sandbox.Git(clone, "worktree", "list", "--porcelain");
        foreach (var worktree in Worktrees)
        {
            listed.ShouldContain($"worktree {Path.Combine(sandbox.Volume, "wt", worktree)}");
        }

        Directory.Exists(Path.Combine(sandbox.Volume, "wt", "sqlite", "stale")).ShouldBeFalse("a stale worktree folder is replaced");
    }

    /// <summary>A branch with a code change, and a failing classifier, both give CODE_CHANGED=true (fail open); ci exports IS_RELEASE=false.</summary>
    /// <param name="script">A copy of prepare.ps1 of a starter or app with gates.</param>
    [TestCaseSource(nameof(GateScripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunPrepare_CodeChangeOrFailingClassifier_ExportsCodeChangedTrue(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var clone = AppCheckout(sandbox);
        sandbox.Git(clone, "checkout", "-q", "-b", "feature");
        sandbox.Write(Path.Combine(clone, "src", "b.cs"), "b\n");
        sandbox.Commit(clone, "code change");
        sandbox.CfExport();
        sandbox.Environment["CF_BRANCH"] = "feature";

        var code = sandbox.RunIn(clone, script);
        sandbox.Git(clone, "reset", "-q", "--hard", "HEAD~1");
        sandbox.Write(Path.Combine(clone, ".github", "scripts", "fail"), "fail\n");
        var failing = sandbox.RunIn(clone, script);

        code.ExitCode.ShouldBe(0, code.Transcript);
        code.Export("IS_RELEASE").ShouldBe("false");
        code.Export("CODE_CHANGED").ShouldBe("true", code.Transcript);
        code.Export("VERSION").ShouldBe($"{ExpectedVersion(script, 3)}-ci.{sandbox.Git(clone, "rev-parse", "HEAD@{1}")[..7]}");
        failing.ExitCode.ShouldBe(0, failing.Transcript);
        failing.Export("CODE_CHANGED").ShouldBe("true", "a failing classifier counts as a code change: " + failing.Transcript);
    }

    /// <summary>Without cf_export the plain values go to env_vars_to_export and the masked password fails the step.</summary>
    /// <param name="script">A copy of prepare.ps1 of a starter or app with gates.</param>
    [TestCaseSource(nameof(GateScripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunPrepare_WithoutCfExport_RefusesToWriteThePassword(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var clone = AppCheckout(sandbox);
        sandbox.WithoutCfExport();
        sandbox.Environment["CF_BRANCH"] = "master";

        var run = sandbox.RunIn(clone, script, "-Release");

        run.ExitCode.ShouldBe(1, run.Transcript);
        run.Error.ShouldContain("cf_export is not on PATH, so the masked variable CI_SQL_SA_PASSWORD cannot be exported");
        run.Exports.Select(line => line.Split('=')[0]).ShouldBe(["VERSION", "BUILD_BUILDNUMBER", "IS_RELEASE", "CODE_CHANGED"], run.Transcript);
    }

    /// <summary>The starters without gates export VERSION and ARTIFACTS_DIR and keep the ten newest build folders.</summary>
    /// <param name="script">A copy of prepare.ps1 of a starter or app without gates.</param>
    [TestCaseSource(nameof(PlainScripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunPrepare_PlainPipeline_ExportsVersionAndArtifactsDir(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var clone = AppCheckout(sandbox);
        var artifacts = OldBuildFolders(sandbox, 12);
        sandbox.CfExport();
        sandbox.Environment["CF_BRANCH"] = "master";
        sandbox.Environment["RELEASE_BRANCH"] = "master";

        var run = sandbox.RunIn(clone, script);

        var version = ExpectedVersion(script, 2);
        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Exports.ShouldBe([$"VERSION={version}", $"ARTIFACTS_DIR={sandbox.Volume}/artifacts/{BuildId}"], run.Transcript);
        run.CallsOf("cf_export").Select(call => call.ToString()).ShouldBe(["cf_export VERSION", "cf_export ARTIFACTS_DIR"]);
        run.OutputLines[^1].ShouldEndWith($" {version}");
        Directory.GetDirectories(artifacts).Length.ShouldBe(10, "the ten newest build folders stay");
        Directory.Exists(Path.Combine(artifacts, BuildId)).ShouldBeTrue();
    }

    private static bool HasGates(string script) =>
        File.Exists(Path.Combine(AppScriptSandbox.RepositoryRoot, Path.GetDirectoryName(script)!, "changed-paths.ps1"));

    /// <summary>MAJOR.MINOR of the version.env next to the copy, and the height.</summary>
    private static string ExpectedVersion(string script, int height)
    {
        var file = Path.Combine(AppScriptSandbox.RepositoryRoot, Path.GetDirectoryName(script)!, "..", "version.env");
        var parts = File.ReadAllLines(file)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] is "MAJOR" or "MINOR")
            .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.Ordinal);
        return $"{parts["MAJOR"]}.{parts["MINOR"]}.{height}";
    }

    /// <summary>An app checkout of master (root, then a docs-only commit) with the classifier, cloned from an origin.</summary>
    private static string AppCheckout(AppScriptSandbox sandbox)
    {
        var origin = Path.Combine(sandbox.Root, "origin");
        sandbox.Git(sandbox.Root, "init", "-q", "-b", "master", origin);
        sandbox.Write(Path.Combine(origin, "src", "a.cs"), "a\n");
        sandbox.Write(Path.Combine(origin, ".github", "scripts", "detect-code-changes.sh"), Classifier.ReplaceLineEndings("\n") + "\n");
        sandbox.Commit(origin, "root");
        sandbox.Write(Path.Combine(origin, "docs", "notes.md"), "notes\n");
        sandbox.Commit(origin, "docs only");
        var clone = Path.Combine(sandbox.Root, "app");
        sandbox.Git(sandbox.Root, "clone", "-q", "file://" + origin, clone);
        sandbox.Environment["CF_BUILD_ID"] = BuildId;
        return clone;
    }

    /// <summary>Build folders old01..oldNN under the volume, each a day newer than the one before.</summary>
    private static string OldBuildFolders(AppScriptSandbox sandbox, int count)
    {
        var artifacts = Path.Combine(sandbox.Volume, "artifacts");
        for (var index = 1; index <= count; index++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(artifacts, $"old{index:00}"));
            folder.LastWriteTimeUtc = new DateTime(2026, 9, index, 10, 0, 0, DateTimeKind.Utc);
        }

        return artifacts;
    }
}
