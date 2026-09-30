using System.Text;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-017: step Acceptance tests (TDD only) of the workorders process puts the counts of its TRX files on the task
/// summary (Write-Highlight) and in output variable AcceptanceSummary, highlights the first ten failed tests, and still
/// passes or fails on the exit code of dotnet test alone: an [LlmTest] warning (NotExecuted in TRX) counts as skipped and
/// as an LLM warning, never as a failure. Step Report platform/tdd status appends the counts to its description, at most
/// 140 characters. Both steps run under the stub Octopus runtime of <see cref="OctopusScriptRunner"/>, with a stub
/// coreutils timeout standing in for dotnet test.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class AcceptanceSummaryTests
{
    private const string Process = ".octopus/apps/workorders/workorders/deployment_process.ocl";
    private const string SummaryVariable = "Octopus.Action[Acceptance tests (TDD only)].Output.AcceptanceSummary";
    private const string LlmWarning = "LLM-dependent test did not pass after 3 attempt(s); reported as a warning instead of a failure. Last outcome: Failed.";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    // A throw-away key generated once per run and never committed: the step signs a real RS256 JWT with it.
    private static readonly string StatusAppKey = GenerateKey();

    /// <summary>A passing run with a skipped test and an [LlmTest] warning passes and reports both as skipped.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void Should_AcceptanceTests_PassingRunWithLlmWarning_HighlightsTheCountsAndPasses()
    {
        using var runner = Acceptance(new OctopusScriptRunner(), exitCode: 0, Trx(passed: 3, failed: 0, ignored: 1, llmWarnings: 1));

        var result = runner.Run(AcceptanceScript, AcceptanceVariables(runner));

        result.Failed.ShouldBeFalse(result.ToString());
        result.Highlights.Count.ShouldBe(1, result.ToString());
        result.Highlights[0].ShouldMatch(@"^Acceptance \(tdd\): 3 passed, 0 failed, 2 skipped \(1 LLM warnings\) of 5 in [0-9]+m[0-5][0-9]s$");
        result.Outputs["AcceptanceSummary"].ShouldBe(result.Highlights[0]);
        result.Artifacts.ShouldBe(["acceptance-tdd-run.trx"]);
        result.Warnings.ShouldBeEmpty();
    }

    /// <summary>Twelve failures fail the step as before and highlight the first ten names.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void When_AcceptanceTests_TestsFail_HighlightsTheFirstTenAndFailsTheStep()
    {
        using var runner = Acceptance(new OctopusScriptRunner(), exitCode: 1, Trx(passed: 30, failed: 12, ignored: 0, llmWarnings: 0));

        var result = runner.Run(AcceptanceScript, AcceptanceVariables(runner));

        result.Failed.ShouldBeTrue(result.ToString());
        result.FailMessage.ShouldBe("Acceptance tests failed (exit code 1); TRX files are attached as artifacts.");
        result.Highlights.Count.ShouldBe(2, result.ToString());
        result.Highlights[0].ShouldMatch(@"^Acceptance \(tdd\): 30 passed, 12 failed, 0 skipped of 42 in [0-9]+m[0-5][0-9]s$");
        result.Highlights[1].ShouldBe("Failed acceptance tests (10 of 12): " + string.Join("; ", Enumerable.Range(1, 10).Select(number => $"Should_Fail_{number}")));
        result.Outputs["AcceptanceSummary"].ShouldBe(result.Highlights[0]);
    }

    /// <summary>The exit code decides: a non-zero exit fails even when the TRX shows no failure, and a timeout without TRX says so.</summary>
    /// <param name="exitCode">Exit code of timeout (dotnet test).</param>
    /// <param name="writeTrx">Whether the run leaves a TRX file.</param>
    /// <param name="summary">Start of the highlighted summary.</param>
    /// <param name="failure">The Fail-Step message.</param>
    [TestCase(1, true, "Acceptance (tdd): 3 passed, 0 failed, 0 skipped of 3 in ", "Acceptance tests failed (exit code 1); TRX files are attached as artifacts.", TestName = "{m}(exit 1, TRX without failures)")]
    [TestCase(124, false, "Acceptance (tdd): no TRX results after ", "Acceptance tests exceeded the 30-minute timeout.", TestName = "{m}(timeout, no TRX)")]
    [Capability("CAP-OCT-017")]
    public void When_AcceptanceTests_ExitCodeNonZero_FailsTheStepWhateverTheCounts(int exitCode, bool writeTrx, string summary, string failure)
    {
        using var runner = Acceptance(new OctopusScriptRunner(), exitCode, writeTrx ? Trx(passed: 3, failed: 0, ignored: 0, llmWarnings: 0) : null);

        var result = runner.Run(AcceptanceScript, AcceptanceVariables(runner));

        result.Failed.ShouldBeTrue(result.ToString());
        result.FailMessage.ShouldBe(failure);
        result.Highlights.Count.ShouldBe(1, result.ToString());
        result.Highlights[0].ShouldStartWith(summary);
        result.Outputs["AcceptanceSummary"].ShouldBe(result.Highlights[0]);
    }

    /// <summary>A TRX file that is no XML only warns: the passing run still passes.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void When_AcceptanceTests_TrxUnreadable_WarnsAndKeepsTheOutcome()
    {
        using var runner = Acceptance(new OctopusScriptRunner(), exitCode: 0, "<TestRun");

        var result = runner.Run(AcceptanceScript, AcceptanceVariables(runner));

        result.Failed.ShouldBeFalse(result.ToString());
        result.Warnings.Count.ShouldBe(1, result.ToString());
        result.Warnings[0].ShouldStartWith("Could not read the acceptance TRX counts: ");
        result.Highlights.Count.ShouldBe(1, result.ToString());
        result.Highlights[0].ShouldStartWith("Acceptance (tdd): no TRX results after ");
    }

    /// <summary>The commit status carries the counts after its description, cut to 140 characters; without them it is unchanged.</summary>
    /// <param name="error">Octopus.Deployment.Error.</param>
    /// <param name="summary">The AcceptanceSummary output, or <c>null</c> when the step did not run.</param>
    /// <param name="description">The expected description.</param>
    [TestCase("", "Acceptance (tdd): 42 passed, 0 failed, 3 skipped of 45 in 6m12s", "TDD deployment and acceptance tests passed: 42 passed, 0 failed, 3 skipped of 45 in 6m12s", TestName = "{m}(passed)")]
    [TestCase("failed", "Acceptance (tdd): 40 passed, 2 failed, 3 skipped (1 LLM warnings) of 45 in 6m12s", "TDD deployment or acceptance tests failed: 40 passed, 2 failed, 3 skipped (1 LLM warnings) of 45 in 6m12s", TestName = "{m}(failed)")]
    [TestCase("failed", null, "TDD deployment or acceptance tests failed", TestName = "{m}(acceptance did not run)")]
    [Capability("CAP-OCT-017")]
    public void Should_ReportCommitStatus_AcceptanceSummary_AppendsTheCounts(string error, string? summary, string description)
    {
        using var runner = CommitStatus(new OctopusScriptRunner());
        var variables = CommitStatusVariables(error);
        if (summary is not null)
        {
            variables[SummaryVariable] = summary;
        }

        var result = runner.Run(CommitStatusScript, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        PostedDescription(runner).ShouldBe(description);
    }

    /// <summary>A long summary is cut so that the description stays within the 140 characters GitHub accepts.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void Should_ReportCommitStatus_LongSummary_CutsTheDescriptionTo140Characters()
    {
        using var runner = CommitStatus(new OctopusScriptRunner());
        var variables = CommitStatusVariables(string.Empty);
        variables[SummaryVariable] = "Acceptance (tdd): " + new string('x', 200);

        var result = runner.Run(CommitStatusScript, variables);

        result.Failed.ShouldBeFalse(result.ToString());
        var posted = PostedDescription(runner);
        posted.Length.ShouldBe(140);
        posted.ShouldStartWith("TDD deployment and acceptance tests passed: xxx");
        posted.ShouldEndWith("x...");
    }

    private static string GenerateKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static string AcceptanceScript => OctopusScriptRunner.ScriptBody(Process, "acceptance-tests");

    private static string CommitStatusScript => OctopusScriptRunner.ScriptBody(Process, "report-commit-status");

    private static Dictionary<string, string> AcceptanceVariables(OctopusScriptRunner runner) => new(StringComparer.Ordinal)
    {
        ["Octopus.Environment.Name"] = "tdd",
        ["Acceptance.AllowDestructiveReset"] = "True",
        ["Octopus.Action.Package[ChurchBulletin.AcceptanceTests].ExtractedPath"] = Path.Combine(runner.Root, "package"),
        ["Db.Server"] = "db",
        ["Db.Name"] = "workorders",
        ["Db.AppLogin"] = "workorders_app",
        ["Octopus.Action[Read deployment secrets].Output.AcceptancePassword"] = "password-for-tests",
        ["App.BaseUrl"] = "https://workorders-tdd.example.test",
    };

    private static Dictionary<string, string> CommitStatusVariables(string error) => new(StringComparer.Ordinal)
    {
        ["GitHub.StatusEnabled"] = "True",
        ["GitHub.StatusAppPrivateKey"] = StatusAppKey,
        ["GitHub.StatusAppId"] = "1",
        ["GitHub.StatusAppInstallationId"] = "2",
        ["GitHub.AppRepository"] = "example-org/workorders",
        ["GitHub.StatusContext"] = "platform/tdd",
        ["Octopus.Release.Notes"] = $"app-commit: {Commit}\n",
        ["Octopus.Deployment.Error"] = error,
        ["Octopus.Web.ServerUri"] = "https://octopus.example.test",
        ["Octopus.Web.DeploymentLink"] = "/app#/deployments/1",
    };

    // The package holds the test assembly; timeout stands in for dotnet test and leaves the TRX file of the run.
    private static OctopusScriptRunner Acceptance(OctopusScriptRunner runner, int exitCode, string? trx)
    {
        var package = Path.Combine(runner.Root, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "ClearMeasure.Bootcamp.AcceptanceTests.dll"), string.Empty);
        var command = "mkdir -p \"$OCTOPUS_STUB_DIR/acceptance-results\"";
        if (trx is not null)
        {
            File.WriteAllText(Path.Combine(runner.Root, "run.trx"), trx, new UTF8Encoding(true));
            command += " && cp \"$OCTOPUS_STUB_DIR/run.trx\" \"$OCTOPUS_STUB_DIR/acceptance-results/run.trx\"";
        }

        return runner.Answer("timeout", string.Empty, new StubAnswer(ExitCode: exitCode, Command: command));
    }

    // The step signs its JWT in memory (the shared helper) and exchanges it with the stub GitHub API behind GITHUB_API_URL, so
    // the key must be a real RSA key; curl only posts the status.
    private static OctopusScriptRunner CommitStatus(OctopusScriptRunner runner)
    {
        var api = runner.Own(new StubGitHubApi());
        runner.Environment["GITHUB_API_URL"] = api.Url;
        runner.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        runner.Environment["AISF_BOARD_APP_INSTALLATION_ID"] = null;
        return runner.Answer("curl", "/statuses/", new StubAnswer());
    }

    // The JSON payload spans several lines of the stub's call record, so the description is read from the raw record.
    private static string PostedDescription(OctopusScriptRunner runner)
    {
        var record = File.ReadAllText(Path.Combine(runner.Root, "calls.tsv"));
        var match = System.Text.RegularExpressions.Regex.Match(record, "\"description\": \"(?<text>[^\"]*)\"");
        match.Success.ShouldBeTrue(record);
        return match.Groups["text"].Value;
    }

    // A TRX file as the NUnit adapter writes it: skipped tests and [LlmTest] warnings are NotExecuted and count in total
    // but not in executed, and the counters leave notExecuted at 0.
    private static string Trx(int passed, int failed, int ignored, int llmWarnings)
    {
        var results = new StringBuilder();
        void Add(string name, string outcome, string? message)
        {
            results.Append(System.Globalization.CultureInfo.InvariantCulture, $"    <UnitTestResult executionId=\"{Guid.NewGuid()}\" testName=\"{name}\" outcome=\"{outcome}\">");
            if (message is not null)
            {
                results.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output>");
            }

            results.Append("</UnitTestResult>\n");
        }

        for (var index = 1; index <= passed; index++)
        {
            Add($"Should_Pass_{index}", "Passed", null);
        }

        for (var index = 1; index <= failed; index++)
        {
            Add($"Should_Fail_{index}", "Failed", "Expected true but was false");
        }

        for (var index = 1; index <= ignored; index++)
        {
            Add($"Should_Skip_{index}", "NotExecuted", "Ignored for the test");
        }

        for (var index = 1; index <= llmWarnings; index++)
        {
            Add($"Should_AskTheModel_{index}", "NotExecuted", LlmWarning);
        }

        var total = passed + failed + ignored + llmWarnings;
        var outcome = failed > 0 ? "Failed" : "Completed";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="{Guid.NewGuid()}" name="acceptance" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
            {results}  </Results>
              <ResultSummary outcome="{outcome}">
                <Counters total="{total}" executed="{passed + failed}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>

            """;
    }
}
