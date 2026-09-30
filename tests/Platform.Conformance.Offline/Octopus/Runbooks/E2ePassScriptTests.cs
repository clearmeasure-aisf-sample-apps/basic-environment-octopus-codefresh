using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-KIT-009 (offline half): runbook e2e-pass of platform-infrastructure runs the end-to-end pass on a dynamic worker,
/// so that it holds no Codefresh build slot. Under the stub Octopus runtime (<see cref="OctopusScriptRunner"/>, stub
/// curl, tar, git and timeout) its one step accepts only infra-nonprod from refs/heads/main with the Octopus key and the
/// three inputs of the GitHub App aisf-conformance set (#44), installs the pinned .NET SDK only when its SHA-512 matches,
/// fetches the environment repository at the run's commit anonymously (the repository is public: no credential in the
/// git environment or arguments), runs codefresh/platform/scripts/conformance-e2e.ps1 under a time limit with the Octopus
/// key and the App inputs (the driver mints the installation token; no GITHUB_TOKEN and no JWT code in the runbook) in
/// its environment only, attaches the summaries, TRX files and task logs, and fails the step when the pass fails.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class E2ePassScriptTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/e2e-pass.ocl";
    private const string ApiKey = "octopus-key-for-tests";
    private const string AppId = "5130402";
    private const string InstallationId = "166400001";
    private const string PrivateKey = "app-private-key-pem-for-tests";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Sdk = "fake sdk archive\n";

    /// <summary>A run from main with the key and the App inputs installs the verified SDK, fetches the commit anonymously and runs the driver.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunEndToEndPass_MainWithSecrets_InstallsTheSdkFetchesTheCommitAndRunsTheDriver()
    {
        using var runner = Stubs(new OctopusScriptRunner(), driverExit: 0);

        var result = runner.Run(Script, Variables());

        result.Failed.ShouldBeFalse(result.ToString());
        var root = runner.Root;
        result.Calls.Select(call => $"{call.Tool} {call.Line}").ShouldBe(
        [
            $"curl --fail --silent --show-error --location --retry 3 --max-time 900 --output {root}/dotnet-sdk-10.0.401-linux-x64.tar.gz https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz",
            $"tar -xzf {root}/dotnet-sdk-10.0.401-linux-x64.tar.gz -C {root}/dotnet",
            $"git init --quiet {root}/env-repo",
            $"git -C {root}/env-repo fetch --quiet --depth 1 https://github.com/example-org/env-repo.git {Commit}",
            $"git -C {root}/env-repo checkout --quiet --detach FETCH_HEAD",
            $"git -C {root}/env-repo rev-parse HEAD",
            $"timeout --kill-after=120 16200 pwsh -NoProfile -NonInteractive -File {root}/env-repo/codefresh/platform/scripts/conformance-e2e.ps1 -ResultsDirectory {root}/e2e-results",
        ], result.ToString());
        result.Calls.SelectMany(call => call.Arguments).ShouldNotContain(argument =>
            argument.Contains(PrivateKey, StringComparison.Ordinal) || argument.Contains(ApiKey, StringComparison.Ordinal) || argument.Contains("x-access-token", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(root, "seen-fetch-env")).ShouldBe("git config env: none; token: none", "the fetch of the public environment repository is anonymous");
        var seen = File.ReadAllLines(Path.Combine(root, "seen-env"));
        seen[0].ShouldBe(ApiKey);
        seen[1].ShouldBe(AppId);
        seen[2].ShouldBe(InstallationId);
        seen[3].ShouldBe(PrivateKey);
        seen[4].ShouldBe("github token unset");
        seen[5].ShouldMatch(@"^r[0-9]{8}t[0-9]{4}-11910007$");
        seen[6].ShouldBe(Path.Combine(root, "dotnet"));
        seen[7].ShouldBe("app default");
        seen[8].ShouldBe(Path.Combine(root, "env-repo"));
        File.Exists(Path.Combine(root, "dotnet-sdk-10.0.401-linux-x64.tar.gz")).ShouldBeFalse("the archive is removed once extracted");
        var run = seen[5];
        result.Artifacts.ShouldBe([$"e2e-{run}-conformance_1.trx", $"e2e-{run}-summary.json", $"e2e-{run}-summary.md", $"e2e-{run}-e2e-tdd-task.log"], result.ToString());
        result.Highlights.ShouldBe([$"End-to-end pass {run} reached prod (example-org/env-repo at {Commit}); 4 files attached."], result.ToString());
        result.Warnings.ShouldBeEmpty();
    }

    /// <summary>App.Name reaches the harness as PLATFORM_E2E_APP; without a commit of the run the head of main is fetched.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunEndToEndPass_AppAndNoCommit_PassesTheAppAndFetchesMain()
    {
        using var runner = Stubs(new OctopusScriptRunner(), driverExit: 0);
        var variables = Variables();
        variables["App.Name"] = "sandbox";
        variables.Remove("Octopus.RunbookRun.Git.Commit");

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        result.CallsOf("git").Select(call => call.Line).ShouldContain($"-C {runner.Root}/env-repo fetch --quiet --depth 1 https://github.com/example-org/env-repo.git main");
        result.Warnings.ShouldBe(["Octopus.RunbookRun.Git.Commit is '', not a commit; running the head of main."]);
        File.ReadAllLines(Path.Combine(runner.Root, "seen-env"))[7].ShouldBe("sandbox");
    }

    /// <summary>A failed or timed-out pass fails the step with the results still attached.</summary>
    /// <param name="exitCode">Exit code of the driver under timeout.</param>
    /// <param name="failure">Start of the Fail-Step message.</param>
    [TestCase(1, "failed (exit 1): summary.md, the TRX files and the task logs are attached (4 files).")]
    [TestCase(124, "exceeded E2E.TimeoutMinutes (270 minutes); 4 files attached.")]
    [TestCase(2, "did not start: a prerequisite of conformance-e2e.ps1 is missing (see the log above).")]
    [Capability("CAP-KIT-009")]
    public void Should_RunEndToEndPass_DriverFails_FailsTheStepWithTheResultsAttached(int exitCode, string failure)
    {
        using var runner = Stubs(new OctopusScriptRunner(), driverExit: exitCode);

        var result = runner.Run(Script, Variables());

        result.Failed.ShouldBeTrue(result.ToString());
        result.FailMessage.ShouldNotBeNull();
        result.FailMessage.ShouldMatch(@"^End-to-end pass r[0-9]{8}t[0-9]{4}-11910007 ");
        result.FailMessage.ShouldEndWith(failure);
        result.Artifacts.Count.ShouldBe(4, result.ToString());
        result.Highlights.ShouldBeEmpty();
    }

    /// <summary>A wrong environment, branch, secret or setting fails the step before any download, clone or run.</summary>
    /// <param name="name">Variable to change.</param>
    /// <param name="value">Its value; <c>null</c> removes it.</param>
    /// <param name="failure">Part of the Fail-Step message.</param>
    [TestCase("Octopus.Environment.Name", "infra-prod", "e2e-pass runs in infra-nonprod only (environment 'infra-prod', Environment.Class 'nonprod').")]
    [TestCase("Environment.Class", "prod", "e2e-pass runs in infra-nonprod only (environment 'infra-nonprod', Environment.Class 'prod').")]
    [TestCase("Octopus.RunbookRun.Git.Ref", "refs/heads/feature", "e2e-pass runs only from refs/heads/main: it deploys app #1 to prod with the platform key (this run: 'refs/heads/feature').")]
    [TestCase("Platform.OctopusApiKey", null, "Platform.OctopusApiKey is empty in this step")]
    [TestCase("E2E.GitHubAppId", "", "The GitHub App aisf-conformance is not configured (PENDING owner setup, #44)")]
    [TestCase("E2E.GitHubAppId", "not-a-number", "The GitHub App aisf-conformance is not configured (PENDING owner setup, #44)")]
    [TestCase("E2E.GitHubAppInstallationId", "", "The GitHub App aisf-conformance is not configured (PENDING owner setup, #44)")]
    [TestCase("E2E.GitHubAppInstallationId", null, "The GitHub App aisf-conformance is not configured (PENDING owner setup, #44)")]
    [TestCase("E2E.GitHubAppPrivateKey", "", "The GitHub App aisf-conformance is not configured (PENDING owner setup, #44)")]
    [TestCase("E2E.GitHubAppPrivateKey", null, "E2E.GitHubAppPrivateKey of platform-infrastructure (TF_VAR_e2e_github_app_id")]
    [TestCase("E2E.EnvRepository", "https://github.com/example-org/env-repo", "E2E.EnvRepository must be owner/name")]
    [TestCase("E2E.DotnetSdkVersion", "8.0.100", "E2E.DotnetSdkVersion must be a .NET 10 SDK version")]
    [TestCase("E2E.DotnetSdkSha512", "abc", "E2E.DotnetSdkSha512 must be the SHA-512 of the SDK archive")]
    [TestCase("E2E.TimeoutMinutes", "0", "E2E.TimeoutMinutes must be a whole number of minutes from 1 to 9999; got '0'.")]
    [TestCase("App.Name", "Bad App", "App.Name must be empty (app #1) or an app slug")]
    [Capability("CAP-KIT-009")]
    public void Should_RunEndToEndPass_WrongSourceSecretOrSetting_FailsBeforeAnyCall(string name, string? value, string failure)
    {
        using var runner = Stubs(new OctopusScriptRunner(), driverExit: 0);
        var variables = Variables();
        if (value is null)
        {
            variables.Remove(name);
        }
        else
        {
            variables[name] = value;
        }

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeTrue(result.ToString());
        result.FailMessage.ShouldNotBeNull();
        result.FailMessage.ShouldContain(failure);
        result.Calls.ShouldBeEmpty(result.ToString());
    }

    /// <summary>
    /// The runbook holds no token and no JWT code (#44): no GITHUB_TOKEN, no git http extraheader, no personal access token
    /// variable; the App inputs it hands the driver are exactly the four names it removes again afterwards.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_ReadRunbook_NoGitHubTokenNoExtraHeaderAndTheAppInputsAreRemovedAfterwards()
    {
        var script = Script;

        script.ShouldNotContain("GITHUB_TOKEN", Case.Sensitive);
        script.ShouldNotContain("extraheader", Case.Insensitive);
        script.ShouldNotContain("GIT_CONFIG", Case.Sensitive);
        script.ShouldNotContain("x-access-token", Case.Insensitive);
        script.ShouldNotContain("RS256", Case.Sensitive);
        var exported = Regex.Matches(script, @"\$env:(AISF_CONFORMANCE_APP_[A-Z_]+) = ").Select(match => match.Groups[1].Value).ToArray();
        exported.ShouldBe(["AISF_CONFORMANCE_APP_ID", "AISF_CONFORMANCE_APP_INSTALLATION_ID", "AISF_CONFORMANCE_APP_PRIVATE_KEY"]);
        var removal = Regex.Match(script, @"Remove-Item -Path (?<paths>[^\r\n]*Env:AISF_CONFORMANCE_APP_PRIVATE_KEY[^\r\n]*)").Groups["paths"].Value;
        removal.ShouldNotBeNullOrEmpty("the App inputs are removed in the finally block");
        foreach (var name in exported.Append("OCTOPUS_API_KEY"))
        {
            removal.ShouldContain($"'Env:{name}'", Case.Sensitive);
        }
    }

    /// <summary>An SDK archive whose SHA-512 differs from E2E.DotnetSdkSha512 is never extracted, and nothing runs.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_RunEndToEndPass_SdkChecksumDiffers_FailsBeforeExtracting()
    {
        using var runner = Stubs(new OctopusScriptRunner(), driverExit: 0);
        var variables = Variables();
        variables["E2E.DotnetSdkSha512"] = new string('0', 128);

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeTrue(result.ToString());
        result.FailMessage.ShouldBe($"The .NET SDK 10.0.401 archive has SHA-512 {Sha512(Sdk)}, not E2E.DotnetSdkSha512; nothing was run.");
        result.Calls.Select(call => call.Tool).ShouldBe(["curl"], result.ToString());
    }

    /// <summary>The runbook pins the SDK the platform image carries, so the pass builds with the toolchain of the suite.</summary>
    [Test]
    [Capability("CAP-KIT-009")]
    public void Should_ReadVariables_DotnetSdkVersion_MatchesThePlatformImage()
    {
        var variables = OctopusRepository.Read(".octopus/platform-infrastructure/variables.ocl");
        var dockerfile = OctopusRepository.Read("containers/platform/ci-dotnet/Dockerfile");

        var pinned = Regex.Match(variables, @"variable ""E2E\.DotnetSdkVersion"" \{\s*value ""(?<version>[^""]+)""");
        var image = Regex.Match(dockerfile, @"^FROM mcr\.microsoft\.com/dotnet/sdk:(?<version>[0-9.]+)-", RegexOptions.Multiline);

        pinned.Success.ShouldBeTrue("variables.ocl pins E2E.DotnetSdkVersion");
        image.Success.ShouldBeTrue("the ci-dotnet Dockerfile starts from mcr.microsoft.com/dotnet/sdk:<version>-");
        pinned.Groups["version"].Value.ShouldBe(image.Groups["version"].Value);
        Regex.IsMatch(variables, @"variable ""E2E\.DotnetSdkSha512"" \{\s*value ""[0-9a-f]{128}""").ShouldBeTrue("variables.ocl pins the SHA-512 of that SDK archive");
    }

    private static string Script => OctopusScriptRunner.ScriptBody(Runbook, "run-end-to-end-pass");

    private static string Sha512(string content) => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["Octopus.Environment.Name"] = "infra-nonprod",
        ["Environment.Class"] = "nonprod",
        ["Octopus.RunbookRun.Git.Ref"] = "refs/heads/main",
        ["Octopus.RunbookRun.Git.Commit"] = Commit,
        ["Octopus.Task.Id"] = "ServerTasks-11910007",
        ["Platform.OctopusApiKey"] = ApiKey,
        ["E2E.GitHubAppId"] = AppId,
        ["E2E.GitHubAppInstallationId"] = InstallationId,
        ["E2E.GitHubAppPrivateKey"] = PrivateKey,
        ["E2E.EnvRepository"] = "example-org/env-repo",
        ["E2E.DotnetSdkVersion"] = "10.0.401",
        ["E2E.DotnetSdkSha512"] = Sha512(Sdk),
        ["E2E.TimeoutMinutes"] = "270",
        ["App.Name"] = string.Empty,
    };

    // curl writes the fake archive, git init creates the driver, git fetch records the header it sees, and timeout (the
    // driver) records its environment and working folder, then writes the results of a run.
    private static OctopusScriptRunner Stubs(OctopusScriptRunner runner, int driverExit)
    {
        var results = "$OCTOPUS_STUB_DIR/e2e-results";
        return runner
            .Answer("curl", "--output", new StubAnswer(Command: $"printf '{Sdk.TrimEnd('\n')}\\n' > \"$OCTOPUS_STUB_DIR/dotnet-sdk-10.0.401-linux-x64.tar.gz\""))
            .Answer("tar", string.Empty, new StubAnswer())
            .Answer("git", " init ", new StubAnswer(Command: "mkdir -p \"$OCTOPUS_STUB_DIR/env-repo/codefresh/platform/scripts\" && : > \"$OCTOPUS_STUB_DIR/env-repo/codefresh/platform/scripts/conformance-e2e.ps1\""))
            .Answer("git", " fetch ", new StubAnswer(Command: "printf 'git config env: %s; token: %s' \"${GIT_CONFIG_COUNT:-none}${GIT_CONFIG_VALUE_0:-}\" \"${GITHUB_TOKEN:-none}${AISF_CONFORMANCE_APP_PRIVATE_KEY:-}\" > \"$OCTOPUS_STUB_DIR/seen-fetch-env\""))
            .Answer("git", " checkout ", new StubAnswer())
            .Answer("git", " rev-parse HEAD ", new StubAnswer(Output: $"{Commit}\n"))
            .Answer("timeout", string.Empty, new StubAnswer(ExitCode: driverExit, Command: $$"""
                {
                  printf '%s\n' "$OCTOPUS_API_KEY" "$AISF_CONFORMANCE_APP_ID" "$AISF_CONFORMANCE_APP_INSTALLATION_ID" "$AISF_CONFORMANCE_APP_PRIVATE_KEY"
                  if [ -n "${GITHUB_TOKEN:-}" ]; then echo 'github token set'; else echo 'github token unset'; fi
                  printf '%s\n' "$PLATFORM_RUN_ID" "$DOTNET_ROOT"
                  printf '%s\n' "$(printenv PLATFORM_E2E_APP || echo 'app default')" "$(pwd)"
                } > "$OCTOPUS_STUB_DIR/seen-env"
                mkdir -p "{{results}}/artifacts/r1"
                printf '# summary\n' > "{{results}}/summary.md"
                printf '{}\n' > "{{results}}/summary.json"
                printf '<TestRun/>\n' > "{{results}}/conformance_1.trx"
                printf 'nonprod=Running\n' > "{{results}}/power-before.txt"
                printf 'task log\n' > "{{results}}/artifacts/r1/e2e-tdd-task.log"
                mkdir -p "{{results}}/progress" && printf '{}\n' > "{{results}}/progress/Platform.Conformance.Tests.json"
                """));
    }
}
