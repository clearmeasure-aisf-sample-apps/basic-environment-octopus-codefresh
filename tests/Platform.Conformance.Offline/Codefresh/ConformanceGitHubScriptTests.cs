using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-KIT-010 (unit half of the conformance App, #44): <c>codefresh/platform/scripts/conformance-github.ps1</c>, the
/// wrapper every conformance script dot-sources to mint the installation token of the GitHub App <c>aisf-conformance</c>.
/// Each test dot-sources it in a real <c>pwsh</c> against the stub GitHub API (<c>GITHUB_API_URL</c>) with a key generated at
/// test time; no live call is made. The transcripts are checked for the key, the JWT and the token.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceGitHubScriptTests
{
    private const string Sandbox = "example-org/platform-sandbox";
    private string scratch = null!;

    /// <summary>Creates the folder for the snippet scripts and token files.</summary>
    [SetUp]
    public void CreateScratch() => scratch = Directory.CreateTempSubdirectory("conformance-github-").FullName;

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void DeleteScratch() => Directory.Delete(scratch, recursive: true);

    /// <summary>github-app.json names the App, the two fixed repositories, exactly the four approved permissions and the 45-minute refresh.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReadGitHubAppJson_TheApprovedRepositoriesAndPermissions()
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, "codefresh", "platform", "github-app.json")))!.AsObject();

        config["name"]!.GetValue<string>().ShouldBe("aisf-conformance");
        config["environmentPrefix"]!.GetValue<string>().ShouldBe("AISF_CONFORMANCE_APP");
        config["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(
            ["clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh", "clearmeasure-aisf-sample-apps/20260923-001"]);
        config["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal).ShouldBe(
            new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["statuses"] = "read", ["metadata"] = "read" }, ignoreOrder: true);
        config["refreshMinutes"]!.GetValue<int>().ShouldBe(45);
        config["runtimeRepositories"]!.GetValue<string>().ShouldContain("three repositories");
        var contracts = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, "contracts", "platform-contracts.yaml"));
        contracts.ShouldContain("permissions: {contents: write, pull_requests: write, statuses: read, metadata: read}", Case.Sensitive);
        contracts.ShouldContain("config: codefresh/platform/github-app.json", Case.Sensitive);
    }

    /// <summary>
    /// The mint is narrowed to the environment repository, 20260923-001, the sandbox repository and PLATFORM_E2E_REPO (each
    /// once; a placeholder is ignored) and exactly the four permissions; GITHUB_TOKEN is set in the process only, with no token
    /// file unless one is asked for, and nothing secret is printed.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_InitializeConformanceGitHubToken_App_MintsTheNarrowedTokenIntoTheProcessOnly()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();

        var result = Run("$r = Initialize-ConformanceGitHubToken; Write-Output \"state=$($r.State) token=$($env:GITHUB_TOKEN -ceq '" + StubGitHubApi.AppToken + "') file=[$($env:GITHUB_TOKEN_FILE)]\"",
            api, key, ("SANDBOX_APP_REPO", Sandbox), ("PLATFORM_E2E_REPO", "clearmeasure-aisf-sample-apps/20260923-001"), ("GITHUB_TOKEN", "stale-pat"));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("state=app token=True file=[]");
        var request = api.Requests.ShouldHaveSingleItem();
        request.PathOnly.ShouldBe("/app/installations/777/access_tokens");
        key.Verifies(request.Bearer).ShouldBeTrue();
        var body = JsonNode.Parse(request.Body)!.AsObject();
        body["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["basic-environment-octopus-codefresh", "20260923-001", "platform-sandbox"]);
        body["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal).ShouldBe(
            new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["statuses"] = "read", ["metadata"] = "read" }, ignoreOrder: true);
        result.Error.ShouldBeEmpty();
        NoSecrets(result, key, request.Bearer, "stale-pat");
    }

    /// <summary>A placeholder SANDBOX_APP_REPO is not a repository of the token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetConformanceGitHubRepositories_PlaceholderSandbox_IsIgnored()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();

        var result = Run("(Get-ConformanceGitHubRepositoryList) -join ','", api, key, ("SANDBOX_APP_REPO", "<sandbox-app-repo>"));

        result.Output.Trim().ShouldBe("clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh,clearmeasure-aisf-sample-apps/20260923-001");
    }

    /// <summary>
    /// With -TokenFile the token is also written to the file named by GITHUB_TOKEN_FILE (a temporary one when it is not set),
    /// readable only by its owner; a re-mint replaces its content atomically (no temporary file is left) and the process
    /// GITHUB_TOKEN follows.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_UpdateConformanceGitHubToken_TokenFile_IsRewrittenAtomicallyWithTheNewTokenAndIsPrivate()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { NumberedTokens = true };
        var file = Path.Combine(scratch, "github.token");
        var snippet = $$"""
            $first = Initialize-ConformanceGitHubToken -TokenFile
            "first=$($first.State) file=$([System.IO.File]::ReadAllText($env:GITHUB_TOKEN_FILE)) env=$($env:GITHUB_TOKEN)"
            if (-not $IsWindows) { "mode=$([Convert]::ToString([int][System.IO.File]::GetUnixFileMode($env:GITHUB_TOKEN_FILE), 8))" }
            $second = Update-ConformanceGitHubToken
            "second=$($second.State) file=$([System.IO.File]::ReadAllText($env:GITHUB_TOKEN_FILE)) env=$($env:GITHUB_TOKEN)"
            if (-not $IsWindows) { "mode=$([Convert]::ToString([int][System.IO.File]::GetUnixFileMode($env:GITHUB_TOKEN_FILE), 8))" }
            "leftovers=$(@(Get-ChildItem -LiteralPath '{{scratch}}' -Filter '*.tmp').Count)"
            """;

        var result = Run(snippet, api, key, ("GITHUB_TOKEN_FILE", file));

        result.ExitCode.ShouldBe(0, result.Transcript);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldContain($"first=app file={api.MintedToken(1)} env={api.MintedToken(1)}");
        lines.ShouldContain($"second=app file={api.MintedToken(2)} env={api.MintedToken(2)}");
        lines.ShouldContain("leftovers=0");
        if (!OperatingSystem.IsWindows())
        {
            lines.Where(line => line.StartsWith("mode=", StringComparison.Ordinal)).ShouldBe(["mode=600", "mode=600"]);
        }

        File.ReadAllText(file).ShouldBe(api.MintedToken(2));
        api.Requests.Count.ShouldBe(2);
        result.Error.ShouldBeEmpty();
        result.Transcript.ShouldNotContain("BEGIN");
        result.Transcript.ShouldNotContain(key.BodyFragment);
    }

    /// <summary>Without the App inputs: the PENDING message once on standard error, no request, and neither GITHUB_TOKEN nor the token file survives (no fallback to a token of the environment).</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_InitializeConformanceGitHubToken_AppNotConfigured_IsPendingWithoutAnyRequestOrFallback()
    {
        using var api = new StubGitHubApi();

        var result = RunWithoutApp("$r = Initialize-ConformanceGitHubToken -TokenFile; Write-Output \"state=$($r.State) reason=$($r.Reason) token=[$($env:GITHUB_TOKEN)] file=[$($env:GITHUB_TOKEN_FILE)]\"",
            api, ("GITHUB_TOKEN", "stale-pat"), ("GH_TOKEN", "gh-token"), ("GITHUB_TOKEN_FILE", Path.Combine(scratch, "stale.token")));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("state=unset reason=not set: AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID, AISF_CONFORMANCE_APP_PRIVATE_KEY token=[] file=[]");
        result.Error.Trim().ShouldBe("conformance-github: GitHub App aisf-conformance is not configured (AISF_CONFORMANCE_APP_ID / _INSTALLATION_ID / _PRIVATE_KEY): PENDING owner setup, #44");
        api.Requests.ShouldBeEmpty();
        File.Exists(Path.Combine(scratch, "stale.token")).ShouldBeFalse();
    }

    /// <summary>A refused exchange names the HTTP status only, drops any token of the environment and exits normally for the caller to decide.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_InitializeConformanceGitHubToken_MintRefused_ReportsTheStatusOnlyAndHoldsNoToken()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { MintStatus = 401 };

        var result = Run("$r = Initialize-ConformanceGitHubToken -TokenFile; Write-Output \"state=$($r.State) reason=$($r.Reason) token=[$($env:GITHUB_TOKEN)] file=[$($env:GITHUB_TOKEN_FILE)]\"",
            api, key, ("GITHUB_TOKEN", "stale-pat"));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("state=failed reason=HTTP 401 token=[] file=[]");
        result.Error.Trim().ShouldBe("conformance-github: mint refused (HTTP 401)");
        NoSecrets(result, key, api.Requests[0].Bearer, "stale-pat");
    }

    /// <summary>A failed re-mint keeps the previous token (still valid for a while) and says so, without a secret.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_UpdateConformanceGitHubToken_RemintRefused_KeepsThePreviousToken()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var file = Path.Combine(scratch, "github.token");

        // The first run mints and writes the file; the stub then refuses, and a second run re-mints.
        var first = Run("$null = Initialize-ConformanceGitHubToken -TokenFile", api, key, ("GITHUB_TOKEN_FILE", file));
        first.ExitCode.ShouldBe(0, first.Transcript);
        api.MintStatus = 403;
        var second = Run("$env:GITHUB_TOKEN = 'previous-token-value'; $r = Update-ConformanceGitHubToken; Write-Output \"state=$($r.State) kept=$($env:GITHUB_TOKEN)\"", api, key, ("GITHUB_TOKEN_FILE", file));

        second.ExitCode.ShouldBe(0, second.Transcript);
        second.Output.Trim().ShouldBe("state=failed kept=previous-token-value");
        second.Error.Trim().ShouldBe("conformance-github: re-mint failed (HTTP 403); the previous token stays in use until it expires");
        File.ReadAllText(file).ShouldBe(StubGitHubApi.AppToken, "the file keeps the last good token");
        second.Transcript.ShouldNotContain(key.BodyFragment);
    }

    /// <summary>Update-...IfStale mints again only when the token is older than refreshMinutes.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_UpdateConformanceGitHubTokenIfStale_OnlyAfterTheRefreshInterval()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { NumberedTokens = true };
        var snippet = """
            $null = Initialize-ConformanceGitHubToken
            Update-ConformanceGitHubTokenIfStale
            "fresh=$($env:GITHUB_TOKEN)"
            $script:ConformanceTokenMintedAt = [DateTime]::UtcNow.AddMinutes(-46)
            Update-ConformanceGitHubTokenIfStale
            "stale=$($env:GITHUB_TOKEN)"
            """;

        var result = Run(snippet, api, key);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldBe([$"fresh={api.MintedToken(1)}", $"stale={api.MintedToken(2)}"]);
        api.Requests.Count.ShouldBe(2);
    }

    /// <summary>Test-ConformanceGitHubAppInput lists the missing inputs by name; a key file counts as the key.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_TestConformanceGitHubAppInput_ListsWhatIsMissingByName()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();

        var none = RunWithoutApp("(Test-ConformanceGitHubAppInput) -join ','", api);
        var keyFileOnly = RunWithoutApp("(Test-ConformanceGitHubAppInput) -join ','", api, ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path));
        var complete = RunWithoutApp("@(Test-ConformanceGitHubAppInput).Count", api, ("AISF_CONFORMANCE_APP_ID", "1"), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "2"), ("AISF_CONFORMANCE_APP_PRIVATE_KEY", key.Pem));

        none.Output.Trim().ShouldBe("AISF_CONFORMANCE_APP_ID,AISF_CONFORMANCE_APP_INSTALLATION_ID,AISF_CONFORMANCE_APP_PRIVATE_KEY");
        keyFileOnly.Output.Trim().ShouldBe("AISF_CONFORMANCE_APP_ID,AISF_CONFORMANCE_APP_INSTALLATION_ID");
        complete.Output.Trim().ShouldBe("0");
    }

    private static void NoSecrets(ProcessResult result, TestAppKey key, params string[] secrets)
    {
        result.Transcript.ShouldNotContain("BEGIN");
        result.Transcript.ShouldNotContain(key.BodyFragment);
        result.Transcript.ShouldNotContain(StubGitHubApi.AppToken);
        foreach (var secret in secrets.Where(secret => secret.Length > 0))
        {
            result.Transcript.ShouldNotContain(secret);
        }
    }

    private ProcessResult Run(string body, StubGitHubApi api, TestAppKey key, params (string Name, string Value)[] set) =>
        Snippet(body, api, [("AISF_CONFORMANCE_APP_ID", "5130402"), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "777"), ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path), .. set]);

    private ProcessResult RunWithoutApp(string body, StubGitHubApi api, params (string Name, string Value)[] set) => Snippet(body, api, set);

    private ProcessResult Snippet(string body, StubGitHubApi api, (string Name, string Value)[] set)
    {
        var script = Path.Combine(scratch, $"snippet-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, $"$ErrorActionPreference = 'Stop'\n. '{GitHubScriptHost.Script("codefresh/platform/scripts/conformance-github.ps1")}'\n{body}\n");
        return GitHubScriptHost.Run(script, GitHubScriptHost.Environment(api, null, set));
    }
}
