using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-KIT-009 (offline half): conformance-e2e.ps1, the driver of runbook e2e-pass and of an operator's machine, runs
/// the end-to-end pass outside a Codefresh build. It checks its prerequisites by name before any build (the Octopus key,
/// the three inputs of the GitHub App aisf-conformance, a .NET 10 SDK), mints a run ID, and runs conformance-run.ps1 from
/// the repository root with TEST_FILTER=FullyQualifiedName~EndToEndTests, passing its exit code on; conformance-run mints
/// the App's installation token for the suite (the chain runs against a stub GitHub API and stub dotnet, the full-system
/// equivalent for a script with no UI); no secret reaches a command line.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceE2eScriptTests
{
    private const string ApiKey = "octopus-key-for-tests";
    private const string Token = StubGitHubApi.AppToken;

    /// <summary>With the Octopus key, the App inputs and a .NET 10 SDK the driver runs the suite with the end-to-end filter and its exit code.</summary>
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
        // The chain: the driver -> conformance-run.ps1 mints the installation token -> the (stub) suite sees it in GITHUB_TOKEN and
        // in the token file, and no private key of the App in its environment.
        File.ReadAllText(Path.Combine(harness.Root, "seen-by-suite")).Trim().ShouldBe($"token={Token} file={Token} key=[] keypath=[]");
        var mint = harness.GitHubApi!.Requests.ShouldHaveSingleItem();
        mint.PathOnly.ShouldBe("/app/installations/777/access_tokens");
        result.Transcript.ShouldNotContain(harness.GitHubKey!.BodyFragment);
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

    /// <summary>
    /// Without the Octopus key or an input of the GitHub App the driver exits 2 before any build, naming only what is missing
    /// (the App not configured is PENDING owner setup); a GITHUB_TOKEN of the environment is no substitute. (Changed for #44:
    /// the prerequisite used to be GITHUB_TOKEN.)
    /// </summary>
    /// <param name="key">OCTOPUS_API_KEY, or <c>null</c>.</param>
    /// <param name="appId">AISF_CONFORMANCE_APP_ID, or <c>null</c>.</param>
    /// <param name="installation">AISF_CONFORMANCE_APP_INSTALLATION_ID, or <c>null</c>.</param>
    /// <param name="privateKey">AISF_CONFORMANCE_APP_PRIVATE_KEY, or <c>null</c>.</param>
    /// <param name="missing">The names in the message.</param>
    [TestCase(null, null, null, null, "OCTOPUS_API_KEY, AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID, AISF_CONFORMANCE_APP_PRIVATE_KEY")]
    [TestCase(ApiKey, null, null, null, "AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID, AISF_CONFORMANCE_APP_PRIVATE_KEY")]
    [TestCase(null, "1", "2", "pem-for-tests", "OCTOPUS_API_KEY")]
    [TestCase(ApiKey, "1", "2", null, "AISF_CONFORMANCE_APP_PRIVATE_KEY")]
    [TestCase(ApiKey, null, "2", "pem-for-tests", "AISF_CONFORMANCE_APP_ID")]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_SecretOrAppInputMissing_ExitsTwoBeforeAnyBuild(string? key, string? appId, string? installation, string? privateKey, string missing)
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode: 0, app: false)
            .With("OCTOPUS_API_KEY", key).With("AISF_CONFORMANCE_APP_ID", appId).With("AISF_CONFORMANCE_APP_INSTALLATION_ID", installation)
            .With("AISF_CONFORMANCE_APP_PRIVATE_KEY", privateKey).With("GITHUB_TOKEN", "stale-pat-for-tests");

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain($"conformance-e2e: not set: {missing} ");
        if (missing.Contains("AISF_CONFORMANCE_APP", StringComparison.Ordinal))
        {
            result.Error.ShouldContain("the GitHub App aisf-conformance is not configured (AISF_CONFORMANCE_APP_ID / _INSTALLATION_ID / _PRIVATE_KEY, or _PRIVATE_KEY_PATH): PENDING owner setup, #44");
        }

        harness.Calls("dotnet").ShouldBeEmpty();
        result.Transcript.ShouldNotContain("stale-pat-for-tests");
        result.Transcript.ShouldNotContain("pem-for-tests");
        if (key is not null)
        {
            result.Error.ShouldNotContain(key);
        }
    }

    /// <summary>An operator's key file (AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH) satisfies the key prerequisite, and the run mints from it.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_KeyFileOfAnOperator_SatisfiesThePrerequisiteAndMints()
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode: 0);

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(0, result.Transcript);
        harness.GitHubApi!.Requests.ShouldHaveSingleItem();
        result.Error.ShouldNotContain("not set:");
    }

    /// <summary>With the App refused at the exchange the run still starts (the harness marks the GitHub tests Inconclusive) and the driver names the status.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_ExchangeRefused_TheSuiteRunsWithoutATokenAndTheStatusIsNamed()
    {
        using var harness = Suite(PlatformScriptHarness.Create("curl", "dotnet"), testExitCode: 1, mintStatus: 403);

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Error.ShouldContain("conformance-github: mint refused (HTTP 403)");
        File.ReadAllText(Path.Combine(harness.Root, "seen-by-suite")).Trim().ShouldBe("token=[] file=[] key=[] keypath=[]");
    }

    /// <summary>Without a .NET 10 SDK on PATH the driver exits 2 before any build.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunConformanceE2e_NoDotnetTenSdk_ExitsTwoBeforeAnyBuild()
    {
        using var harness = PlatformScriptHarness.Create("curl", "dotnet").WithGitHubApp().With("OCTOPUS_API_KEY", ApiKey);
        harness.Route("dotnet", ["--list-sdks"], "8.0.100 [/usr/share/dotnet/sdk]\n");

        var result = harness.Run("conformance-e2e.ps1", "-ResultsDirectory", Path.Combine(harness.Root, "results"));

        result.ExitCode.ShouldBe(2, result.Transcript);
        result.Error.ShouldContain("lists: 8.0.100); install it (tests/global.json)");
        harness.Calls("dotnet").Select(call => string.Join(' ', call.Arguments)).ShouldBe(["--list-sdks"]);
    }

    // The Octopus key, the App inputs (a stub GitHub API and a generated key), a .NET 10 SDK, and stub build, test and report
    // steps that leave a TRX file and the summaries; the stub suite records what its environment holds.
    private static PlatformScriptHarness Suite(PlatformScriptHarness harness, int testExitCode, bool app = true, int mintStatus = 201)
    {
        harness.With("OCTOPUS_API_KEY", ApiKey).With("PLATFORM_RUN_ID", null);
        if (app)
        {
            harness.WithGitHubApp(mintStatus);
        }

        var seen = Path.Combine(harness.Root, "seen-by-suite");
        harness.Route("dotnet", ["--list-sdks"], "10.0.401 [/usr/share/dotnet/sdk]\n");
        harness.Route("dotnet", ["build tests/Platform.Conformance.sln"], "stub: build succeeded\n");
        harness.Route("dotnet", ["test tests/Platform.Conformance.sln"], "stub: tests ran\n", exitCode: testExitCode, run: $$"""
            file=''
            [ -n "$GITHUB_TOKEN_FILE" ] && [ -f "$GITHUB_TOKEN_FILE" ] && file=$(cat "$GITHUB_TOKEN_FILE")
            printf 'token=%s file=%s key=[%s] keypath=[%s]\n' "${GITHUB_TOKEN:-[]}" "${file:-[]}" "$AISF_CONFORMANCE_APP_PRIVATE_KEY" "$AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH" >'{{seen}}'
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
