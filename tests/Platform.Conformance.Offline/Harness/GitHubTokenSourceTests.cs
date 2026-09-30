using System.Net;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>
/// The GitHub token of the harness in a suite longer than one hour (#44): a GitHub App installation token lives one hour,
/// so the pipeline scripts re-mint it and rewrite the file named by <c>GITHUB_TOKEN_FILE</c>, and every request asks
/// <see cref="GitHubTokenSource"/> for the token. These tests prove that a token rewritten while the harness runs reaches
/// the next request (through <see cref="GitHubApi"/>, <see cref="BearerTokenHandler"/> and the per-request readers of the
/// suite) without a restart, that <c>GITHUB_TOKEN</c> remains the one-shot fallback, and that no value is ever printed.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class GitHubTokenSourceTests
{
    private string folder = null!;
    private string tokenFile = null!;

    /// <summary>Creates the folder of the token file.</summary>
    [SetUp]
    public void CreateFolder()
    {
        folder = Directory.CreateTempSubdirectory("github-token-source-").FullName;
        tokenFile = Path.Combine(folder, "github.token");
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void DeleteFolder() => Directory.Delete(folder, recursive: true);

    /// <summary>The file wins over <c>GITHUB_TOKEN</c>, is trimmed, and a rewrite is picked up on the next read.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenCurrent_TokenFileRewritten_ReturnsTheNewTokenOnTheNextRead()
    {
        File.WriteAllText(tokenFile, "<stub-token-one>\n");
        var source = new GitHubTokenSource("<stub-token-start>", tokenFile);

        var first = source.Current;
        File.WriteAllText(tokenFile, "<stub-token-two>");
        var second = source.Current;

        first.ShouldBe("<stub-token-one>");
        second.ShouldBe("<stub-token-two>");
    }

    /// <summary>Without the file, or with an empty or missing one (it is being replaced), <c>GITHUB_TOKEN</c> is used; with neither there is no token.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenCurrent_NoUsableFile_FallsBackToTheTokenReadAtStartAndElseNull()
    {
        var withFallback = new GitHubTokenSource("<stub-token-start>", tokenFile);
        var withoutAnything = new GitHubTokenSource("  ", "  ");

        var missingFile = withFallback.Current;
        File.WriteAllText(tokenFile, " \n");
        var emptyFile = withFallback.Current;

        missingFile.ShouldBe("<stub-token-start>");
        emptyFile.ShouldBe("<stub-token-start>");
        withoutAnything.Current.ShouldBeNull();
        new GitHubTokenSource("<stub-token-start>", null).Current.ShouldBe("<stub-token-start>");
    }

    /// <summary>The description says whether a token exists, never its value.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenToString_TokenSet_NeverPrintsTheValue()
    {
        File.WriteAllText(tokenFile, "<stub-token-one>");

        new GitHubTokenSource(null, tokenFile).ToString().ShouldBe("GitHubTokenSource { set }");
        new GitHubTokenSource(null, null).ToString().ShouldBe("GitHubTokenSource { missing }");
    }

    /// <summary>A second request of the same client carries the token the file holds by then: the client is not restarted.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGitHubApiRequests_TokenFileRewrittenBetweenRequests_TheSecondRequestCarriesTheNewToken()
    {
        File.WriteAllText(tokenFile, "<stub-token-one>");
        var source = new GitHubTokenSource(null, tokenFile);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "object": { "sha": "abc123" } }"""));
        using var gitHub = GitHubApi.Create(() => source.Current, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        await gitHub.GetBranchHeadAsync("example-org/env", "main");
        File.WriteAllText(tokenFile, "<stub-token-two>");
        await gitHub.GetBranchHeadAsync("example-org/env", "main");

        handler.Requests.Select(request => request.Header("Authorization")).ShouldBe(["Bearer <stub-token-one>", "Bearer <stub-token-two>"]);
        handler.Requests.ShouldAllBe(request => request.Header("X-GitHub-Api-Version") == GitHubApi.ApiVersion);
    }

    /// <summary>The one-shot form of <c>Create</c> keeps sending its token, and a source with no token sends none.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGitHubApiCreate_FixedTokenOrNoToken_SendsThatTokenOrNone()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "object": { "sha": "abc123" } }"""));
        using var fixedToken = GitHubApi.Create("<stub-fixed-token>", TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");
        using var noToken = GitHubApi.Create(() => null, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        await fixedToken.GetBranchHeadAsync("example-org/env", "main");
        await noToken.GetBranchHeadAsync("example-org/env", "main");

        handler.Requests[0].Header("Authorization").ShouldBe("Bearer <stub-fixed-token>");
        handler.Requests[1].Header("Authorization").ShouldBeNull();
    }

    /// <summary>The handler sets the header from the source at each request, over whatever a request already carried.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenBearerTokenHandlerSends_TokenChanges_EachRequestCarriesTheCurrentToken()
    {
        var token = "<stub-token-one>";
        var inner = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new BearerTokenHandler(() => token, inner)) { BaseAddress = new Uri("https://github.example.test/") };

        await client.GetAsync("a");
        token = "<stub-token-two>";
        await client.GetAsync("b");

        inner.Requests.Select(request => request.Header("Authorization")).ShouldBe(["Bearer <stub-token-one>", "Bearer <stub-token-two>"]);
    }

    /// <summary>
    /// The settings read <c>GITHUB_TOKEN_FILE</c>: <see cref="PlatformSecrets.GitHubToken"/> follows the file on every access, so
    /// the readers that take the token per request (Kit, GitOps, Octopus REST helpers) pick up a re-minted token; the file
    /// path and the value are not printed.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenSecrets_TokenFileVariableSet_FollowsTheFileAndPrintsNothing()
    {
        File.WriteAllText(tokenFile, "<stub-token-one>");
        var environment = new StubEnvironmentVariables(
            (EnvironmentVariableNames.GitHubToken, "<stub-token-start>"),
            (EnvironmentVariableNames.GitHubTokenFile, tokenFile));
        var secrets = PlatformSecrets.FromEnvironment(environment);

        var first = secrets.GitHubToken;
        File.WriteAllText(tokenFile, "<stub-token-two>");
        var second = secrets.GitHubToken;

        first.ShouldBe("<stub-token-one>");
        second.ShouldBe("<stub-token-two>");
        secrets.ToString().ShouldBe("PlatformSecrets { OCTOPUS_API_KEY = missing, CODEFRESH_API_KEY = missing, AZURE_CLIENT_ID = missing, AZURE_CLIENT_SECRET = missing, GITHUB_TOKEN = set }");
        secrets.ToString().ShouldNotContain("stub-token");
        secrets.ToString().ShouldNotContain(tokenFile);
        EnvironmentVariableNames.Secrets.ShouldNotContain(EnvironmentVariableNames.GitHubTokenFile, "the token file path is not a secret, the token is");
    }

    /// <summary>With only the token file set, the GitHub client is created and sends the file's token; with neither variable the prerequisite names both.</summary>
    [Test]
    [Capability("CAP-HARNESS-006")]
    public async Task WhenGitHubClient_OnlyTheTokenFileIsSet_IsCreatedAndSendsItsTokenAndWithNeitherTheMessageNamesBoth()
    {
        File.WriteAllText(tokenFile, "<stub-token-one>");
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "object": { "sha": "abc123" } }"""));
        using var clients = new PlatformClients(Settings((EnvironmentVariableNames.GitHubTokenFile, tokenFile)), handlerFactory: () => handler);
        using var withNeither = new PlatformClients(Settings());

        await clients.GitHub.GetBranchHeadAsync("example-org/env", "main");
        File.WriteAllText(tokenFile, "<stub-token-two>");
        await clients.GitHub.GetBranchHeadAsync("example-org/env", "main");
        var exception = Should.Throw<PlatformPrerequisiteException>(() => withNeither.GitHub);

        handler.Requests.Select(request => request.Header("Authorization")).ShouldBe(["Bearer <stub-token-one>", "Bearer <stub-token-two>"]);
        exception.Message.ShouldContain("environment variable GITHUB_TOKEN is not set, and GITHUB_TOKEN_FILE names no token file");
        exception.Message.ShouldContain("the GitHub App aisf-conformance is not configured yet");
    }

    private static PlatformSettings Settings(params (string Name, string? Value)[] environment) => PlatformSettings.Parse(
        """{ "OctopusUrl": "https://octopus.example.test", "OctopusSpaceId": "Spaces-1", "AzureSubscriptionId": "00000000-0000-0000-0000-000000000001" }""",
        "settings.json",
        new StubEnvironmentVariables(environment));
}
