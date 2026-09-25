using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Kit;
using Platform.Conformance.Offline.Kit.Boundaries;

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
/// The fallback writer (step template platform-pin-writer, octopus/step-templates/pin-writer.ps1) runs against a local
/// bare repository with the real git and a stub kubectl (<see cref="OctopusScriptRunner"/>): it rewrites only
/// images[].newTag of its app (dropping a digest), commits as the pin bot without the token on any command line, makes no
/// commit when the tag is already pinned, and fails when an image is missing or Argo CD does not report the commit.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PinWriterTests
{
    private const string Kustomization = "gitops/apps/sandbox/envs/tdd/app/kustomization.yaml";
    private const string Token = "stub-token-offline";
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
    /// that commit. The token reaches git through the askpass environment only.
    /// </summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_PinWriter_NewTags_CommitsOnlyTheTagsAndWaitsForArgoCd()
    {
        using var runner = new OctopusScriptRunner();
        var remote = SeedRemote(runner);
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/pin-writer.ps1"), Variables(remote, "web=0.1.5, migrator=0.1.5"));

        result.Failed.ShouldBeFalse(result.ToString());
        Git(runner, "--git-dir", remote, "show", $"main:{Kustomization}").ShouldBe(Seed
            .Replace("    newTag: \"0.1.4\"\n    digest: sha256:0000000000000000000000000000000000000000000000000000000000000000\n", "    newTag: \"0.1.5\"\n", StringComparison.Ordinal)
            .Replace("    newTag: 0.1.4 # pinned by release 0.1.4\n", "    newTag: \"0.1.5\"\n", StringComparison.Ordinal));
        Git(runner, "--git-dir", remote, "log", "-1", "--format=%an <%ae>|%s|%b", "main").TrimEnd().ShouldBe(
            "octopus-argocd-pin-bot <octopus-argocd-pin-bot@users.noreply.github.com>|pin(sandbox/app/tdd): web=0.1.5 migrator=0.1.5|Octopus release 0.1.5, platform-pin-writer (ADR-IR34 decision 20).");
        var head = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        result.Outputs["PinWriter.Commit"].ShouldBe(head);
        result.CallsOf("git")[0].Line.ShouldStartWith($"clone --quiet --depth 50 --branch main file://{remote} ", Case.Sensitive);
        result.Calls.ShouldNotContain(call => call.Arguments.Any(argument => argument.Contains(Token, StringComparison.Ordinal)), "the token appeared on a command line");
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
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/pin-writer.ps1"), Variables(remote, "worker=0.1.4"));

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
        var remote = SeedRemote(runner);
        var seed = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        Argo(runner, remote, " Synced Healthy");

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/pin-writer.ps1"), Variables(remote, "web=0.1.5,api=0.1.5"));

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
        var remote = SeedRemote(runner);
        Argo(runner, remote, " OutOfSync Healthy");

        var result = runner.Run(OctopusRepository.Read("octopus/step-templates/pin-writer.ps1"), Variables(remote, "web=0.1.5"));

        var head = Git(runner, "--git-dir", remote, "rev-parse", "main").Trim();
        result.FailMessage.ShouldBe($"Argo CD Application sandbox-app-tdd did not reach Synced and Healthy at {head} within 0 seconds (last: '{head} OutOfSync Healthy').", result.ToString());
        result.Highlights.ShouldBeEmpty();
    }

    private static Dictionary<string, string> Variables(string remote, string images) => new(StringComparer.Ordinal)
    {
        ["PinWriter.App"] = "sandbox",
        ["PinWriter.Deployable"] = "app",
        ["PinWriter.Environment"] = "tdd",
        ["PinWriter.Images"] = images,
        ["PinWriter.RepoUrl"] = $"file://{remote}",
        ["PinWriter.Branch"] = "main",
        ["PinWriter.TimeoutSeconds"] = "0",
        ["PinWriter.GitToken"] = Token,
        ["Octopus.Release.Number"] = "0.1.5",
    };

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
}
