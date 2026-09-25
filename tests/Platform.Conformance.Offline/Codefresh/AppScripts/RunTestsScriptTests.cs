using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half, for the sandbox fixture: step <c>build_test</c> (<c>scripts/run-tests.ps1</c>) runs the unit
/// tests as TRX, summarises the TRX files also after a failing test, and fails the step when a test failed. A stub dotnet
/// writes the TRX file of a passing or a failing run.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class RunTestsScriptTests
{
    private const string Title = "sandbox/ci 0.1.4-ci.abc1234";

    private const string Dotnet = """
        [ "$1" = test ] || exit 0
        folder=""; previous=""
        for a in "$@"; do
          if [ "$previous" = "--results-directory" ]; then folder="$a"; fi
          previous="$a"
        done
        mkdir -p "$folder"
        if [ "${TEST_MODE:-pass}" = fail ]; then
          printf '<TestRun><Results><UnitTestResult testName="Sandbox.Tests.Fails" outcome="Failed" /></Results><ResultSummary><Counters total="2" passed="1" failed="1" /></ResultSummary></TestRun>\n' >"$folder/sandbox_run.trx"
          exit 1
        fi
        printf '<TestRun><ResultSummary><Counters total="2" passed="2" failed="0" /></ResultSummary></TestRun>\n' >"$folder/sandbox_run.trx"
        """;

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("run-tests.ps1");

    /// <summary>A passing run passes, a failing one fails; both leave the summary with the counters.</summary>
    /// <param name="script">A copy of run-tests.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunTests_PassingOrFailingRun_SummarisesTheTrxAndKeepsTheResult(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        sandbox.Stub("dotnet", Dotnet);
        sandbox.Environment["ARTIFACTS_DIR"] = "art";
        var summary = Path.Combine(sandbox.Work, "art", "test-summary.md");

        var passing = sandbox.Run(script, "-Title", Title);
        var passingSummary = File.ReadAllText(summary);
        sandbox.Environment["TEST_MODE"] = "fail";
        var failing = sandbox.Run(script, "-Title", Title);
        var failingSummary = File.ReadAllText(summary);

        passing.ExitCode.ShouldBe(0, passing.Transcript);
        passing.Calls.Select(call => call.ToString()).ShouldBe(
            ["dotnet test Sandbox.sln --configuration Release --no-build --logger trx;LogFilePrefix=sandbox --results-directory art/tests"]);
        passing.OutputLines.ShouldContain($"## {Title}", passing.Transcript);
        passingSummary.ShouldContain("| **All** | **2** | **2** | **0** | **0** |");
        failing.ExitCode.ShouldBe(1, failing.Transcript);
        failing.OutputLines.ShouldContain("- `Sandbox.Tests.Fails` (Failed)", failing.Transcript);
        failingSummary.ShouldContain("| **All** | **2** | **1** | **1** | **0** |");
    }
}
