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
        // Assembled at run time so that no file of the repository holds a private key block, not even a bogus one.
        var bogusKey = "-----BEGIN " + "PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END " + "PRIVATE KEY-----\n";
        var snippet = $"try {{ New-GitHubAppJwt -AppId '{AppId}' -Now {Now} | Out-Null; Write-Output 'NO-ERROR' }} catch {{ Write-Output $_.Exception.Message }}";

        var missing = Snippet(snippet);
        var absentFile = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY_PATH", Path.Combine(scratch, "no-such-key.pem")));
        var garbled = Snippet(snippet, ("AISF_BOARD_APP_PRIVATE_KEY", bogusKey));

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

    /// <summary>The helper signs exactly what <c>openssl dgst -sha256 -sign</c> signs: an independent signer confirms the signature (the Octopus step report-commit-status now runs this helper).</summary>
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

        // openssl signs the header and claims of the helper's token as an independent signer.
        var input = Path.Combine(scratch, "input.txt");
        var signature = Path.Combine(scratch, "signature.bin");
        File.WriteAllText(input, $"{parts[0]}.{parts[1]}");
        var signed = KitToolbox.Run(openssl!, ["dgst", "-sha256", "-sign", key.Path, "-out", signature, input], scratch);
        signed.ExitCode.ShouldBe(0, signed.Transcript);

        System.Buffers.Text.Base64Url.EncodeToString(File.ReadAllBytes(signature)).ShouldBe(parts[2]);
    }

    /// <summary>The key handed over in memory (-PrivateKey) gives the token of the key file for the same clock, with no key variable set, and a one-line PEM with escaped line breaks works.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_PrivateKeyParameter_GivesTheTokenOfTheFileWithoutAnyKeySource()
    {
        using var key = new TestAppKey();
        var fromFile = Snippet($"Write-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now})", Key(key));
        var oneLine = key.Pem.Trim().Replace("\n", "\\n", StringComparison.Ordinal);

        var inMemory = Snippet($"{PemLiteral("$key", key.Pem)}\nWrite-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now} -PrivateKey $key)");
        var escaped = Snippet($"{PemLiteral("$key", oneLine)}\nWrite-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now} -PrivateKey $key)");

        inMemory.ExitCode.ShouldBe(0, inMemory.Transcript);
        inMemory.Output.Trim().ShouldBe(fromFile.Output.Trim(), inMemory.Transcript);
        escaped.Output.Trim().ShouldBe(fromFile.Output.Trim(), escaped.Transcript);
        key.Verifies(inMemory.Output.Trim()).ShouldBeTrue(inMemory.Transcript);
    }

    /// <summary>An in-memory key wins over the key file and the environment variable.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_PrivateKeyParameter_WinsOverTheFileAndTheEnvironment()
    {
        using var memoryKey = new TestAppKey();
        using var fileKey = new TestAppKey();
        using var textKey = new TestAppKey();

        var result = Snippet($"{PemLiteral("$key", memoryKey.Pem)}\nWrite-Output (New-GitHubAppJwt -AppId '{AppId}' -Now {Now} -PrivateKey $key)", Key(fileKey), ("AISF_BOARD_APP_PRIVATE_KEY", textKey.Pem));

        var jwt = result.Output.Trim();
        memoryKey.Verifies(jwt).ShouldBeTrue(result.Transcript);
        fileKey.Verifies(jwt).ShouldBeFalse();
        textKey.Verifies(jwt).ShouldBeFalse();
    }

    /// <summary>A garbled in-memory key fails with a message that holds neither key material nor a PEM marker.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_BadPrivateKeyParameter_FailsWithoutLeakingKeyMaterial()
    {
        // Assembled at run time so that no file of the repository holds a private key block, not even a bogus one.
        var bogusKey = "-----BEGIN " + "PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END " + "PRIVATE KEY-----\n";
        var snippet = $"{PemLiteral("$key", bogusKey)}\ntry {{ New-GitHubAppJwt -AppId '{AppId}' -Now {Now} -PrivateKey $key | Out-Null; Write-Output 'NO-ERROR' }} catch {{ Write-Output $_.Exception.Message }}";

        var result = Snippet(snippet);

        result.Output.Trim().ShouldBe($"GitHub App {AppId}: the private key is not a valid RSA PEM key.");
        result.Transcript.ShouldNotContain("BEGIN");
        result.Transcript.ShouldNotContain("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo");
    }

    /// <summary>The installation token is minted with the in-memory key alone: the exchange is signed by it, no key variable is set, no file with key material appears in the temporary folder, and the transcript holds no key, JWT or token.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetGitHubAppInstallationToken_PrivateKeyParameter_SignsTheExchangeAndWritesNoKeyToDisk()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var temporary = Directory.CreateDirectory(Path.Combine(scratch, "tmp")).FullName;
        var snippet = $$"""
            {{PemLiteral("$key", key.Pem)}}
            $token = Get-GitHubAppInstallationToken -AppId '{{AppId}}' -InstallationId '777' -Now {{Now}} -Repository @('acme/first-repo') -Permission @{ statuses = 'write' } -PrivateKey $key
            Write-Output "minted=$($token -ceq '{{StubGitHubApi.AppToken}}')"
            """;

        var environment = GitHubScriptHost.Environment(api, null);
        environment["TMPDIR"] = temporary;
        environment["TEMP"] = temporary;
        environment["TMP"] = temporary;
        var result = RunSnippet(snippet, environment);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.ShouldContain("minted=True");
        var request = api.Requests.ShouldHaveSingleItem();
        request.PathOnly.ShouldBe("/app/installations/777/access_tokens");
        key.Verifies(request.Bearer).ShouldBeTrue("the exchange is not authenticated by a JWT signed with the in-memory key");
        JsonNode.Parse(request.Body)!.ToJsonString().ShouldBe("""{"repositories":["first-repo"],"permissions":{"statuses":"write"}}""");
        foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).Contains(key.BodyFragment, StringComparison.Ordinal).ShouldBeFalse($"the key was written to {file}");
        }

        AssertNoSecrets(result, key, request.Bearer);
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

    // A PowerShell single-quoted here-string that puts the PEM in a variable; the key is not in any environment variable or key file.
    private static string PemLiteral(string variable, string pem) => $"{variable} = @'\n{pem.TrimEnd()}\n'@";

    /// <summary>
    /// The conformance App (#44): with -Prefix AISF_CONFORMANCE_APP the key comes from that prefix's file, text (also with
    /// escaped line breaks) and the file wins, and the board key is ignored; the token is the same, being deterministic.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_NewGitHubAppJwt_ConformancePrefix_ReadsItsOwnKeyFileTextAndEscapedTextAndIgnoresTheBoardKey()
    {
        using var conformanceKey = new TestAppKey();
        using var boardKey = new TestAppKey();
        var snippet = $"Write-Output (New-GitHubAppJwt -AppId '5130402' -Now {Now} -Prefix AISF_CONFORMANCE_APP)";

        var fromFile = Snippet(snippet, ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", conformanceKey.Path), Key(boardKey));
        var fromText = Snippet(snippet, ("AISF_CONFORMANCE_APP_PRIVATE_KEY", conformanceKey.Pem), Key(boardKey));
        var fromEscapedText = Snippet(snippet, ("AISF_CONFORMANCE_APP_PRIVATE_KEY", conformanceKey.Pem.Trim().Replace("\n", "\\n", StringComparison.Ordinal)));
        var fileWinsOverText = Snippet(snippet, ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", conformanceKey.Path), ("AISF_CONFORMANCE_APP_PRIVATE_KEY", boardKey.Pem));
        var onlyBoardKey = Snippet($"try {{ New-GitHubAppJwt -AppId '5130402' -Now {Now} -Prefix AISF_CONFORMANCE_APP | Out-Null; 'NO-ERROR' }} catch {{ $_.Exception.Message }}", Key(boardKey));

        fromFile.ExitCode.ShouldBe(0, fromFile.Transcript);
        conformanceKey.Verifies(fromFile.Output.Trim()).ShouldBeTrue(fromFile.Transcript);
        fromText.Output.Trim().ShouldBe(fromFile.Output.Trim(), fromText.Transcript);
        fromEscapedText.Output.Trim().ShouldBe(fromFile.Output.Trim(), fromEscapedText.Transcript);
        fileWinsOverText.Output.Trim().ShouldBe(fromFile.Output.Trim(), fileWinsOverText.Transcript);
        onlyBoardKey.Output.ShouldContain("GitHub App 5130402: no private key (set AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH to a PEM file, or AISF_CONFORMANCE_APP_PRIVATE_KEY to the PEM text).");
        onlyBoardKey.Transcript.ShouldNotContain("NO-ERROR");
        onlyBoardKey.Transcript.ShouldNotContain(boardKey.BodyFragment);
    }

    /// <summary>The installation id of the conformance App comes from its own variable, and the board's installation variable is not read.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_GetGitHubAppInstallationToken_ConformancePrefix_UsesItsOwnInstallationIdVariable()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var snippet = $"$null = Get-GitHubAppInstallationToken -AppId '5130402' -Now {Now} -Prefix AISF_CONFORMANCE_APP -Repository @('acme/r') -Permission @{{ contents = 'write' }}";

        var result = Snippet(snippet, api, ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "888"), ("AISF_BOARD_APP_INSTALLATION_ID", "111"));

        result.ExitCode.ShouldBe(0, result.Transcript);
        api.Requests.ShouldHaveSingleItem().PathOnly.ShouldBe("/app/installations/888/access_tokens");
    }

    /// <summary>
    /// Resolve-GitHubAppToken with the conformance inputs mints one installation token limited to exactly the repositories and
    /// permissions asked for (the three repositories and four permissions of aisf-conformance), reports State 'app' and prints
    /// no key, JWT or token.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubAppToken_ConformanceInputs_MintsTheNarrowedTokenAndReportsStateApp()
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var snippet = $$"""
            $resolved = Resolve-GitHubAppToken -Prefix AISF_CONFORMANCE_APP -Now {{Now}} `
                -Repository @('acme/env-repo', 'acme/20260923-001', 'acme/platform-sandbox') `
                -Permission @{ contents = 'write'; pull_requests = 'write'; statuses = 'read'; metadata = 'read' }
            Write-Output "state=$($resolved.State) source=$($resolved.Source) matches=$($resolved.Token -ceq '{{StubGitHubApi.AppToken}}') reason=[$($resolved.Reason)]"
            """;

        var result = Snippet(snippet, api, ("AISF_CONFORMANCE_APP_ID", "5130402"), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "777"), ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe("state=app source=app matches=True reason=[]");
        var request = api.Requests.ShouldHaveSingleItem();
        request.PathOnly.ShouldBe("/app/installations/777/access_tokens");
        key.Verifies(request.Bearer).ShouldBeTrue();
        var body = JsonNode.Parse(request.Body)!.AsObject();
        body["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["env-repo", "20260923-001", "platform-sandbox"]);
        body["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal).ShouldBe(
            new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["statuses"] = "read", ["metadata"] = "read" }, ignoreOrder: true);
        AssertNoSecrets(result, key, request.Bearer);
    }

    /// <summary>With the App id, the installation id or the key missing the state is 'unset', nothing is requested, and no other credential is tried.</summary>
    /// <param name="withId">Set the App id.</param>
    /// <param name="withInstallation">Set the installation id.</param>
    /// <param name="withKey">Set the key.</param>
    /// <param name="missing">The names the reason lists.</param>
    [TestCase(false, false, false, "AISF_CONFORMANCE_APP_ID, AISF_CONFORMANCE_APP_INSTALLATION_ID, AISF_CONFORMANCE_APP_PRIVATE_KEY")]
    [TestCase(true, true, false, "AISF_CONFORMANCE_APP_PRIVATE_KEY")]
    [TestCase(false, true, true, "AISF_CONFORMANCE_APP_ID")]
    [TestCase(true, false, true, "AISF_CONFORMANCE_APP_INSTALLATION_ID")]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubAppToken_AnInputMissing_IsUnsetMakesNoRequestAndIgnoresOtherCredentials(bool withId, bool withInstallation, bool withKey, string missing)
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi();
        var set = new List<(string Name, string Value)>
        {
            ("AISF_BOARD_APP_TOKEN", "board-token-must-not-be-used"), ("GH_TOKEN", "gh-token-must-not-be-used"), ("GITHUB_TOKEN", "pat-must-not-be-used"), Key(key),
        };
        if (withId) { set.Add(("AISF_CONFORMANCE_APP_ID", "5130402")); }
        if (withInstallation) { set.Add(("AISF_CONFORMANCE_APP_INSTALLATION_ID", "777")); }
        if (withKey) { set.Add(("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path)); }
        var snippet = "$resolved = Resolve-GitHubAppToken -Prefix AISF_CONFORMANCE_APP -Repository @('acme/r') -Permission @{ contents = 'read' }; Write-Output \"state=$($resolved.State) token=[$($resolved.Token)] reason=$($resolved.Reason)\"";

        var result = Snippet(snippet, api, [.. set]);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe($"state=unset token=[] reason=not set: {missing}");
        api.Requests.ShouldBeEmpty();
    }

    /// <summary>A refused exchange gives State 'failed' with the HTTP status only, no token, and no key, JWT or token in the transcript.</summary>
    /// <param name="status">HTTP status of the refused exchange.</param>
    [TestCase(401)]
    [TestCase(422)]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubAppToken_ExchangeRefused_IsFailedWithTheStatusOnly(int status)
    {
        using var key = new TestAppKey();
        using var api = new StubGitHubApi { MintStatus = status };
        var snippet = "$resolved = Resolve-GitHubAppToken -Prefix AISF_CONFORMANCE_APP -Repository @('acme/r') -Permission @{ contents = 'read' }; Write-Output \"state=$($resolved.State) token=[$($resolved.Token)] reason=$($resolved.Reason)\"";

        var result = Snippet(snippet, api, ("AISF_CONFORMANCE_APP_ID", "5130402"), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "777"), ("AISF_CONFORMANCE_APP_PRIVATE_KEY_PATH", key.Path));

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Trim().ShouldBe($"state=failed token=[] reason=HTTP {status}");
        AssertNoSecrets(result, key, api.Requests[0].Bearer);
    }

    /// <summary>A key that is not PEM gives State 'failed' without the key in the transcript.</summary>
    [Test]
    [Capability("CAP-KIT-010")]
    public void Should_ResolveGitHubAppToken_GarbledKey_IsFailedWithoutLeakingIt()
    {
        var bogusKey = "-----BEGIN " + "PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END " + "PRIVATE KEY-----\n";
        using var api = new StubGitHubApi();
        var snippet = "$resolved = Resolve-GitHubAppToken -Prefix AISF_CONFORMANCE_APP -Repository @('acme/r') -Permission @{ contents = 'read' }; Write-Output \"state=$($resolved.State) reason=$($resolved.Reason)\"";

        var result = Snippet(snippet, api, ("AISF_CONFORMANCE_APP_ID", "5130402"), ("AISF_CONFORMANCE_APP_INSTALLATION_ID", "777"), ("AISF_CONFORMANCE_APP_PRIVATE_KEY", bogusKey));

        result.Output.Trim().ShouldBe("state=failed reason=the private key was not usable");
        result.Transcript.ShouldNotContain("BEGIN");
        result.Transcript.ShouldNotContain("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo");
        api.Requests.ShouldBeEmpty();
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
