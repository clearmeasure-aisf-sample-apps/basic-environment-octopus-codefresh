using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-004, offline half: the advisory gate <c>security_scan</c> (every copy of <c>scripts/security-scan.ps1</c>)
/// runs Gitleaks over the checkout and writes the NuGet vulnerability and deprecation reports; a leak, a vulnerable
/// package or a failing tool fails the step, a deprecated package only warns. Stub gitleaks and dotnet answer.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class SecurityScanScriptTests
{
    private const string Gitleaks = """
        report=""; previous=""
        for a in "$@"; do
          if [ "$previous" = "--report-path" ]; then report="$a"; fi
          previous="$a"
        done
        case "${GITLEAKS_MODE:-clean}" in
          clean) printf '{"runs":[]}\n' >"$report"; echo "INF no leaks found" >&2 ;;
          leak) printf '{"runs":[{"results":[1]}]}\n' >"$report"; echo "WRN leaks found: 1" >&2; exit 1 ;;
        esac
        """;

    private const string Dotnet = """
        case "$1" in
          restore)
            if [ -n "${RESTORE_FAIL:-}" ]; then echo "error NU1101: Unable to find package Missing.Package"; exit 1; fi
            echo "  All projects are up-to-date for restore." ;;
          list)
            case "$*" in
              *--vulnerable*) mode="${VULN_MODE:-none}"; kind=vulnerable ;;
              *) mode="${DEPR_MODE:-none}"; kind=deprecated ;;
            esac
            case "$mode" in
              none) echo "The given project \`UI.Server\` has no $kind packages given the current sources." ;;
              found) echo "Project \`UI.Server\` has the following $kind packages"; echo "   > Some.Package      1.0.0" ;;
              error) echo "error: A project or solution file could not be found." >&2; exit 1 ;;
            esac ;;
        esac
        """;

    private static readonly string[] AllCalls =
    [
        "gitleaks dir . --config .gitleaks.toml --redact --no-banner --report-format sarif --report-path art/security/gitleaks.sarif",
        "dotnet restore src/ChurchBulletin.sln",
        "dotnet list src/ChurchBulletin.sln package --vulnerable --include-transitive",
        "dotnet list src/ChurchBulletin.sln package --deprecated",
    ];

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("security-scan.ps1");

    /// <summary>A clean checkout passes and leaves the three reports; deprecated packages add a warning and still pass.</summary>
    /// <param name="script">A copy of security-scan.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunSecurityScan_CleanOrOnlyDeprecated_WritesTheReportsAndPasses(string script)
    {
        using var sandbox = Sandbox();

        var clean = sandbox.Run(script);
        sandbox.Environment["DEPR_MODE"] = "found";
        var deprecated = sandbox.Run(script);

        clean.ExitCode.ShouldBe(0, clean.Transcript);
        clean.Calls.Select(call => call.ToString()).ShouldBe(AllCalls);
        var reports = Path.Combine(sandbox.Work, "art", "security");
        File.ReadAllText(Path.Combine(reports, "gitleaks.sarif")).ShouldBe("{\"runs\":[]}\n");
        File.ReadAllText(Path.Combine(reports, "vulnerability-report.txt"))
            .ShouldBe("The given project `UI.Server` has no vulnerable packages given the current sources.\n");
        clean.OutputLines.ShouldContain("The given project `UI.Server` has no deprecated packages given the current sources.", clean.Transcript);
        clean.Output.ShouldNotContain("WARNING");
        deprecated.ExitCode.ShouldBe(0, deprecated.Transcript);
        deprecated.OutputLines.ShouldContain("WARNING: deprecated NuGet packages", deprecated.Transcript);
        File.ReadAllText(Path.Combine(reports, "deprecated-report.txt")).ShouldStartWith("Project `UI.Server` has the following deprecated packages\n");
    }

    /// <summary>A leak, a vulnerable package or a failing listing fails after every report; a failing restore stops at once.</summary>
    /// <param name="script">A copy of security-scan.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-004")]
    public void Should_RunSecurityScan_LeakVulnerabilityOrFailingTool_Fails(string script)
    {
        using var sandbox = Sandbox();

        sandbox.Environment["GITLEAKS_MODE"] = "leak";
        var leak = sandbox.Run(script);
        sandbox.Environment["GITLEAKS_MODE"] = "clean";
        sandbox.Environment["VULN_MODE"] = "found";
        var vulnerable = sandbox.Run(script);
        sandbox.Environment["VULN_MODE"] = "error";
        var listFails = sandbox.Run(script);
        sandbox.Environment["VULN_MODE"] = "none";
        sandbox.Environment["RESTORE_FAIL"] = "1";
        var restoreFails = sandbox.Run(script);

        foreach (var run in new[] { leak, vulnerable, listFails })
        {
            run.ExitCode.ShouldBe(1, run.Transcript);
            run.Calls.Select(call => call.ToString()).ShouldBe(AllCalls, run.Transcript);
        }

        listFails.OutputLines.ShouldContain("error: A project or solution file could not be found.", "the listing's error goes to the report and the log");
        File.ReadAllText(Path.Combine(sandbox.Work, "art", "security", "vulnerability-report.txt")).ShouldContain("could not be found");
        restoreFails.ExitCode.ShouldBe(1, restoreFails.Transcript);
        restoreFails.Calls.Select(call => call.ToString()).ShouldBe(AllCalls[..2], restoreFails.Transcript);
    }

    private static AppScriptSandbox Sandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        sandbox.Stub("gitleaks", Gitleaks);
        sandbox.Stub("dotnet", Dotnet);
        sandbox.Environment["ARTIFACTS_DIR"] = "art";
        return sandbox;
    }
}
