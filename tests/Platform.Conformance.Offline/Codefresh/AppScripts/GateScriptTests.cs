using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half: step <c>gate</c> (every copy of <c>scripts/gate.ps1</c>) sets the build result with the
/// semantics of GitHub Actions <c>build-result</c>: a docs-only change set passes without inspecting the gates; otherwise
/// every required gate must have written its <c>success</c> marker (or report success through <c>GATE_&lt;gate&gt;</c>), and
/// advisory gates never fail the build. The Markdown summary goes to standard output and the summary file.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class GateScriptTests
{
    private static readonly string[] Gates = ["build_sql", "build_sqlite", "code_analysis", "qodana", "security_scan", "acceptance"];

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("gate.ps1");

    /// <summary>Every required marker present: pass; the advisory gate may be missing.</summary>
    /// <param name="script">A copy of gate.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunGate_RequiredMarkersPresent_PassesWhateverTheAdvisoryGate(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        var gates = Markers(sandbox, "security_scan");
        var summary = Path.Combine(sandbox.Root, "reports", "gate-summary.md");

        var run = sandbox.Run(script, ["-Advisory", "security_scan", "-SummaryFile", summary, .. Gates]);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.OutputLines.ShouldContain("| qodana | success | yes |");
        run.OutputLines.ShouldContain("| security_scan | failure or skipped | advisory |");
        run.OutputLines[^1].ShouldBe("Result: **pass**. All required gates succeeded.");
        File.ReadAllText(summary).ShouldBe(run.Output, "the file holds what the log shows");
        Directory.Exists(gates).ShouldBeTrue();
    }

    /// <summary>A missing required marker, a failure reported in the environment, or an unresolved Codefresh variable with no marker, fail the build.</summary>
    /// <param name="script">A copy of gate.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunGate_RequiredGateWithoutSuccess_FailsTheBuild(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        Markers(sandbox, "qodana", "acceptance");
        sandbox.Environment["GATE_build_sql"] = "failure";
        sandbox.Environment["GATE_acceptance"] = "${{steps.acceptance.result}}";

        var run = sandbox.Run(script, ["-Advisory", "security_scan", .. Gates]);

        run.ExitCode.ShouldBe(1, run.Transcript);
        run.OutputLines.ShouldContain("| build_sql | failure | yes |");
        run.OutputLines.ShouldContain("| qodana | failure or skipped | yes |");
        run.OutputLines.ShouldContain("| acceptance | failure or skipped | yes |");
        run.OutputLines[^1].ShouldBe("Result: **fail**. A required gate did not succeed.");
        File.Exists(Path.Combine(sandbox.Volume, "reports", "gate-summary.md")).ShouldBeTrue("the default summary lives on the volume");
    }

    /// <summary>A docs-only change set passes with no marker at all.</summary>
    /// <param name="script">A copy of gate.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunGate_DocsOnlyChangeSet_PassesWithoutInspectingTheGates(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        Markers(sandbox, [.. Gates]);
        sandbox.Environment["CODE_CHANGED"] = "false";

        var run = sandbox.Run(script, ["-Advisory", "security_scan", .. Gates]);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.OutputLines[^1].ShouldBe("Docs-only change set: gates skipped, result **pass**.");
        run.Output.ShouldNotContain("| Gate |");
    }

    /// <summary>No gate, an unknown option or an invalid gate name is a usage error (exit 2).</summary>
    /// <param name="script">A copy of gate.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunGate_UsageErrors_ExitTwo(string script)
    {
        using var sandbox = AppScriptSandbox.Create();
        Markers(sandbox);

        var none = sandbox.Run(script, "-Advisory", "security_scan");
        var option = sandbox.Run(script, "-Bogus", "build_sql");
        var name = sandbox.Run(script, "build_sql", "bad-name");

        none.ExitCode.ShouldBe(2, none.Transcript);
        none.Error.ShouldContain("no gates given");
        option.ExitCode.ShouldBe(2, option.Transcript);
        option.Error.ShouldContain("unknown option: -Bogus");
        name.ExitCode.ShouldBe(2, name.Transcript);
        name.Error.ShouldContain("invalid gate name: bad-name");
    }

    /// <summary>A success marker for every gate but the given ones; CODE_CHANGED=true and VERSION set.</summary>
    private static string Markers(AppScriptSandbox sandbox, params string[] missing)
    {
        var gates = Path.Combine(sandbox.Root, "gates");
        Directory.CreateDirectory(gates);
        foreach (var gate in Gates.Except(missing))
        {
            File.WriteAllText(Path.Combine(gates, gate), "success\n");
        }

        sandbox.Environment["GATE_DIR"] = gates;
        sandbox.Environment["CODE_CHANGED"] = "true";
        sandbox.Environment["VERSION"] = "2.5.9";
        return gates;
    }
}
