using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-HARNESS-009 for the two helpers the conformance scripts dot-source: aks-power.ps1 reads the power state through
/// Azure Resource Manager with the client secret and the token only in private files, and sandbox-git.ps1 mints the
/// installation token of the GitHub App aisf-conformance and hands it to git only through a credential helper that reads
/// the environment.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformanceHelperScriptTests
{
    /// <summary>One token request with the secret from a private file, then one GET per configured cluster.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenGetAksPowerState_ServicePrincipal_ReadsEachClusterWithTheSecretAndTokenOnlyInPrivateFiles()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithClusters(nonprod: "Running", prod: "Stopped");

        var result = harness.RunCommand("""
            . '{scripts}/aks-power.ps1'
            $folder = [System.IO.Directory]::CreateTempSubdirectory('aks-power-').FullName
            "init=$(Initialize-AksPower -Directory $folder)"
            foreach ($tier in 'nonprod', 'prod', 'build') { "$tier=$(Get-AksPowerState -Tier $tier)" }
            "files=$((Get-ChildItem -LiteralPath $folder -Name) -join ',')"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(
            ["init=True", "nonprod=Running/Succeeded", "prod=Stopped/Succeeded", "build=unconfigured", "files=arm-headers"]);
        var calls = harness.Calls("curl");
        calls.Select(call => $"{call.Method} {call.Url}").ShouldBe(
        [
            "POST https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token",
            "GET https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-np/providers/Microsoft.ContainerService/managedClusters/aks-np?api-version=2024-10-01",
            "GET https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-p/providers/Microsoft.ContainerService/managedClusters/aks-p?api-version=2024-10-01",
        ]);
        var secretArgument = calls[0].Values("--data-urlencode").Single(value => value.StartsWith("client_secret@", StringComparison.Ordinal));
        var secretFile = secretArgument["client_secret@".Length..];
        calls[0].Files[secretFile].ShouldBe(PlatformStubRoutes.AzureSecret);
        calls[0].PrivateFiles.ShouldContain(secretFile);
        calls[0].Values("--data-urlencode").ShouldBe(["grant_type=client_credentials", "client_id=client-1", secretArgument, "scope=https://management.azure.com/.default"]);
        calls.Skip(1).ShouldAllBe(call => call.Headers.SequenceEqual(new[] { "Authorization: Bearer token-for-tests" }));
        calls.Skip(1).SelectMany(call => call.HeaderFiles).ShouldAllBe(file => file.Private);
        calls.SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(PlatformStubRoutes.AzureSecret, StringComparison.Ordinal) || argument.Contains("token-for-tests", StringComparison.Ordinal));
    }

    /// <summary>
    /// The data disks of a tier (conformance-arm's settled-stop condition): the names of those Attached, '' when none is,
    /// 'unknown' when the read fails; one GET per tier with the token only in the private header file.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void Should_GetAksAttachedDisk_DiskStates_NamesOnlyAttachedDisksAndUnknownOnAFailedRead()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithClusters();
        harness.Route("curl", ["/resourceGroups/rg-platform-nonprod-data/providers/Microsoft.Compute/disks?api-version=2024-03-02"], """
            {"value": [{"name": "disk-sandbox-tdd-db", "properties": {"diskState": "Attached"}}, {"name": "disk-sandbox-uat-db", "properties": {"diskState": "Reserved"}},
                       {"name": "disk-workorders-tdd-db", "properties": {"diskState": "Unattached"}}]}
            """);
        harness.Route("curl", ["/resourceGroups/rg-platform-prod-data/providers/Microsoft.Compute/disks?api-version=2024-03-02"], """{"value": [{"name": "disk-sandbox-prod-db", "properties": {"diskState": "Reserved"}}]}""");

        var result = harness.RunCommand("""
            . '{scripts}/aks-power.ps1'
            "before=$(Get-AksAttachedDisk -Tier nonprod)"
            $null = Initialize-AksPower -Directory ([System.IO.Directory]::CreateTempSubdirectory('aks-power-').FullName)
            foreach ($tier in 'nonprod', 'prod', 'build') { "$tier=$(Get-AksAttachedDisk -Tier $tier)" }
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(
            ["before=unknown", "nonprod=disk-sandbox-tdd-db", "prod=", "build=unknown"]);
        var reads = harness.Calls("curl").Where(call => call.Url?.Contains("Microsoft.Compute/disks", StringComparison.Ordinal) == true).ToArray();
        reads.Select(call => $"{call.Method} {call.Url}").ShouldBe(
        [
            "GET https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-platform-nonprod-data/providers/Microsoft.Compute/disks?api-version=2024-03-02",
            "GET https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-platform-prod-data/providers/Microsoft.Compute/disks?api-version=2024-03-02",
            "GET https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-platform-build-data/providers/Microsoft.Compute/disks?api-version=2024-03-02",
        ]);
        reads.ShouldAllBe(call => call.Headers.SequenceEqual(new[] { "Authorization: Bearer token-for-tests" }));
    }

    /// <summary>A placeholder client ID: a warning, no request, and every state unknown.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenInitializeAksPower_PlaceholderClientId_WarnsWithoutARequest()
    {
        using var harness = PlatformScriptHarness.Create("curl").WithClusters().With("AZURE_CLIENT_ID", "<client-id-of-sp-platform-conformance>");

        var result = harness.RunCommand("""
            . '{scripts}/aks-power.ps1'
            "init=$(Initialize-AksPower -Directory ([System.IO.Directory]::CreateTempSubdirectory('aks-power-').FullName))"
            "nonprod=$(Get-AksPowerState -Tier nonprod)"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(["init=False", "nonprod=unknown"]);
        result.Error.ShouldContain("aks-power: WARN Azure settings hold placeholders");
        harness.Calls().ShouldBeEmpty();
    }

    /// <summary>git gets the credential helper and the identity; the token itself is in no argument, and git reads it from the environment.</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenInvokeSandboxGit_AnyCommand_PassesTheTokenOnlyThroughTheCredentialHelper()
    {
        // The token is no longer given: Test-SandboxRequirement mints it from the GitHub App aisf-conformance (#44).
        const string token = StubGitHubApi.AppToken;
        using var harness = PlatformScriptHarness.Create();
        harness.RecordRealGit();
        harness.WithGitHubApp().With("SANDBOX_APP_REPO", "example-org/platform-sandbox");

        var result = harness.RunCommand("""
            . '{scripts}/sandbox-git.ps1'
            "require=$(Test-SandboxRequirement)"
            "minted=$($env:GITHUB_TOKEN -ceq 'stub-app-installation-token-0001')"
            "url=$(Get-SandboxUrl)"
            Invoke-SandboxGit config --get user.email
            $helper = @(Invoke-SandboxGit config --get-all credential.helper)[-1]
            "helper=$helper"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldContain("require=True");
        lines.ShouldContain("minted=True");
        lines.ShouldContain("url=https://github.com/example-org/platform-sandbox.git");
        lines.ShouldContain("platform-conformance@users.noreply.github.com");
        lines.ShouldContain("helper=!f() { echo username=x-access-token; echo \"password=${GITHUB_TOKEN}\"; }; f");
        var calls = harness.Calls("git");
        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(call => call.Arguments.Take(8).SequenceEqual(new[]
        {
            "-c", "credential.helper=", "-c", "credential.helper=!f() { echo username=x-access-token; echo \"password=${GITHUB_TOKEN}\"; }; f",
            "-c", "user.name=platform-conformance", "-c", "user.email=platform-conformance@users.noreply.github.com",
        }));
        calls.SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(token, StringComparison.Ordinal));
        result.Transcript.ShouldNotContain(token);
        result.Transcript.ShouldNotContain(harness.GitHubKey!.BodyFragment);
        harness.GitHubApi!.Requests.ShouldHaveSingleItem().PathOnly.ShouldBe("/app/installations/777/access_tokens");
    }

    /// <summary>
    /// The credential helper hands git the token that is in the environment when git calls it, so a token re-minted after one
    /// hour (Invoke-SandboxGit re-mints one older than 45 minutes) reaches git without touching a command line.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenInvokeSandboxGit_TokenOlderThanRefreshInterval_IsReMintedBeforeGitRuns()
    {
        using var harness = PlatformScriptHarness.Create();
        harness.RecordRealGit();
        harness.WithGitHubApp(numberedTokens: true).With("SANDBOX_APP_REPO", "example-org/platform-sandbox");

        var result = harness.RunCommand("""
            . '{scripts}/sandbox-git.ps1'
            "require=$(Test-SandboxRequirement)"
            "first=$($env:GITHUB_TOKEN)"
            $null = Invoke-SandboxGit config --get user.name
            "same=$($env:GITHUB_TOKEN)"
            $script:ConformanceTokenMintedAt = [DateTime]::UtcNow.AddMinutes(-46)
            $null = Invoke-SandboxGit config --get user.name
            "renewed=$($env:GITHUB_TOKEN)"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        var api = harness.GitHubApi!;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldBe(["require=True", $"first={api.MintedToken(1)}", $"same={api.MintedToken(1)}", $"renewed={api.MintedToken(2)}"]);
        api.Requests.Count.ShouldBe(2);
        harness.Calls("git").SelectMany(call => call.Arguments).ShouldNotContain(argument => argument.Contains(StubGitHubApi.AppToken, StringComparison.Ordinal));
    }

    /// <summary>An exchange the App refuses is reported by status only, returns false and leaves no token (a token of the environment is not used instead).</summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenTestSandboxRequirement_MintRefused_ReturnsFalseWithTheStatusAndNoToken()
    {
        using var harness = PlatformScriptHarness.Create();
        harness.WithGitHubApp(mintStatus: 401).With("SANDBOX_APP_REPO", "example-org/platform-sandbox").With("GITHUB_TOKEN", "stale-pat-for-tests");

        var result = harness.RunCommand("""
            . '{scripts}/sandbox-git.ps1'
            "require=$(Test-SandboxRequirement)"
            "token=[$($env:GITHUB_TOKEN)]"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(["require=False", "token=[]"]);
        result.Error.ShouldContain("conformance-github: mint refused (HTTP 401)");
        result.Transcript.ShouldNotContain("stale-pat-for-tests");
        result.Transcript.ShouldNotContain(harness.GitHubKey!.BodyFragment);
    }

    /// <summary>
    /// With the GitHub App not configured or with the repository placeholder the helper says why, and SANDBOX_GIT_URL wins
    /// over the GitHub URL. (Changed for #44: it used to name a missing GITHUB_TOKEN; a GITHUB_TOKEN of the environment is
    /// now dropped, never a fallback.)
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-009")]
    public void WhenTestSandboxRequirement_AppNotConfiguredOrRepositoryPlaceholder_ReturnsFalseWithTheReason()
    {
        using var harness = PlatformScriptHarness.Create();

        var result = harness.RunCommand("""
            . '{scripts}/sandbox-git.ps1'
            $env:SANDBOX_APP_REPO = 'example-org/platform-sandbox'
            $env:GITHUB_TOKEN = 'stale-pat-for-tests'
            "no-app=$(Test-SandboxRequirement)"
            "token=[$($env:GITHUB_TOKEN)]"
            $env:SANDBOX_APP_REPO = '<sandbox-app-repo>'
            "placeholder=$(Test-SandboxRequirement)"
            $env:SANDBOX_GIT_URL = '/tmp/sandbox.git'
            "url=$(Get-SandboxUrl)"
            """);

        result.ExitCode.ShouldBe(0, result.Transcript);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ShouldBe(["no-app=False", "token=[]", "placeholder=False", "url=/tmp/sandbox.git"]);
        result.Error.ShouldContain("conformance-github: GitHub App aisf-conformance is not configured (AISF_CONFORMANCE_APP_ID / _INSTALLATION_ID / _PRIVATE_KEY): PENDING owner setup, #44");
        result.Error.ShouldContain("sandbox-git: SANDBOX_APP_REPO is not set (spec variable)");
        result.Transcript.ShouldNotContain("stale-pat-for-tests");
    }
}
