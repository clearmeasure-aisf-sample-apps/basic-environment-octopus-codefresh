using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-KIT-009 (offline half): conformance-e2e.ps1, the driver of runbook e2e-pass and of an operator's machine, runs
/// the end-to-end pass outside a Codefresh build. It checks its prerequisites by name before any build (the Octopus key,
/// the GitHub token, a .NET 10 SDK), mints a run ID, and runs conformance-run.ps1 from the repository root with
/// TEST_FILTER=FullyQualifiedName~EndToEndTests, passing its exit code on; no secret reaches a command line.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceE2eScriptTests
{
    private const string ApiKey = "octopus-key-for-tests";
    private const string Token = "github-token-for-tests";

    /// <summary>With both secrets and a .NET 10 SDK the driver runs the suite with the end-to-end filter and its exit code.</summary>
    /// <param name="testExitCode">Exit code of dotnet test.</param>
    [TestCase(0)]
    [TestCase(1)]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_SecretsAndSdk_RunsTheSuiteWithTheEndToEndFilter(int testExitCode)
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode).With("TEST_FILTER", "TestCategory=Offline");
        var results = Path.Combine(harness.Root, "results");

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", results);

        result.ExitCode.ShouldBe(testExitCode, result.Transcript);
        var dotnet = harness.Calls("dotnet").Select(call => string.Join(' ', call.Arguments)).ToArray();
        dotnet.Length.ShouldBe(4, result.Transcript);
        dotnet[0].ShouldBe("--list-sdks");
        dotnet[1].ShouldBe("build tests/Platform.Conformance.sln --configuration Release --nologo");
        dotnet[2].ShouldBe($"test tests/Platform.Conformance.sln --configuration Release --no-build --filter FullyQualifiedName~EndToEndTests --logger trx;LogFilePrefix=conformance --logger console;verbosity=normal --results-directory {results}");
        dotnet[3].ShouldMatch(@"^run --project tests/Platform\.Conformance\.Report .* --out " + System.Text.RegularExpressions.Regex.Escape(results) + @" --title conformance r[0-9]{8}t[0-9]{4}-e2e$");
        result.Error.ShouldContain("conformance-e2e: TEST_FILTER 'TestCategory=Offline' replaced by FullyQualifiedName~EndToEndTests");
        result.Error.ShouldContain($"ended with exit code {testExitCode}; summary.md, summary.json and the TRX files are in {results}");
        File.Exists(Path.Combine(results, "summary.md")).ShouldBeTrue(result.Transcript);
        harness.Calls().SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(ApiKey, StringComparison.Ordinal) || argument.Contains(Token, StringComparison.Ordinal));
        result.Output.ShouldNotContain(ApiKey);
        result.Output.ShouldNotContain(Token);
    }

    /// <summary>A given PLATFORM_RUN_ID is kept: the runbook names the run after its task.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_GivenRunId_KeepsIt()
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode: 0).With("PLATFORM_RUN_ID", "r20260928t1200-11910007");

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(0, result.Transcript);
        harness.Calls("dotnet").Last().Arguments[^1].ShouldBe("conformance r20260928t1200-11910007");
    }

    /// <summary>Without the Octopus key or the GitHub token the driver exits 2 before any build, naming only what is missing.</summary>
    /// <param name="key">OCTOPUS_API_KEY, or <c>null</c>.</param>
    /// <param name="token">GITHUB_TOKEN, or <c>null</c>.</param>
    /// <param name="missing">The names in the message.</param>
    [TestCase(null, null, "OCTOPUS_API_KEY, GITHUB_TOKEN")]
    [TestCase(ApiKey, null, "GITHUB_TOKEN")]
    [TestCase(null, Token, "OCTOPUS_API_KEY")]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_SecretMissing_ExitsTwoBeforeAnyBuild(string? key, string? token, string missing)
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode: 0).With("OCTOPUS_API_KEY", key).With("GITHUB_TOKEN", token);

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain($"conformance-e2e: not set: {missing} ");
        harness.Calls("dotnet").ShouldBeEmpty();
        if (key is not null)
        {
            result.Error.ShouldNotContain(key);
        }
    }

    /// <summary>Without a .NET 10 SDK on PATH the driver exits 2 before any build.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_NoDotnetTenSdk_ExitsTwoBeforeAnyBuild()
    {
        using var harness = PlatformScriptHarness.Create("curl", "dotnet").With("OCTOPUS_API_KEY", ApiKey).With("GITHUB_TOKEN", Token);
        harness.Route("dotnet", ["--list-sdks"], "8.0.100 [/usr/share/dotnet/sdk]\n");

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain("lists: 8.0.100); install it (tests/global.json)");
        harness.Calls("dotnet").Select(call => string.Join(' ', call.Arguments)).ShouldBe(["--list-sdks"]);
    }

    // The secrets, a .NET 10 SDK, and stub build, test and report steps that leave a TRX file and the summaries.
    private static PlatformScriptHarness Suite(PlatformScriptHarness harness, int testExitCode)
    {
        harness.With("OCTOPUS_API_KEY", ApiKey).With("GITHUB_TOKEN", Token).With("PLATFORM_RUN_ID", null);
        harness.Route("dotnet", ["--list-sdks"], "10.0.401 [/usr/share/dotnet/sdk]\n");
        harness.Route("dotnet", ["build tests/Platform.Conformance.sln"], "stub: build succeeded\n");
        harness.Route("dotnet", ["test tests/Platform.Conformance.sln"], "stub: tests ran\n", exitCode: testExitCode, run: """
            while [ $# -gt 0 ]; do
              if [ "$1" = --results-directory ]; then mkdir -p "$2" && printf '<TestRun/>\n' >"$2/conformance_1.trx"; fi
              shift
            done
            """);
        harness.Route("dotnet", ["run --project tests/Platform.Conformance.Report"], "stub: report written\n", run: """
            while [ $# -gt 0 ]; do
              if [ "$1" = --out ]; then printf '{"totals": {}}\n' >"$2/summary.json" && printf '# Conformance summary of the stub\n' >"$2/summary.md"; fi
              shift
            done
            """);
        return harness;
    }
}
