using System.Text.Json.Nodes;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-HARNESS-011 for the pipeline scripts around the report tool: conformance-run.ps1 runs the suite with TEST_FILTER,
/// the report and the build annotations and exits with the test exit code; conformance-publish.ps1 pushes the TRX files
/// and summaries to branch conformance-results and never fails the build.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceResultsScriptTests
{
    private const string BuildId = "6600000000000000000000aa";
    private const string CodefreshKey = "codefresh-key-for-tests";
    private const string Summary = """
        {"totals": {"results": 9, "passed": 6, "failed": 2, "inconclusive": 1},
         "capabilities": [{"id": "CAP-A-001", "status": "pass"}, {"id": "CAP-B-002", "status": "fail"}, {"id": "CAP-C-003", "status": "fail"}]}
        """;

    /// <summary>Failed tests: the report and the annotations still run, and the step exits with dotnet test's code.</summary>
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_TestsFail_ReportsAnnotatesAndExitsWithTheTestExitCode()
    {
        using var harness = Run(PlatformScriptHarness.Create("curl", "dotnet", "cf_export").WithClusters(nonprod: "Running", prod: "Stopped"), testExitCode: 1);
        var results = Path.Combine(harness.Volume, "conformance", BuildId);

        var result = harness.Run("conformance-run.ps1", "-ResultsDirectory", results, "-KeepResults", "10");

        result.ExitCode.ShouldBe(1, result.Transcript);
        harness.Exports.ShouldBe([$"CONFORMANCE_RESULTS_DIR={results}", "PLATFORM_RUN_ID=r1-test"]);
        File.ReadAllText(Path.Combine(results, "power-before.txt")).ShouldBe("nonprod=Running/Succeeded\nprod=Stopped/Succeeded\n");
        harness.Calls("dotnet").Select(call => string.Join(' ', call.Arguments)).ShouldBe(
        [
            "build tests/Platform.Conformance.sln --configuration Release --nologo",
            $"test tests/Platform.Conformance.sln --configuration Release --no-build --filter (TestCategory=Live&TestCategory!=Destructive)|TestCategory=Offline --logger trx;LogFilePrefix=conformance --logger console;verbosity=normal --results-directory {results}",
            $"run --project tests/Platform.Conformance.Report --configuration Release --no-build -- report --trx {results} --assembly tests/Platform.Conformance.Tests/bin/Release/net10.0/Platform.Conformance.Tests.dll --assembly tests/Platform.Conformance.Offline/bin/Release/net10.0/Platform.Conformance.Offline.dll --repo-root . --out {results} --title platform-env/conformance r1-test",
        ]);
        result.Output.ShouldContain("# Conformance summary of the stub");
        var annotations = harness.Calls("curl").Where(call => call.Url == "https://g.codefresh.io/api/annotations").ToArray();
        annotations.Select(call => JsonNode.Parse(call.Body!)!.ToJsonString()).ShouldBe(
        [
            $$"""{"entityType":"build","entityId":"{{BuildId}}","key":"conformance-run-id","value":"r1-test"}""",
            $$"""{"entityType":"build","entityId":"{{BuildId}}","key":"conformance-passed","value":"6"}""",
            $$"""{"entityType":"build","entityId":"{{BuildId}}","key":"conformance-failed","value":"2"}""",
            $$"""{"entityType":"build","entityId":"{{BuildId}}","key":"conformance-inconclusive","value":"1"}""",
            $$"""{"entityType":"build","entityId":"{{BuildId}}","key":"conformance-failed-capabilities","value":"CAP-B-002 CAP-C-003"}""",
        ]);
        annotations.ShouldAllBe(call => call.Method == "POST" && call.Value("--max-time") == "30" && call.Headers.SequenceEqual(new[] { $"Authorization: {CodefreshKey}", "Content-Type: application/json" }));
        annotations.SelectMany(call => call.HeaderFiles).ShouldAllBe(file => file.Private);
        harness.Calls().SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(CodefreshKey, StringComparison.Ordinal));
    }

    /// <summary>-KeepResults 10 keeps the ten newest build folders, this one included, and never touches a folder not named like a build.</summary>
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_KeepResultsTen_DeletesOnlyTheOlderBuildFolders()
    {
        using var harness = Run(PlatformScriptHarness.Create("curl", "dotnet", "cf_export"), testExitCode: 0);
        var parent = Path.Combine(harness.Volume, "conformance");
        var now = DateTime.UtcNow;
        for (var index = 0; index < 12; index++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(parent, $"5f00000000000000000000{index:D2}"));
            Directory.SetLastWriteTimeUtc(folder.FullName, now.AddHours(index - 24));
        }

        Directory.SetLastWriteTimeUtc(Directory.CreateDirectory(Path.Combine(parent, "notes")).FullName, now.AddDays(-30));

        var result = harness.Run("conformance-run.ps1", "-ResultsDirectory", Path.Combine(parent, BuildId), "-KeepResults", "10");

        result.ExitCode.ShouldBe(0, result.Transcript);
        Directory.EnumerateDirectories(parent).Select(Path.GetFileName).ShouldBe(
            [BuildId, .. Enumerable.Range(3, 9).Select(index => $"5f00000000000000000000{index:D2}"), "notes"],
            ignoreOrder: true);
    }

    /// <summary>Without TEST_FILTER the results folder is still exported for publish and teardown; then the step exits 2 before any build.</summary>
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenRun_NoTestFilter_ExportsTheResultsFolderThenExitsTwo()
    {
        using var harness = Run(PlatformScriptHarness.Create("curl", "dotnet", "cf_export"), testExitCode: 0).With("TEST_FILTER", null);
        var results = Path.Combine(harness.Volume, "conformance", BuildId);

        var result = harness.Run("conformance-run.ps1", "-ResultsDirectory", results);

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain("TEST_FILTER is not set");
        harness.Exports.ShouldBe([$"CONFORMANCE_RESULTS_DIR={results}"]);
        Directory.Exists(results).ShouldBeTrue();
        harness.Calls("dotnet").ShouldBeEmpty();
    }

    /// <summary>The first publication creates the orphan branch with a README and the run's folder of TRX files and summaries.</summary>
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenPublish_FirstRun_CreatesTheResultsBranchWithTheRunFolder()
    {
        using var harness = PlatformScriptHarness.Create();
        harness.RecordRealGit();
        var bare = harness.SeedRepository("sandbox", resultsBranch: false);
        harness.With("GITHUB_TOKEN", "github-token-for-tests").With("SANDBOX_APP_REPO", "example-org/platform-sandbox").With("SANDBOX_GIT_URL", bare).With("PLATFORM_RUN_ID", "r1-test");
        var results = Directory.CreateDirectory(Path.Combine(harness.Volume, "conformance", BuildId)).FullName;
        File.WriteAllText(Path.Combine(results, "conformance_1.trx"), "<TestRun/>\n");
        File.WriteAllText(Path.Combine(results, "summary.md"), "# summary\n");
        File.WriteAllText(Path.Combine(results, "summary.json"), "{}\n");
        File.WriteAllText(Path.Combine(results, "power-before.txt"), "nonprod=Stopped/Succeeded\n");
        Directory.CreateDirectory(Path.Combine(results, "artifacts", "run"));
        File.WriteAllText(Path.Combine(results, "artifacts", "run", "extra.trx"), "<TestRun/>\n");
        File.WriteAllText(Path.Combine(results, "artifacts", "task.log"), "log\n");

        var result = harness.Run("conformance-publish.ps1", "-ResultsDirectory", results);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var folder = $"results/{DateTime.UtcNow:yyyy-MM-dd}-r1-test";
        result.Error.ShouldContain($"conformance-publish: results in example-org/platform-sandbox, branch conformance-results, {folder}");
        harness.Git(harness.Root, "--git-dir", bare, "ls-tree", "-r", "--name-only", "conformance-results").Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldBe(
            ["README.md", $"{folder}/conformance_1.trx", $"{folder}/extra.trx", $"{folder}/summary.json", $"{folder}/summary.md"], ignoreOrder: true);
        harness.Git(harness.Root, "--git-dir", bare, "log", "--format=%an %s", "conformance-results").Trim().ShouldBe("platform-conformance conformance r1-test: results");
        harness.Calls("git").Where(call => call.Arguments.Contains("push")).Select(call => string.Join(' ', call.Arguments.Skip(8))).ShouldBe(["push --quiet origin HEAD:refs/heads/conformance-results"]);
    }

    /// <summary>Publishing problems are warnings: no token, no results folder, or a rejected push all exit 0.</summary>
    [Test]
    [Capability("CAP-HARNESS-011")]
    public void WhenPublish_NoTokenOrRejectedPush_WarnsAndExitsZero()
    {
        using var harness = PlatformScriptHarness.Create();
        harness.RecordRealGit();
        var bare = harness.SeedRepository("sandbox", resultsBranch: true);
        harness.Route("git", ["push --quiet origin HEAD:refs/heads/conformance-results"], exitCode: 1, stderr: "remote: rejected\n");
        harness.With("SANDBOX_APP_REPO", "example-org/platform-sandbox").With("SANDBOX_GIT_URL", bare).With("PLATFORM_RUN_ID", "r1-test");
        var results = Directory.CreateDirectory(Path.Combine(harness.Volume, "conformance", BuildId)).FullName;
        File.WriteAllText(Path.Combine(results, "summary.md"), "# summary\n");

        var withoutToken = harness.Run("conformance-publish.ps1", "-ResultsDirectory", results);
        var gitCallsWithoutToken = harness.Calls("git").Count;
        harness.With("GITHUB_TOKEN", "github-token-for-tests");
        var rejected = harness.Run("conformance-publish.ps1", "-ResultsDirectory", results);
        var missingFolder = harness.Run("conformance-publish.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "nowhere"));

        withoutToken.ExitCode.ShouldBe(0, withoutToken.Transcript);
        withoutToken.Error.ShouldContain("WARN nothing published");
        gitCallsWithoutToken.ShouldBe(0);
        rejected.ExitCode.ShouldBe(0, rejected.Transcript);
        rejected.Error.ShouldContain("WARN push to example-org/platform-sandbox branch conformance-results failed (git exit 1)");
        missingFolder.ExitCode.ShouldBe(0, missingFolder.Transcript);
        missingFolder.Error.ShouldContain("WARN no results folder");
    }

    private static PlatformScriptHarness Run(PlatformScriptHarness harness, int testExitCode)
    {
        harness.With("TEST_FILTER", "(TestCategory=Live&TestCategory!=Destructive)|TestCategory=Offline").With("PLATFORM_RUN_ID", "r1-test")
            .With("CF_BUILD_ID", BuildId).With("CF_PIPELINE_NAME", "platform-env/conformance").With("CF_API_KEY", CodefreshKey);
        harness.Route("dotnet", ["build tests/Platform.Conformance.sln"], "stub: build succeeded\n");
        harness.Route("dotnet", ["test tests/Platform.Conformance.sln"], "stub: tests ran\n", exitCode: testExitCode, run: """
            while [ $# -gt 0 ]; do
              if [ "$1" = --results-directory ]; then mkdir -p "$2" && printf '<TestRun/>\n' >"$2/conformance_1.trx"; fi
              shift
            done
            """);
        var summary = Path.Combine(harness.Root, "summary.json");
        File.WriteAllText(summary, Summary);
        harness.Route("dotnet", ["run --project tests/Platform.Conformance.Report"], "stub: report written\n", exitCode: testExitCode, run: $$"""
            while [ $# -gt 0 ]; do
              if [ "$1" = --out ]; then cp '{{summary}}' "$2/summary.json" && printf '# Conformance summary of the stub\n' >"$2/summary.md"; fi
              shift
            done
            """);
        harness.Route("curl", ["https://g.codefresh.io/api/annotations"], "{}");
        return harness;
    }
}
