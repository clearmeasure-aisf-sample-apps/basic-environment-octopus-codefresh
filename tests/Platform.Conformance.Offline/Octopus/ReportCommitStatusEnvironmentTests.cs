using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-019: step Report platform status (<c>report-commit-status</c>) of the workorders process reports every
/// environment it deploys to, on the app commit of the release: context <c>platform/tdd</c>, <c>platform/uat</c> or
/// <c>platform/prod</c>, computed from <c>Octopus.Environment.Name</c> and from nothing else, with a description that names
/// only what that environment runs (the acceptance tests and their counts in tdd alone). The step is the last of the process,
/// runs always and in all three environments, so a failed deployment reports <c>failure</c>. The real step body runs under the
/// stub Octopus runtime of <see cref="OctopusScriptRunner"/> against a stub GitHub API; nothing reaches a live system.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public partial class ReportCommitStatusEnvironmentTests
{
    private const string Process = ".octopus/apps/workorders/workorders/deployment_process.ocl";
    private const string Variables = ".octopus/apps/workorders/workorders/variables.ocl";
    private const string Contracts = "contracts/platform-contracts.yaml";
    private const string Slug = "report-commit-status";
    private const string SummaryVariable = "Octopus.Action[Acceptance tests (TDD only)].Output.AcceptanceSummary";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string[] Environments = ["tdd", "uat", "prod"];

    // A throw-away key generated once per run and never committed: the step signs a real RS256 JWT with it.
    private static readonly string StatusAppKey = GenerateKey();

    /// <summary>Each environment posts its own context with its own description, success or failure; a leftover GitHub.StatusContext changes nothing.</summary>
    /// <param name="environment">Octopus.Environment.Name.</param>
    /// <param name="error">Octopus.Deployment.Error.</param>
    /// <param name="state">The expected state.</param>
    /// <param name="description">The expected description.</param>
    [TestCase("tdd", "", "success", "TDD deployment and acceptance tests passed", TestName = "{m}(tdd, passed)")]
    [TestCase("tdd", "The step failed", "failure", "TDD deployment or acceptance tests failed", TestName = "{m}(tdd, failed)")]
    [TestCase("uat", "", "success", "UAT deployment and smoke test passed", TestName = "{m}(uat, passed)")]
    [TestCase("uat", "The step failed", "failure", "UAT deployment failed", TestName = "{m}(uat, failed)")]
    [TestCase("prod", "", "success", "Prod backup, deployment and smoke test passed", TestName = "{m}(prod, passed)")]
    [TestCase("prod", "The step failed", "failure", "Prod deployment failed", TestName = "{m}(prod, failed)")]
    [Capability("CAP-OCT-019")]
    public void Should_ReportCommitStatus_EachEnvironment_PostsItsOwnContextStateAndDescription(string environment, string error, string state, string description)
    {
        using var runner = CommitStatus(new OctopusScriptRunner());
        var variables = CommitStatusVariables(environment, error);
        variables["GitHub.StatusContext"] = "platform/elsewhere";

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        var record = Record(runner);
        record.ShouldContain($"https://api.github.com/repos/example-org/workorders/statuses/{Commit}");
        Posted(record, "context").ShouldBe($"platform/{environment}");
        Posted(record, "state").ShouldBe(state);
        Posted(record, "description").ShouldBe(description);
        Posted(record, "target_url").ShouldBe("https://octopus.example.test/app#/deployments/1");
        result.Log.ShouldContain($"Posted platform/{environment}={state} to example-org/workorders@{Commit}.");
    }

    /// <summary>uat and prod run no acceptance tests: their descriptions never carry counts, even when a summary variable is present.</summary>
    /// <param name="environment">Octopus.Environment.Name.</param>
    /// <param name="error">Octopus.Deployment.Error.</param>
    /// <param name="description">The expected description.</param>
    [TestCase("uat", "", "UAT deployment and smoke test passed", TestName = "{m}(uat, passed)")]
    [TestCase("uat", "The step failed", "UAT deployment failed", TestName = "{m}(uat, failed)")]
    [TestCase("prod", "", "Prod backup, deployment and smoke test passed", TestName = "{m}(prod, passed)")]
    [TestCase("prod", "The step failed", "Prod deployment failed", TestName = "{m}(prod, failed)")]
    [Capability("CAP-OCT-019")]
    public void Should_ReportCommitStatus_UatAndProd_CarryNoAcceptanceCounts(string environment, string error, string description)
    {
        using var runner = CommitStatus(new OctopusScriptRunner());
        var variables = CommitStatusVariables(environment, error);
        variables[SummaryVariable] = "Acceptance (tdd): 42 passed, 0 failed, 3 skipped of 45 in 6m12s";

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        var posted = Posted(Record(runner), "description");
        posted.ShouldBe(description);
        posted.ShouldNotContain("acceptance", Case.Insensitive);
        posted.ShouldNotContain("42 passed");
    }

    /// <summary>Channel Hotfix starts in uat: its uat and prod statuses say that tdd was skipped, within 140 characters; channel Default says nothing.</summary>
    /// <param name="environment">Octopus.Environment.Name.</param>
    /// <param name="channel">Octopus.Release.Channel.Name.</param>
    /// <param name="error">Octopus.Deployment.Error.</param>
    /// <param name="description">The expected description.</param>
    [TestCase("uat", "Hotfix", "", "UAT deployment and smoke test passed (Hotfix channel, tdd skipped)", TestName = "{m}(uat, Hotfix, passed)")]
    [TestCase("uat", "Hotfix", "The step failed", "UAT deployment failed (Hotfix channel, tdd skipped)", TestName = "{m}(uat, Hotfix, failed)")]
    [TestCase("prod", "Hotfix", "", "Prod backup, deployment and smoke test passed (Hotfix channel, tdd skipped)", TestName = "{m}(prod, Hotfix, passed)")]
    [TestCase("prod", "Hotfix", "The step failed", "Prod deployment failed (Hotfix channel, tdd skipped)", TestName = "{m}(prod, Hotfix, failed)")]
    [TestCase("uat", "Default", "", "UAT deployment and smoke test passed", TestName = "{m}(uat, Default)")]
    [TestCase("prod", "Default", "", "Prod backup, deployment and smoke test passed", TestName = "{m}(prod, Default)")]
    [Capability("CAP-OCT-019")]
    public void Should_ReportCommitStatus_HotfixChannel_SaysThatTddWasSkipped(string environment, string channel, string error, string description)
    {
        using var runner = CommitStatus(new OctopusScriptRunner());
        var variables = CommitStatusVariables(environment, error);
        variables["Octopus.Release.Channel.Name"] = channel;

        var result = runner.Run(Script, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        var record = Record(runner);
        Posted(record, "context").ShouldBe($"platform/{environment}");
        Posted(record, "description").ShouldBe(description);
        description.Length.ShouldBeLessThanOrEqualTo(140);
    }

    /// <summary>An environment the step has no status for fails the step before any request: no token is minted and nothing is posted.</summary>
    /// <param name="environment">Octopus.Environment.Name.</param>
    [TestCase("staging", TestName = "{m}(staging)")]
    [TestCase("TDD", TestName = "{m}(upper case)")]
    [TestCase("", TestName = "{m}(empty)")]
    [Capability("CAP-OCT-019")]
    public void When_ReportCommitStatus_EnvironmentIsNotTddUatOrProd_FailsBeforeAnyRequest(string environment)
    {
        using var runner = new OctopusScriptRunner();
        var api = runner.Own(new StubGitHubApi());
        CommitStatus(runner, api);

        var result = runner.Run(Script, CommitStatusVariables(environment, string.Empty));

        result.FailMessage.ShouldBe($"No commit status is defined for environment '{environment}' (the step reports tdd, uat and prod).");
        api.Requests.ShouldBeEmpty();
        result.Calls.ShouldBeEmpty();
    }

    /// <summary>The step is the last of the process, required, runs always (a failed deployment still reports) and in tdd, uat and prod.</summary>
    [Test]
    [Capability("CAP-OCT-019")]
    public void Should_ReadWorkorders_ReportCommitStatus_IsTheLastStepAndRunsAlwaysInTddUatAndProd()
    {
        var steps = OctopusRepository.Steps(OctopusRepository.Read(Process));
        var step = steps[^1];

        step.Slug.ShouldBe(Slug, "the status step must stay the last step: only then does it see the outcome of every other step");
        steps.Count(candidate => candidate.Slug == Slug).ShouldBe(1);
        var header = step.Text[..step.Text.IndexOf("Octopus.Action.Script.ScriptBody", StringComparison.Ordinal)];
        header.ShouldContain("    name = \"Report platform status\"\n");
        header.ShouldContain("    condition = \"Always\"\n");
        header.ShouldContain("        is_required = true\n");
        var scoped = EnvironmentList().Match(header);
        scoped.Success.ShouldBeTrue("the step names its environments");
        Regex.Matches(scoped.Groups["items"].Value, "\"(?<item>[^\"]*)\"").Select(item => item.Groups["item"].Value).ShouldBe(Environments);
    }

    /// <summary>The context has one source, the environment name: the step reads no context variable, the project declares none, and the contract lists exactly the three contexts.</summary>
    [Test]
    [Capability("CAP-OCT-019")]
    public void Should_ReadWorkorders_StatusContext_ComesFromTheEnvironmentNameAlone()
    {
        var step = OctopusRepository.Steps(OctopusRepository.Read(Process)).Single(candidate => candidate.Slug == Slug).Text;

        step.ShouldContain("$environment = [string]$OctopusParameters['Octopus.Environment.Name']");
        step.ShouldContain("$context = \"platform/$environment\"");
        step.ShouldNotContain("GitHub.StatusContext");
        OctopusRepository.Read(Variables).ShouldNotContain("GitHub.StatusContext");
        OctopusRepository.Read(Contracts).ShouldContain("\n  octopus: [platform/tdd, platform/uat, platform/prod]\n");
    }

    /// <summary>Every output the step reads from another step names a step of the process (outputs are addressed by step name).</summary>
    [Test]
    [Capability("CAP-OCT-019")]
    public void Should_ReadWorkorders_ReportCommitStatus_ReadsOutputsOfExistingStepsOnly()
    {
        var process = OctopusRepository.Read(Process);
        var step = OctopusRepository.Steps(process).Single(candidate => candidate.Slug == Slug).Text;
        var names = StepName().Matches(process).Select(match => match.Groups["name"].Value).ToArray();

        var read = ActionOutput().Matches(step).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();

        read.ShouldBe(["Acceptance tests (TDD only)"]);
        read.ShouldBeSubsetOf(names);
    }

    private static string GenerateKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static string Script => OctopusScriptRunner.ScriptBody(Process, Slug);

    private static Dictionary<string, string> CommitStatusVariables(string environment, string error) => new(StringComparer.Ordinal)
    {
        ["GitHub.StatusEnabled"] = "True",
        ["GitHub.StatusAppPrivateKey"] = StatusAppKey,
        ["GitHub.StatusAppId"] = "1",
        ["GitHub.StatusAppInstallationId"] = "2",
        ["GitHub.AppRepository"] = "example-org/workorders",
        ["Octopus.Environment.Name"] = environment,
        ["Octopus.Release.Channel.Name"] = "Default",
        ["Octopus.Release.Notes"] = $"app-commit: {Commit}\n",
        ["Octopus.Deployment.Error"] = error,
        ["Octopus.Web.ServerUri"] = "https://octopus.example.test",
        ["Octopus.Web.DeploymentLink"] = "/app#/deployments/1",
    };

    // The step signs its JWT in memory and exchanges it with the stub GitHub API behind GITHUB_API_URL; curl only posts the status.
    private static OctopusScriptRunner CommitStatus(OctopusScriptRunner runner, StubGitHubApi? api = null)
    {
        api ??= runner.Own(new StubGitHubApi());
        runner.Environment["GITHUB_API_URL"] = api.Url;
        runner.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        runner.Environment["AISF_BOARD_APP_INSTALLATION_ID"] = null;
        return runner.Answer("curl", "/statuses/", new StubAnswer());
    }

    private static string Record(OctopusScriptRunner runner) => File.ReadAllText(Path.Combine(runner.Root, "calls.tsv"));

    // The JSON payload spans several lines of the stub's call record, so a field is read from the raw record.
    private static string Posted(string record, string field)
    {
        var match = Regex.Match(record, "\"" + Regex.Escape(field) + "\": \"(?<text>[^\"]*)\"");
        match.Success.ShouldBeTrue($"no {field} in the posted status:{Environment.NewLine}{record}");
        return match.Groups["text"].Value;
    }

    [GeneratedRegex(@"^[ \t]*environments = \[(?<items>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex EnvironmentList();

    [GeneratedRegex(@"^    name = ""(?<name>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex StepName();

    [GeneratedRegex(@"Octopus\.Action\[(?<name>[^\]]+)\]")]
    private static partial Regex ActionOutput();
}
