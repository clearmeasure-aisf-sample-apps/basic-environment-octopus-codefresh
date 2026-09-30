using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Kit;
using Platform.Conformance.Offline.Kit.Boundaries;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-012, offline half: the bot-path audit of the checked-out history passes. <see cref="BotCommitAudit"/>, the C#
/// port of <c>scripts/checks/tool-boundaries.sh --audit-bot-commits</c>, fails when a first-parent commit by a bot
/// identity (<c>PLATFORM_BOT_AUTHORS</c>, else the design value: the Octopus image-tag step and
/// <c>octopus-argocd-pin-bot</c>) changes anything but a pin field under
/// <c>gitops/apps/&lt;app&gt;/envs/&lt;env&gt;/&lt;deployable&gt;/</c>. <c>AUDIT_DEPTH</c> sets how many first-parent commits
/// it reads (default 20). Without git the test is Inconclusive, and failed when <c>CI=true</c>; a tree without Git
/// history, or a branch without a commit, has nothing to audit and is Inconclusive. With <c>CI=true</c>,
/// <c>PLATFORM_BOT_AUTHORS</c> must be set, as for the script.
/// The pin writer (step template platform-pin-writer, octopus/step-templates/pin-writer.ps1) runs against a local
/// bare repository with the real git, a stub kubectl (<see cref="OctopusScriptRunner"/>) and a stub GitHub API behind
/// GITHUB_API_URL (#58): it mints a per-run installation token of the GitHub App aisf-pin-writer from a key generated at
/// test time, rewrites only images[].newTag of its app (dropping a digest), commits as the pin bot without the token, the
/// JWT or the key on any command line or in any output, makes no commit when the tag is already pinned, fails before any
/// commit when an App input is unset or the exchange is refused or unanswered (no personal access token fallback), and
/// fails when an image is missing or Argo CD does not report the commit. Text tests keep the inline copy of
/// <c>scripts/github/GitHubAppAuth.ps1</c> equal to the library and the template parameters equal to the script's inputs.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PinWriterTests
{
    private const string Kustomization = "gitops/apps/sandbox/envs/tdd/app/kustomization.yaml";
    private const string Script = "octopus/step-templates/pin-writer.ps1";
    private const string Helper = "scripts/github/GitHubAppAuth.ps1";
    private const string AppId = "5130403";
    private const string InstallationId = "777";
    private const string PinBot = "octopus-argocd-pin-bot <octopus-argocd-pin-bot@users.noreply.github.com>";
    private const string Seed = """
        apiVersion: kustomize.config.k8s.io/v1beta1
        kind: Kustomization
        namespace: sandbox-tdd
        resources:
          - ../../../base
        images:
          - name: acrplatform.azurecr.io/apps/sandbox/web
            newTag: "0.1.4"
            digest: sha256:0000000000000000000000000000000000000000000000000000000000000000
          - name: "acrplatform.azurecr.io/apps/sandbox/migrator"
            newName: acrplatform.azurecr.io/apps/sandbox/migrator
            newTag: 0.1.4 # pinned by release 0.1.4
          - name: acrplatform.azurecr.io/apps/sandbox/worker
            newTag: "0.1.4"
          - name: acrplatform.azurecr.io/apps/other/web
            newTag: "9.9.9"

        """;

    /// <summary>The bot commits on the checked-out history change only pin lines.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_AuditBotCommits_CheckedOutHistory_OnlyPinsChanged()
    {
        var git = KitToolbox.Require("git");
        var settings = BotAuditSettings.From(ProcessEnvironmentVariables.Instance);
        var identitiesMissingInCi = KitToolbox.IsCi && !settings.BotAuthorsFromEnvironment;
        identitiesMissingInCi.ShouldBeFalse($"{BotAuditSettings.BotAuthorsVariable} is not set, so the bot-path audit cannot run (CI=true)");

        var result = BotCommitAudit.Run(git, OctopusRepository.Root, settings.BotAuthors, settings.Depth);

        TestContext.Out.WriteLine($"bot identities: {settings.BotAuthors}{(settings.BotAuthorsFromEnvironment ? string.Empty : $" (design value; {BotAuditSettings.BotAuthorsVariable} is not set)")}");
        TestContext.Out.Write(result.Report());
        if (result.Outcome == BotAuditOutcome.Skipped)
        {
            Assert.Inconclusive(result.Report());
        }

        result.Outcome.ShouldBe(BotAuditOutcome.Passed, $"the bot-path audit failed:{Environment.NewLine}{result.Report()}");
    }

    /// <summary>
    /// New tags for an image with a digest (web) and one without (migrator): only their newTag lines change, the digest
    /// goes, another app's entry stays, the pin bot commits and pushes, and the writer waits for Synced and Healthy at
    /// that commit. The installation token reaches git through the askpass environment only.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_NewTags_CommitsOnlyTheTagsAndWaitsForArgoCd()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5, migrator=0.1.5", fixture.Key.Pem));

        result.Failed.ShouldBeFalse(result.ToString());
        Git(runner, "--git-dir", remote, "show", $"main:{Kustomization}").ShouldBe(Seed
            .Replace("    newTag: \"0.1.4\"\n    digest: sha256:0000000000000000000000000000000000000000000000000000000000000000\n", "    newTag: \"0.1.5\"\n", StringComparison.Ordinal)
            .Replace("    newTag: 0.1.4 # pinned by release 0.1.4\n", "    newTag: \"0.1.5\"\n", StringComparison.Ordinal));
        Git(runner, "--git-dir", remote, "log", "-1", "--format=%an <%ae>|%s|%b", "main").TrimEnd().ShouldBe(
            $"{PinBot}|pin(sandbox/app/tdd): web=0.1.5 migrator=0.1.5|Octopus release 0.1.5, platform-pin-writer (ADR-IR34 decision 20).");
        var head = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        result.Outputs["PinWriter.Commit"].ShouldBe(head);
        result.CallsOf("git")[0].Line.ShouldStartWith($"clone --quiet --depth 50 --branch main file://{remote} ", Case.Sensitive);
        result.Calls.ShouldNotContain(call => call.Arguments.Any(argument => argument.Contains(StubGitHubApi.AppToken, StringComparison.Ordinal)), "the token appeared on a command line");
        result.CallsOf("git").ShouldContain(call => call.Line.EndsWith(" push --quiet origin HEAD:main", StringComparison.Ordinal));
        result.CallsOf("kubectl").Single().Line.ShouldBe("--namespace argocd get application sandbox-app-tdd --output jsonpath={.status.sync.revision} {.status.sync.status} {.status.health.status}");
        result.Highlights.ShouldBe([$"Argo CD Application sandbox-app-tdd is Synced at {head} and Healthy."]);
    }

    /// <summary>A tag that is already pinned leaves the file unchanged: no commit, no push, and the writer still waits.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_TagAlreadyPinned_CommitsNothingAndStillWaits()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "worker=0.1.4", fixture.Key.Pem));

        result.Failed.ShouldBeFalse(result.ToString());
        result.Log.ShouldContain($"{Kustomization} already pins worker=0.1.4; nothing to commit.");
        Git(runner, "--git-dir", remote, "rev-parse", "main").Trim().ShouldBe(seed);
        result.CallsOf("git").ShouldNotContain(call => call.Line.Contains(" commit ", StringComparison.Ordinal) || call.Line.Contains(" push ", StringComparison.Ordinal));
        result.Outputs["PinWriter.Commit"].ShouldBe(seed);
        result.CallsOf("kubectl").Count.ShouldBe(1);
    }

    /// <summary>An image without an images[] entry of the app fails the step before any commit.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_ImageMissing_FailsWithoutACommit()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5,api=0.1.5", fixture.Key.Pem));

        result.FailMessage.ShouldBe($"{Kustomization} has no images[] entry for apps/sandbox/api.", result.ToString());
        Git(runner, "--git-dir", remote, "rev-parse", "main").Trim().ShouldBe(seed);
        result.CallsOf("kubectl").ShouldBeEmpty();
    }

    /// <summary>Argo CD that never reports Synced and Healthy at the pin commit fails the step at the timeout, naming the last state.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_ArgoCdNotSynced_FailsAtTheTimeout()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        Argo(runner, remote, " OutOfSync Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", fixture.Key.Pem));

        var head = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        result.FailMessage.ShouldBe($"Argo CD Application sandbox-app-tdd did not reach Synced and Healthy at {head} within 0 seconds (last: '{head} OutOfSync Healthy').", result.ToString());
        result.Highlights.ShouldBeEmpty();
    }

    /// <summary>
    /// The token exchange is authenticated by a JWT signed with the key of the step (issuer: the App id), asks for exactly this
    /// repository and contents write plus metadata read, happens once, and the key, the JWT and the token appear in no log,
    /// no recorded command line and no temporary file (the key is not even written to a file).
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_TokenExchange_IsNarrowedToThisRepositoryAndNothingSecretLeaks()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", fixture.Key.Pem));

        result.Failed.ShouldBeFalse(result.ToString());
        var request = fixture.Api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.PathOnly.ShouldBe($"/app/installations/{InstallationId}/access_tokens");
        fixture.Key.Verifies(request.Bearer).ShouldBeTrue("the exchange is not authenticated by a JWT signed with the key of the step");
        JsonNode.Parse(System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(request.Bearer.Split('.')[1])))!["iss"]!.GetValue<string>().ShouldBe(AppId);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        body.Select(property => property.Key).Order(StringComparer.Ordinal).ShouldBe(["permissions", "repositories"]);
        body["repositories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["basic-environment-octopus-codefresh"]);
        body["permissions"]!.AsObject().ToDictionary(property => property.Key, property => property.Value!.GetValue<string>(), StringComparer.Ordinal).ShouldBe(
            new Dictionary<string, string> { ["contents"] = "write", ["metadata"] = "read" }, ignoreOrder: true);
        AssertNoSecrets(runner, result, fixture.Key, request.Bearer);
    }

    /// <summary>
    /// The App-mode commit is authored and committed by the pin bot, an identity that matches PLATFORM_BOT_AUTHORS (the design
    /// value and the value of the environment), changes only the pin file of its own app, and the bot-path audit of the
    /// resulting history passes with that commit counted as a bot commit.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_AppModeCommit_IsThePinBotChangesOnlyItsOwnPinsAndPassesTheBotPathAudit()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", fixture.Key.Pem));

        result.Failed.ShouldBeFalse(result.ToString());
        Git(runner, "--git-dir", remote, "log", "-1", "--format=%an <%ae>%n%cn <%ce>", "main").TrimEnd().Split('\n').ShouldBe([PinBot, PinBot]);
        Git(runner, "--git-dir", remote, "diff-tree", "--no-commit-id", "--name-only", "-r", "main").Trim().ShouldBe(Kustomization);
        PosixPatterns.Ere(BotAuditSettings.DesignBotAuthors).IsMatch(PinBot).ShouldBeTrue("the design value of PLATFORM_BOT_AUTHORS does not match the pin bot");
        var settings = BotAuditSettings.From(ProcessEnvironmentVariables.Instance);
        PosixPatterns.Ere(settings.BotAuthors).IsMatch(PinBot).ShouldBeTrue($"{BotAuditSettings.BotAuthorsVariable} does not match the pin bot");

        var audited = Path.Combine(runner.Root, "audited");
        Git(runner, "clone", "--quiet", remote, audited);
        var audit = BotCommitAudit.Run(runner.RealGit!, audited, BotAuditSettings.DesignBotAuthors);
        audit.Outcome.ShouldBe(BotAuditOutcome.Passed, audit.Report());
        audit.Summary.ShouldContain("1 by platform-bots");
    }

    /// <summary>
    /// With the App id, the installation id or the key unset (or empty) the step fails naming the variable, before any
    /// request, any git call, any kubectl call and any commit; a personal access token variable of the former writer is ignored,
    /// so there is no fallback credential.
    /// </summary>
    /// <param name="unset">Comma-separated variables to leave empty.</param>
    [TestCase("PinWriter.AppId")]
    [TestCase("PinWriter.InstallationId")]
    [TestCase("PinWriter.AppPrivateKey")]
    [TestCase("PinWriter.AppId,PinWriter.InstallationId,PinWriter.AppPrivateKey")]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_AnAppInputUnset_FailsNamingTheVariableBeforeAnyCommitAndHasNoFallback(string unset)
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        var variables = Variables(remote, "web=0.1.5", fixture.Key.Pem);
        const string DecoyValue = "decoy-legacy-token-must-not-be-used";
        variables["PinWriter." + "GitToken"] = DecoyValue;
        var names = unset.Split(',');
        foreach (var name in names)
        {
            variables[name] = string.Empty;
        }

        var result = runner.Run(OctopusRepository.Read(Script), variables);

        result.FailMessage.ShouldStartWith($"The GitHub App inputs are not set: {string.Join(", ", names)}. ", Case.Sensitive);
        result.FailMessage.ShouldContain("there is no fallback credential");
        fixture.Api.Requests.ShouldBeEmpty();
        result.Calls.ShouldBeEmpty("nothing ran before the inputs were checked");
        Git(runner, "--git-dir", remote, "rev-parse", "main").Trim().ShouldBe(seed);
        result.Log.ShouldNotContain(DecoyValue);
        AssertNoSecrets(runner, result, fixture.Key);
    }

    /// <summary>An App id or installation id that is not a number fails the step before any request.</summary>
    /// <param name="name">The variable.</param>
    /// <param name="message">The message.</param>
    [TestCase("PinWriter.AppId", "PinWriter.AppId (-AppId) is not a GitHub App id.")]
    [TestCase("PinWriter.InstallationId", "PinWriter.InstallationId (-InstallationId) is not a GitHub App installation id.")]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_AppInputNotANumber_FailsBeforeAnyRequest(string name, string message)
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        var variables = Variables(remote, "web=0.1.5", fixture.Key.Pem);
        variables[name] = "not-a-number";

        var result = runner.Run(OctopusRepository.Read(Script), variables);

        result.FailMessage.ShouldBe(message, result.ToString());
        fixture.Api.Requests.ShouldBeEmpty();
        result.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// A refused exchange fails the step with the App id and the HTTP status only, before any git call, any commit and any
    /// Argo CD wait, and the transcript holds no key, JWT or token.
    /// </summary>
    /// <param name="status">HTTP status of the refused exchange.</param>
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(422)]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_ExchangeRefused_FailsWithTheStatusOnlyBeforeAnyCommit(int status)
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        fixture.Api.MintStatus = status;
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", fixture.Key.Pem));

        result.FailMessage.ShouldBe($"No installation token for GitHub App {AppId} (HTTP {status}); nothing was committed. There is no fallback credential.", result.ToString());
        result.Calls.ShouldBeEmpty("no git, kubectl or other call happens without a token");
        Git(runner, "--git-dir", remote, "rev-parse", "main").Trim().ShouldBe(seed);
        AssertNoSecrets(runner, result, fixture.Key, fixture.Api.Requests.Single().Bearer);
    }

    /// <summary>A GitHub API that does not answer fails the step with "no response" before any commit.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_NoResponseFromGitHub_FailsBeforeAnyCommit()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        runner.Environment["GITHUB_API_URL"] = $"http://127.0.0.1:{closedPort}";

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", fixture.Key.Pem));

        result.FailMessage.ShouldBe($"No installation token for GitHub App {AppId} (no response); nothing was committed. There is no fallback credential.", result.ToString());
        result.Calls.ShouldBeEmpty();
        AssertNoSecrets(runner, result, fixture.Key);
    }

    /// <summary>A key that is no RSA PEM fails the step without echoing it and without reaching the API.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_KeyIsNoPem_FailsWithoutEchoingTheKeyOrCallingTheApi()
    {
        using var runner = new OctopusScriptRunner();
        var fixture = Prepare(runner);
        var remote = SeedRemote(runner);
        // Assembled at run time so that no file of the repository holds a private key block, not even a bogus one.
        var bogusKey = "-----BEGIN " + "PRIVATE KEY-----\nQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=\n-----END " + "PRIVATE KEY-----\n";

        var result = runner.Run(OctopusRepository.Read(Script), Variables(remote, "web=0.1.5", bogusKey));

        result.FailMessage.ShouldBe($"No installation token for GitHub App {AppId} (the private key is not usable); nothing was committed. There is no fallback credential.", result.ToString());
        result.Log.ShouldNotContain("BEGIN");
        result.Log.ShouldNotContain("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo");
        fixture.Api.Requests.ShouldBeEmpty();
        result.Calls.ShouldBeEmpty();
    }

    /// <summary>The inline copy of the JWT and installation-token functions in the script equals the marked region of the library, once (drift test).</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriterScript_InlineGitHubAppAuth_EqualsTheMarkedRegionOfTheLibrary()
    {
        var canonical = OctopusRepository.MarkedRegion(Helper);

        var copies = OctopusRepository.InlineCopiesOf(OctopusRepository.Read(Script), Helper);

        canonical.ShouldNotBeEmpty();
        var copy = copies.ShouldHaveSingleItem($"{Script} inlines {Helper} exactly once");
        copy.ShouldBe(canonical, $"the inline copy of {Helper} in {Script} differs from its marked region; copy the region again");
    }

    /// <summary>
    /// The script has no second token implementation and no fallback: no openssl or JWT literal outside the copy, the key goes
    /// to the library in memory (never an environment variable, a file or a command line), and neither a personal access token
    /// nor the GitHub CLI token is read.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriterScript_OutsideTheCopy_MintsInMemoryWithNoSecondImplementationAndNoPatFallback()
    {
        var lines = OctopusRepository.Read(Script).Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == $"# >>> {Helper}");
        var end = Array.FindIndex(lines, line => line.Trim() == $"# <<< {Helper}");
        (start >= 0 && end > start).ShouldBeTrue($"{Script} has no marked copy of {Helper}");
        var outside = string.Join('\n', lines.Take(start).Concat(lines.Skip(end + 1)));

        outside.ShouldNotContain("openssl", Case.Insensitive);
        outside.ShouldNotContain("\"alg\"");
        outside.ShouldNotContain("Base64");
        outside.ShouldNotContain("/access_tokens");
        outside.ShouldNotContain("GetTempFileName");
        outside.ShouldNotContain("_PRIVATE_KEY");
        outside.ShouldNotContain("gh auth");
        outside.ShouldNotContain("GH_TOKEN");
        outside.ShouldNotContain("GITHUB_TOKEN");
        outside.ShouldNotContain("Resolve-GitHubToken");
        outside.ShouldContain("Get-GitHubAppInstallationToken -AppId $AppId -Repository @($tokenRepository) -Permission @{ contents = 'write'; metadata = 'read' } -InstallationId $InstallationId -PrivateKey $key");
        outside.ShouldContain("$tokenRepository = 'clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh'");
        outside.ShouldContain("$ProgressPreference = 'SilentlyContinue'");
        outside.ShouldContain("[string]$OctopusParameters['PinWriter.AppPrivateKey']");
        foreach (var forbidden in new[] { "${", "%{", "#{" })
        {
            outside.Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse($"{Script} contains {forbidden}, which an OCL heredoc forbids");
        }
    }

    /// <summary>
    /// The step template declares the App id and the installation id as parameters and no other input of the script that is a
    /// parameter default; the private key is a sensitive variable, never a template parameter, and no parameter holds a token.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_StepTemplateTerraform_PinWriter_DeclaresTheAppParametersAndNoKeyOrTokenParameter()
    {
        var terraform = OctopusRepository.Read("octopus/terraform/step-templates.tf");
        var resource = terraform[terraform.IndexOf("resource \"octopusdeploy_step_template\" \"pin_writer\"", StringComparison.Ordinal)..];
        var declared = Regex.Matches(resource, @"^\s*name\s*=\s*""(?<name>PinWriter\.[A-Za-z]+)""", RegexOptions.Multiline).Select(match => match.Groups["name"].Value).ToArray();
        var parameterBlock = OctopusRepository.Read(Script);
        parameterBlock = parameterBlock[parameterBlock.IndexOf("param(", StringComparison.Ordinal)..parameterBlock.IndexOf("\n)\n", StringComparison.Ordinal)];
        var read = Regex.Matches(parameterBlock, @"\$OctopusParameters\['(?<name>PinWriter\.[A-Za-z]+)'\]").Select(match => match.Groups["name"].Value).ToArray();

        read.ShouldContain("PinWriter.AppId");
        read.ShouldContain("PinWriter.InstallationId");
        declared.Order(StringComparer.Ordinal).ShouldBe(read.Order(StringComparer.Ordinal), "the template parameters and the script parameter defaults differ");
        declared.ShouldNotContain("PinWriter.AppPrivateKey");
        resource.ShouldContain("PinWriter.AppPrivateKey");
        Regex.IsMatch(resource, @"^\s*name\s*=\s*""[^""]*(Token|PrivateKey)""", RegexOptions.Multiline).ShouldBeFalse("a template parameter holds a token or a key");
    }

    /// <summary>The former personal-access-token variable of the writer is named nowhere in the tracked files: not in the script, the template, the tests, the docs or the design.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_TrackedFiles_FormerPinWriterTokenVariable_IsNamedNowhere()
    {
        // Assembled at run time so that this file does not name it either.
        var retired = "PinWriter." + "GitToken";
        var tree = new BoundaryTree(OctopusRepository.Root);

        var hits = tree.TrackedFiles(string.Empty)
            .Where(file => tree.Lines(file) is { } lines && lines.Any(line => line.Contains(retired, StringComparison.Ordinal)))
            .ToArray();

        hits.ShouldBeEmpty($"{retired} was replaced by the GitHub App inputs PinWriter.AppId, PinWriter.InstallationId and PinWriter.AppPrivateKey (#58)");
    }

    private static Dictionary<string, string> Variables(string remote, string images, string privateKey) => new(StringComparer.Ordinal)
    {
        ["PinWriter.App"] = "sandbox",
        ["PinWriter.Deployable"] = "app",
        ["PinWriter.Environment"] = "tdd",
        ["PinWriter.Images"] = images,
        ["PinWriter.RepoUrl"] = $"file://{remote}",
        ["PinWriter.AppId"] = AppId,
        ["PinWriter.InstallationId"] = InstallationId,
        ["PinWriter.AppPrivateKey"] = privateKey,
        ["PinWriter.Branch"] = "main",
        ["PinWriter.TimeoutSeconds"] = "0",
        ["Octopus.Release.Number"] = "0.1.5",
    };

    // A key generated at test time and the stub GitHub API behind GITHUB_API_URL, with the ambient GitHub App and proxy variables removed.
    private static PinFixture Prepare(OctopusScriptRunner runner)
    {
        var key = runner.Own(new TestAppKey());
        var api = runner.Own(new StubGitHubApi());
        runner.Environment["GITHUB_API_URL"] = api.Url;
        runner.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        foreach (var name in new[] { "AISF_BOARD_APP_TOKEN", "AISF_BOARD_APP_INSTALLATION_ID", "AISF_BOARD_APP_PRIVATE_KEY", "AISF_BOARD_APP_PRIVATE_KEY_PATH", "GH_TOKEN", "GITHUB_TOKEN", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY" })
        {
            runner.Environment[name] = null;
        }

        return new PinFixture(api, key);
    }

    // Neither the key, a JWT nor the installation token is in the log, in a recorded call or in a temporary file of the step.
    private static void AssertNoSecrets(OctopusScriptRunner runner, OctopusScriptResult result, TestAppKey key, params string[] jwts)
    {
        var calls = Path.Combine(runner.Root, "calls.tsv");
        var recorded = File.Exists(calls) ? File.ReadAllText(calls) : string.Empty;
        foreach (var text in new[] { result.Log, recorded })
        {
            text.ShouldNotContain("BEGIN");
            text.ShouldNotContain(key.BodyFragment);
            text.ShouldNotContain(StubGitHubApi.AppToken);
            foreach (var jwt in jwts.Where(jwt => jwt.Length > 0))
            {
                text.ShouldNotContain(jwt);
            }
        }

        Directory.EnumerateFileSystemEntries(Path.Combine(runner.Root, "tmp"), "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("the step leaves no temporary file (no key file, no askpass helper)");
    }

    /// <summary>A bare repository whose main holds <see cref="Seed"/> at the sandbox tdd kustomization.</summary>
    private static string SeedRemote(OctopusScriptRunner runner)
    {
        runner.RealGit.ShouldNotBeNull("git is needed for the pin-writer tests");
        var work = Path.Combine(runner.Root, "seed");
        var remote = Path.Combine(runner.Root, "remote.git");
        Git(runner, "init", "--quiet", "--initial-branch", "main", work);
        var file = Path.Combine(work, Kustomization.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Seed.ReplaceLineEndings("\n"));
        Git(runner, "-C", work, "add", ".");
        Git(runner, "-C", work, "-c", "user.name=seed", "-c", "user.email=seed@example.com", "commit", "--quiet", "-m", "seed");
        Git(runner, "clone", "--quiet", "--bare", work, remote);
        return remote;
    }

    /// <summary>kubectl get application answers the remote's main commit followed by <paramref name="state"/>.</summary>
    private static void Argo(OctopusScriptRunner runner, string remote, string state) =>
        runner.Answer("kubectl", "get application", new StubAnswer(Command: $"'{runner.RealGit}' --git-dir '{remote}' rev-parse main | tr -d '\\n'; printf '%s' '{state}'"));

    private static string Git(OctopusScriptRunner runner, params string[] arguments)
    {
        var result = KitToolbox.Run(runner.RealGit!, arguments, runner.Root);
        result.ExitCode.ShouldBe(0, result.Transcript);
        return result.Output.ReplaceLineEndings("\n");
    }

    private sealed record PinFixture(StubGitHubApi Api, TestAppKey Key);
}
