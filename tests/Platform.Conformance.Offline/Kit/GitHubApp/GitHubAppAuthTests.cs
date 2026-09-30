using System.Text;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit.GitHubApp;

/// <summary>
/// CAP-KIT-010, unit half: <c>scripts/github/GitHubAppAuth.ps1</c>, the shared GitHub App helper (RS256 JWT, installation
/// token, token order). Every test dot-sources the script in a real <c>pwsh</c>, with a key generated at test time and a stub
/// GitHub API behind GITHUB_API_URL; none makes a live call. The transcripts are checked for the key, the JWT and the token.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class GitHubAppAuthTests
{
    private const string AppId = "5130401";
    private const long Now = 1_800_000_000;

    private string scratch = null!;

    /// <summary>Creates the folder for the snippet scripts.</summary>
    [SetUp]
    public void CreateScratch() => scratch = Directory.CreateTempSubdirectory("github-app-auth-").FullName;

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void DeleteScratch() => Directory.Delete(scratch, recursive: true);

    /// <summary>The JWT has three segments, the documented header and claims, a signature that verifies with the App's public key, and exp - iat within ten minutes.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_KeyFile_ProducesAValidRs256Token()
    {
        using var key = new TestAppKey();

        var result = Snippet($"Write-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now})", Key(key));

        result.ExitCode.ShouldBe(0, result.Transcript);
        var jwt = result.Output.Trim();
        jwt.Split('.').Length.ShouldBe(3);
        Json(jwt.Split('.')[0]).ToJsonString().ShouldBe("""{"alg":"RS256","typ":"JWT"}""");
        var claims = Json(jwt.Split('.')[1]);
        claims["iat"]!.GetValue<long>().ShouldBe(Now - 60);
        claims["exp"]!.GetValue<long>().ShouldBe(Now + 540);
        claims["iss"]!.GetValue<string>().ShouldBe(AppId);
        (claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>()).ShouldBeLessThanOrEqualTo(600);
        key.Verifies(jwt).ShouldBeTrue("the signature does not verify with the public half of the key");
    }

    /// <summary>A signature is deterministic (PKCS#1 v1.5), so the key from a file, from PEM text and from PEM text with escaped line breaks give the same token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_KeyFromFileOrEnvironment_GivesTheSameToken()
    {
        using var key = new TestAppKey();
        var snippet = $"Write-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now})";

        var fromFile = Snippet(snippet, Key(key));
        var fromText = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY", key.Pem));
        var fromEscapedText = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY", key.Pem.Trim().Replace("\n", "\\n", StringComparison.Ordinal)));

        fromFile.ExitCode.ShouldBe(0, fromFile.Transcript);
        fromText.Output.Trim().ShouldBe(fromFile.Output.Trim(), fromText.Transcript);
        fromEscapedText.Output.Trim().ShouldBe(fromFile.Output.Trim(), fromEscapedText.Transcript);
    }

    /// <summary>The key file wins over the environment variable when both are set.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_BothKeySources_UsesTheFile()
    {
        using var fileKey = new TestAppKey();
        using var textKey = new TestAppKey();

        var result = Snippet($"Write-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now})", Key(fileKey), ("AISF_BOARD_APP_PRIVATE_KEY", textKey.Pem));

        var jwt = result.Output.Trim();
        fileKey.Verifies(jwt).ShouldBeTrue(result.Transcript);
        textKey.Verifies(jwt).ShouldBeFalse();
    }

    /// <summary>A missing, absent-file or garbled key fails with a message that holds neither key material nor a PEM marker.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_MissingOrGarbledKey_FailsWithoutLeakingKeyMaterial()
    {
        const string Garbled = "-----BEGIN PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END PRIVATE KEY-----\n";
        var snippet = $"try {{ New-GitHubAppJwt -AppId '{AppId}' -Now {Now} | Out-Null; Write-Output 'NO-ERROR' }} catch {{ Write-Output $_.Exception.Message }}";

        var missing = Snippet(snippet);
        var absentFile = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY_PATH", Path.Combine(scratch, "no-such-key.pem")));
        var garbled = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY", Garbled));

        missing.Output.ShouldContain($"GitHub App {AppId}: no private key");
        missing.Output.ShouldContain("AISF_BOARD_APP_PRIVATE_KEY_PATH");
        absentFile.Output.ShouldContain("AISF_BOARD_APP_PRIVATE_KEY_PATH does not exist");
        garbled.Output.ShouldContain($"GitHub App {AppId}: the private key is not a valid RSA PEM key");
        foreach (var result in new[] { missing, absentFile, garbled })
        {
            result.Transcript.ShouldNotContain("BEGIN");
            result.Transcript.ShouldNotContain("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo");
            result.Transcript.ShouldNotContain("NO-ERROR");
        }
    }

    /// <summary>The helper signs exactly what <c>openssl dgst -sha256 -sign</c> signs, which is what the Octopus step report-commit-status runs.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_SameKeyAndClock_MatchesTheOpensslSignatureOfTheOctopusStep()
    {
        var openssl = KitToolbox.Find(OperatingSystem.IsWindows() ? "openssl.exe" : "openssl", "OPENSSL");
        if (openssl is null)
        {
            Assert.Inconclusive("openssl is not on PATH (set OPENSSL); the parity with the Octopus step is checked where it is installed");
        }

        using var key = new TestAppKey();
        var result = Snippet($"Write-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now})", Key(key));
        result.ExitCode.ShouldBe(0, result.Transcript);
        var parts = result.Output.Trim().Split('.');

        // The Octopus step (deployment_process.ocl, report-commit-status) builds the same header and claims and signs them with openssl.
        var input = Path.Combine(scratch, "input.txt");
        var signature = Path.Combine(scratch, "signature.bin");
        File.WriteAllText(input, $"{parts[0]}.{parts[1]}");
        var signed = KitToolbox.Run(openssl!, ["dgst", "-sha256", "-sign", key.Path, "-out", signature, input], scratch);
        signed.ExitCode.ShouldBe(0, signed.Transcript);

        System.Buffers.Text.Base64Url.EncodeToString(File.ReadAllBytes(signature)).ShouldBe(parts[2]);
    }

    /// <summary>The Octopus step and the helper agree on the header, the clock skew and lifetime, the endpoint and the narrowing shape (a contract the offline test enforces).</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ReadOctopusStep_ReportCommitStatus_SharesTheJwtAndInstallationTokenContractWithTheHelper()
    {
        var ocl = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ".octopus", "apps", "workorders", "workorders", "deployment_process.ocl"));
        var helper = File.ReadAllText(GitHubScriptHost.Script("scripts/github/GitHubAppAuth.ps1"));
        var step = ocl[ocl.IndexOf("step \"report-commit-status\"", StringComparison.Ordinal)..];

        foreach (var literal in new[] { """'{"alg":"RS256","typ":"JWT"}'""", "'{\"iat\":' + ($now - 60) + ',\"exp\":' + ($now + 540)", "/access_tokens\"", "repositories = @($repositoryName)", "statuses = 'write'" })
        {
            step.ShouldContain(literal, Case.Sensitive);
        }

        helper.ShouldContain("""'{"alg":"RS256","typ":"JWT"}'""", Case.Sensitive);
        helper.ShouldContain("'{\"iat\":' + ($Now - 60) + ',\"exp\":' + ($Now + 540)", Case.Sensitive);
        helper.ShouldContain("/access_tokens", Case.Sensitive);
        helper.ShouldContain("repositories = $names; permissions = $Permission", Case.Sensitive);
    }

    /// <summary>The token exchange sends exactly <c>repositories</c> and <c>permissions</c> (narrowed), authenticated by a JWT, and the transcript holds no token, JWT or key.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetGitHubAppInstallationToken_RepositoriesAndPermissions_AreSentNarrowedAndNothingIsPrinted()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var snippet = $$"""
            $token = Get-GitHubAppInstallationToken -AppId '{{AppId}}' -InstallationId '777' -Now {{Now}} `
                -Repository @('acme/first-repo', 'acme/second-repo') -Permission @{ organization_projects = 'write'; issues = 'read' }
            Write-Output "minted=$($token.Length -gt 0)"
            """;

        var result = Snippet(snippet, api, Key(key));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain("minted=True");
        var request = api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.PathOnly.ShouldBe("/app/installations/777/access_tokens");
        key.Verifies(request.Bearer).ShouldBeTrue("the exchange is not authenticated by a JWT signed with the App key");
        var body = JsonNode.Parse(request.Body)!.AsObject();
        body.Select(property => property.Key).ShouldBe(["repositories", "permissions"]);
        body["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["first-repo", "second-repo"]);
        var permissions = body["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal);
        permissions.Count.ShouldBe(2, "the token is narrowed to exactly the permissions asked for");
        permissions["organization_projects"].ShouldBe("write");
        permissions["issues"].ShouldBe("read");
        AssertNoSecrets(result, key, request.Bearer);
    }

    /// <summary>Without an installation id the helper finds it with the JWT, then exchanges.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetGitHubAppInstallationToken_NoInstallationId_DiscoversItWithTheJwt()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var snippet = $"$token = Get-GitHubAppInstallationToken -AppId '{AppId}' -Now {Now} -Repository @('acme/first-repo') -Permission @{{ issues = 'read' }}; Write-Output ($token -ceq '{StubGitHubApi.AppToken}')";

        var result = Snippet(snippet, api, Key(key));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("True");
        api.Requests.Select(request => $"{request.Method} {request.PathOnly}")
            .ShouldBe(["GET /repos/acme/first-repo/installation", "POST /app/installations/4242/access_tokens"]);
        api.Requests.ShouldAllBe(request => key.Verifies(request.Bearer));
    }

    /// <summary>A refused exchange fails with the App id and the HTTP status only.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetGitHubAppInstallationToken_ExchangeRefused_FailsWithAppIdAndStatusOnly()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { MintStatus = 401 };
        var snippet = $"try {{ Get-GitHubAppInstallationToken -AppId '{AppId}' -InstallationId '777' -Now {Now} -Repository @('acme/r') -Permission @{{ issues = 'read' }} | Out-Null }} catch {{ Write-Output $_.Exception.Message }}";

        var result = Snippet(snippet, api, Key(key));

        result.Output.Trim().ShouldBe($"GitHub App {AppId}: no installation token (HTTP 401).");
        AssertNoSecrets(result, key, api.Requests[0].Bearer);
    }

    /// <summary>Token order: a pre-minted token wins over everything, and a retired personal access token variable is ignored.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubToken_PreMintedTokenAndDecoyPat_PreMintedWinsAndDecoyIsIgnored()
    {
        using var fakeGh = new FakeGhCli();

        var result = Resolve(fakeGh, ("AISF_BOARD_APP_TOKEN", "pre-minted"), ("FAKE_GH_TOKEN", "cli-token"), (RetiredCredentialGuard.SampleAppsPat, "decoy-pat"));

        result.Output.Trim().ShouldBe("app pre-minted");
    }

    /// <summary>Token order: without an App the GitHub CLI token is used, and the retired personal access token variable is still ignored.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubToken_OnlyTheCliAndDecoyPat_UsesTheCliToken()
    {
        using var fakeGh = new FakeGhCli();

        var result = Resolve(fakeGh, ("FAKE_GH_TOKEN", "cli-token"), (RetiredCredentialGuard.SampleAppsPat, "decoy-pat"));

        result.Output.Trim().ShouldBe("gh cli-token");
    }

    /// <summary>Token order: without gh on PATH, GH_TOKEN is read directly (a cloud session), and with nothing at all there is no token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubToken_NoGhOnPath_ReadsGhTokenDirectlyAndReturnsNothingWithoutOne()
    {
        using var noGh = new FakeGhCli();
        File.Delete(Path.Combine(noGh.Directory, OperatingSystem.IsWindows() ? "gh.cmd" : "gh"));
        var snippet = "$resolved = Resolve-GitHubToken; if ($resolved) { Write-Output \"$($resolved.Source) $($resolved.Token)\" } else { Write-Output 'none' }";

        var withToken = Snippet(snippet, environment => environment["PATH"] = noGh.Directory, ("GH_TOKEN", "env-token"), (RetiredCredentialGuard.SampleAppsPat, "decoy-pat"));
        var withoutToken = Snippet(snippet, environment => environment["PATH"] = noGh.Directory);

        withToken.Output.Trim().ShouldBe("gh env-token");
        withoutToken.Output.Trim().ShouldBe("none");
    }

    /// <summary>Token order: the App is minted from the id and the key when no token is pre-minted, ahead of the GitHub CLI token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubToken_AppIdAndKey_MintsTheInstallationTokenAheadOfTheCli()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        using var fakeGh = new FakeGhCli();
        var snippet = """
            $config = @{ appId = 5130401; installationId = 166366113; repositories = @('acme/one', 'acme/two'); permissions = @{ issues = 'read' } }
            $resolved = Resolve-GitHubToken -AppConfig $config
            Write-Output "$($resolved.Source) $($resolved.Token)"
            """;

        var result = Snippet(snippet, api, fakeGh, Key(key), ("FAKE_GH_TOKEN", "cli-token"));

        result.Output.Trim().ShouldBe($"app {StubGitHubApi.AppToken}");
        api.Requests.ShouldHaveSingleItem().PathOnly.ShouldBe("/app/installations/166366113/access_tokens");
    }

    /// <summary>Token order: a refused mint falls back to the GitHub CLI token with a message that names the App id and status only.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubToken_MintRefused_FallsBackToTheCliWithoutLeaking()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { MintStatus = 403 };
        using var fakeGh = new FakeGhCli();
        var snippet = """
            $config = @{ appId = 5130401; installationId = 166366113; repositories = @('acme/one'); permissions = @{ issues = 'read' } }
            $resolved = Resolve-GitHubToken -AppConfig $config
            Write-Output "$($resolved.Source)"
            """;

        var result = Snippet(snippet, api, fakeGh, Key(key), ("FAKE_GH_TOKEN", "cli-token"));

        result.Output.ShouldContain("github-app: GitHub App 5130401: no installation token (HTTP 403). Falling back to the GitHub CLI token.");
        result.Output.TrimEnd().ShouldEndWith("gh");
        AssertNoSecrets(result, key, api.Requests[0].Bearer);
    }

    private static (string Name, string Value) Key(TestAppKey key) => ("AISF_BOARD_APP_PRIVATE_KEY_PATH", key.Path);

    private static JsonNode Json(string base64Url) => JsonNode.Parse(Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(base64Url)))!;

    private static void AssertNoSecrets(ProcessResult result, TestAppKey key, params string[] secrets)
    {
        var transcript = result.Transcript;
        transcript.ShouldNotContain("BEGIN");
        transcript.ShouldNotContain(key.BodyFragment);
        transcript.ShouldNotContain(StubGitHubApi.AppToken);
        foreach (var secret in secrets.Where(secret => secret.Length > 0))
        {
            transcript.ShouldNotContain(secret);
        }
    }

    private ProcessResult Resolve(FakeGhCli fakeGh, params (string Name, string Value)[] set) => Snippet(
        "$resolved = Resolve-GitHubToken; if ($resolved) { Write-Output \"$($resolved.Source) $($resolved.Token)\" } else { Write-Output 'none' }",
        null,
        fakeGh,
        set);

    private ProcessResult Snippet(string body, params (string Name, string Value)[] set) => Snippet(body, null, null, set);

    private ProcessResult Snippet(string body, StubGitHubApi api, params (string Name, string Value)[] set) => Snippet(body, api, null, set);

    private ProcessResult Snippet(string body, Action<Dictionary<string, string?>> adjust, params (string Name, string Value)[] set)
    {
        var environment = GitHubScriptHost.Environment(null, null, set);
        adjust(environment);
        return RunSnippet(body, environment);
    }

    private ProcessResult Snippet(string body, StubGitHubApi? api, FakeGhCli? fakeGh, params (string Name, string Value)[] set) =>
        RunSnippet(body, GitHubScriptHost.Environment(api, fakeGh, set));

    private ProcessResult RunSnippet(string body, Dictionary<string, string?> environment)
    {
        var script = Path.Combine(scratch, $"snippet-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, $"$ErrorActionPreference = 'Stop'\n. '{GitHubScriptHost.Script("scripts/github/GitHubAppAuth.ps1")}'\n{body}\n");
        return GitHubScriptHost.Run(script, environment);
    }
}
