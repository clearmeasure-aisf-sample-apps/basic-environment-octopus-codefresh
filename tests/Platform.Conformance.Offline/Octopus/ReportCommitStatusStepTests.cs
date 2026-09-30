using System.Text;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-KIT-010, integration half for the Octopus step Report platform/tdd status (<c>report-commit-status</c> of the workorders
/// process): the real step body runs under the stub Octopus runtime of <see cref="OctopusScriptRunner"/> in a real pwsh with the
/// marked inline copy of <c>scripts/github/GitHubAppAuth.ps1</c>, a throw-away RSA key generated at test time, and a stub GitHub
/// API behind GITHUB_API_URL (the web call cannot be intercepted by a PATH stub); curl, which still posts the status, is the usual
/// PATH stub. Nothing reaches a live system. Like every runner test these are Inconclusive on Windows and run on Linux in env-checks.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class ReportCommitStatusStepTests
{
    private const string Process = ".octopus/apps/workorders/workorders/deployment_process.ocl";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>The exchange is signed by a real RS256 JWT, asks for exactly the one repository and statuses=write, the status is posted, and no key, JWT or token is printed, recorded or written to a temporary file.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatus_RealKey_ExchangesWithAVerifiableJwtNarrowedToOneRepositoryAndPostsTheStatus()
    {
        using var key = new TestAppKey();
        using var runner = new OctopusScriptRunner();
        var api = Prepare(runner);
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = runner.Run(Script, Variables(key.Pem));

        result.Failed.ShouldBeFalse(result.ToString());
        var request = api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.PathOnly.ShouldBe("/app/installations/2/access_tokens");
        key.Verifies(request.Bearer).ShouldBeTrue("the exchange is not authenticated by a JWT signed with the key of the step");
        var parts = request.Bearer.Split('.');
        Decode(parts[0]).ToJsonString().ShouldBe("""{"alg":"RS256","typ":"JWT"}""");
        var claims = Decode(parts[1]);
        claims["iss"]!.GetValue<string>().ShouldBe("1");
        (claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>()).ShouldBe(600);
        claims["iat"]!.GetValue<long>().ShouldBeInRange(started - 60 - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60 + 5);
        JsonNode.Parse(request.Body)!.ToJsonString().ShouldBe("""{"repositories":["workorders"],"permissions":{"statuses":"write"}}""");

        // The JSON payload spans several lines of the stub's call record, so the post is read from the raw record.
        var record = File.ReadAllText(Path.Combine(runner.Root, "calls.tsv"));
        record.ShouldContain($"https://api.github.com/repos/example-org/workorders/statuses/{Commit}");
        record.ShouldContain("\"state\": \"success\"");
        record.ShouldContain("\"context\": \"platform/tdd\"");
        record.ShouldContain("\"description\": \"TDD deployment and acceptance tests passed\"");
        record.Contains("/access_tokens", StringComparison.Ordinal).ShouldBeFalse("the token exchange is no curl call any more");
        result.Log.ShouldContain($"Posted platform/tdd=success to example-org/workorders@{Commit}.");
        AssertNoSecrets(runner, result, key, request.Bearer);
    }

    /// <summary>An empty installation id is discovered with the JWT before the exchange.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatus_NoInstallationId_DiscoversTheInstallationThenExchanges()
    {
        using var key = new TestAppKey();
        using var runner = new OctopusScriptRunner();
        var api = Prepare(runner);
        var variables = Variables(key.Pem);
        variables["GitHub.StatusAppInstallationId"] = string.Empty;

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        api.Requests.Select(request => $"{request.Method} {request.PathOnly}")
            .ShouldBe(["GET /repos/example-org/workorders/installation", "POST /app/installations/4242/access_tokens"]);
        api.Requests.ShouldAllBe(request => key.Verifies(request.Bearer));
        File.ReadAllText(Path.Combine(runner.Root, "calls.tsv")).ShouldContain($"/repos/example-org/workorders/statuses/{Commit}");
    }

    /// <summary>A refused exchange fails the step with the same message as before, logs the App id and the HTTP status only, and posts nothing.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatus_ExchangeRefused_FailsTheStepWithTheAppIdAndStatusOnly()
    {
        using var key = new TestAppKey();
        using var runner = new OctopusScriptRunner();
        var api = Prepare(runner);
        api.MintStatus = 401;

        var result = runner.Run(Script, Variables(key.Pem));

        result.FailMessage.ShouldBe("No installation token for GitHub App 1.");
        result.Log.ShouldContain("GitHub App 1: no installation token (HTTP 401).");
        result.CallsOf("curl").ShouldBeEmpty("no status is posted without a token");
        AssertNoSecrets(runner, result, key, api.Requests.Single().Bearer);
    }

    /// <summary>A key that is no RSA PEM ends in the same Fail-Step, without echoing the key, and never reaches the API.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatus_KeyIsNoPem_FailsTheStepWithoutEchoingTheKey()
    {
        using var runner = new OctopusScriptRunner();
        var api = Prepare(runner);
        // Assembled at run time so that no file of the repository holds a private key block, not even a bogus one.
        var bogusKey = "-----BEGIN " + "PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END " + "PRIVATE KEY-----\n";

        var result = runner.Run(Script, Variables(bogusKey));

        result.FailMessage.ShouldBe("No installation token for GitHub App 1.");
        result.Log.ShouldContain("GitHub App 1: the private key is not a valid RSA PEM key.");
        result.Log.ShouldNotContain("BEGIN");
        result.Log.ShouldNotContain("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo");
        api.Requests.ShouldBeEmpty();
        result.CallsOf("curl").ShouldBeEmpty();
    }

    /// <summary>The empty-key and disabled guards are unchanged: no request is made.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReportCommitStatus_EmptyKeyOrDisabled_StopsBeforeAnyRequest()
    {
        using var runner = new OctopusScriptRunner();
        var api = Prepare(runner);

        var empty = runner.Run(Script, Variables(string.Empty));
        var disabledVariables = Variables("ignored");
        disabledVariables["GitHub.StatusEnabled"] = "False";
        var disabled = runner.Run(Script, disabledVariables);

        empty.FailMessage.ShouldBe("GitHub.StatusEnabled is True but the sensitive variable GitHub.StatusAppPrivateKey is empty.");
        disabled.Failed.ShouldBeFalse(disabled.ToString());
        disabled.Log.ShouldContain("Skipped: GitHub.StatusEnabled is not True");
        api.Requests.ShouldBeEmpty();
        empty.Calls.ShouldBeEmpty();
        disabled.Calls.ShouldBeEmpty();
    }

    private static string Script => OctopusScriptRunner.ScriptBody(Process, "report-commit-status");

    // The stub GitHub API behind GITHUB_API_URL, with the ambient GitHub App and proxy variables removed; curl posts the status.
    private static StubGitHubApi Prepare(OctopusScriptRunner runner)
    {
        var api = runner.Own(new StubGitHubApi());
        runner.Environment["GITHUB_API_URL"] = api.Url;
        runner.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        foreach (var name in new[] { "AISF_BOARD_APP_INSTALLATION_ID", "AISF_BOARD_APP_PRIVATE_KEY", "AISF_BOARD_APP_PRIVATE_KEY_PATH", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY" })
        {
            runner.Environment[name] = null;
        }

        runner.Answer("curl", "/statuses/", new StubAnswer());
        return api;
    }

    private static Dictionary<string, string> Variables(string privateKey) => new(StringComparer.Ordinal)
    {
        ["GitHub.StatusEnabled"] = "True",
        ["GitHub.StatusAppPrivateKey"] = privateKey,
        ["GitHub.StatusAppId"] = "1",
        ["GitHub.StatusAppInstallationId"] = "2",
        ["GitHub.AppRepository"] = "example-org/workorders",
        ["GitHub.StatusContext"] = "platform/tdd",
        ["Octopus.Release.Notes"] = $"app-commit: {Commit}\n",
        ["Octopus.Deployment.Error"] = string.Empty,
        ["Octopus.Web.ServerUri"] = "https://octopus.example.test",
        ["Octopus.Web.DeploymentLink"] = "/app#/deployments/1",
    };

    private static JsonNode Decode(string base64Url) => JsonNode.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(base64Url)))!;

    // Neither the key, a JWT nor the installation token is in the log, in a recorded call or in a temporary file of the step.
    private static void AssertNoSecrets(OctopusScriptRunner runner, OctopusScriptResult result, TestAppKey key, string jwt)
    {
        var calls = Path.Combine(runner.Root, "calls.tsv");
        var recorded = File.Exists(calls) ? File.ReadAllText(calls) : string.Empty;
        foreach (var text in new[] { result.Log, recorded })
        {
            text.ShouldNotContain("BEGIN");
            text.ShouldNotContain(key.BodyFragment);
            text.ShouldNotContain(jwt);
            text.ShouldNotContain(StubGitHubApi.AppToken);
        }

        var temporary = Path.Combine(runner.Root, "tmp");
        Directory.EnumerateFileSystemEntries(temporary, "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("the step leaves no temporary file (no key file, no signature file)");
    }
}
