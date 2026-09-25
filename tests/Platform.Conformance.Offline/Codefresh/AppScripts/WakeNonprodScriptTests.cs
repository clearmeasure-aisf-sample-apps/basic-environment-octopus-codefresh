using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-009, offline half: step <c>wake_nonprod</c> (every copy of <c>scripts/wake-nonprod.ps1</c>) asks Octopus to
/// run runbook env-wake of project platform-infrastructure in infra-nonprod and never fails the build. A stub
/// <c>curl</c> answers the project and environment lookups and the run request, and records the header file it was
/// given: the key travels in that private file only, never on a command line or in the log, and the file is gone
/// when the script ends.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class WakeNonprodScriptTests
{
    private const string Key = "fake-octopus-key";
    private const string Headers = "@<headers>";
    private const string Lookup = "curl -fsS --max-time 30 -H " + Headers;

    private const string Curl = """
        last=""; header=""; previous=""
        for a in "$@"; do
          if [ "$previous" = "-H" ]; then header="${a#@}"; fi
          previous="$a"; last="$a"
        done
        if [ -n "$header" ]; then
          { stat -c '%a' "$header" 2>/dev/null || stat -f '%Lp' "$header"; cat "$header"; } >>"${APP_SCRIPT_CALLS%/*}/headers.log"
        fi
        case "$last" in
          */projects/all) answer="$WAKE_PROJECTS" ;;
          */environments/all) answer="$WAKE_ENVIRONMENTS" ;;
          */run/v1) answer="$WAKE_RUN" ;;
          *) echo "curl: unexpected URL $last" >&2; exit 6 ;;
        esac
        if [ "$answer" = fail ]; then echo "curl: (22) The requested URL returned error: 401" >&2; exit 22; fi
        printf '%s' "$answer"
        """;

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("wake-nonprod.ps1");

    /// <summary>
    /// The project (by slug) and the environment found: one POST to the config-as-code run route with the environment in
    /// the body, the key only in a private header file, and the task IDs of the response in the log.
    /// </summary>
    /// <param name="script">A copy of wake-nonprod.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-009")]
    public void Should_RunWakeNonprod_ProjectAndEnvironmentFound_QueuesEnvWakeWithTheKeyInAPrivateHeaderFile(string script)
    {
        using var sandbox = Sandbox();

        var run = sandbox.Run(script);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.CallsOf("curl").Select(Line).ShouldBe(
        [
            $"{Lookup} https://octo.example.test/api/Spaces-1/projects/all",
            $"{Lookup} https://octo.example.test/api/Spaces-1/environments/all",
            $"{Lookup} -X POST --data-binary "
                + """{"SelectedPackages":[],"SelectedGitResources":[],"Runs":[{"EnvironmentId":"Environments-2","TenantId":null,"SkipActions":[],"SpecificMachineIds":[],"ExcludedMachineIds":[]}]}"""
                + " https://octo.example.test/api/spaces/Spaces-1/projects/Projects-7/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1",
        ], run.Transcript);
        run.CallsOf("curl").ShouldAllBe(call => call.Arguments[4].StartsWith("@" + sandbox.Temp, StringComparison.Ordinal));
        var header = $"600\nX-Octopus-ApiKey: {Key}\nContent-Type: application/json\n";
        File.ReadAllText(Path.Combine(sandbox.Root, "headers.log")).ShouldBe(header + header + header, "a private header file (mode 600) for every request");
        run.OutputLines.ShouldBe(["wake_nonprod: env-wake queued in infra-nonprod, tasks [\"ServerTasks-1\",\"ServerTasks-2\"]; the build does not wait for it."], run.Transcript);
        run.Calls.ShouldAllBe(call => call.Arguments.All(argument => !argument.Contains(Key, StringComparison.Ordinal)), "the key never goes on a command line");
        (run.Output + run.Error).ShouldNotContain(Key);
        Directory.EnumerateFiles(sandbox.Temp, "*", SearchOption.AllDirectories)
            .ShouldAllBe(file => !File.ReadAllText(file).Contains(Key, StringComparison.Ordinal), "the header file is removed");
    }

    /// <summary>A failing or empty lookup, a failing run request and an unreadable response are warnings: exit 0.</summary>
    /// <param name="script">A copy of wake-nonprod.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-009")]
    public void Should_RunWakeNonprod_LookupOrRunFails_WarnsAndExitsZero(string script)
    {
        using var sandbox = Sandbox();

        sandbox.Environment["WAKE_PROJECTS"] = "fail";
        var projectsFail = sandbox.Run(script);
        sandbox.Environment["WAKE_PROJECTS"] = """[{"Id":"Projects-3","Name":"Platform-Infrastructure","Slug":"PLATFORM-INFRASTRUCTURE"}]""";
        var noProject = sandbox.Run(script);
        sandbox.Environment["WAKE_PROJECTS"] = """{"a":{"Id":"Projects-4","Name":"platform-infrastructure","Slug":"pi"}}""";
        sandbox.Environment["WAKE_ENVIRONMENTS"] = "{not json";
        var environmentsBad = sandbox.Run(script);
        sandbox.Environment["WAKE_ENVIRONMENTS"] = """[{"Id":"Environments-2","Name":"infra-nonprod"}]""";
        sandbox.Environment["WAKE_RUN"] = "fail";
        var runFails = sandbox.Run(script);
        sandbox.Environment["WAKE_RUN"] = "<html>busy</html>";
        var unreadable = sandbox.Run(script);

        foreach (var (run, reason, calls) in new[]
        {
            (projectsFail, "the project lookup failed", 1),
            (noProject, "project platform-infrastructure not found", 1),
            (environmentsBad, "the environment lookup failed", 2),
            (runFails, "the env-wake run request failed", 3),
        })
        {
            run.ExitCode.ShouldBe(0, run.Transcript);
            run.Error.ShouldContain($"wake_nonprod: WARNING: {reason}. Not waking now; step 0 of the tdd deployment wakes the cluster.", Case.Sensitive, run.Transcript);
            run.CallsOf("curl").Count.ShouldBe(calls, run.Transcript);
            run.Output.ShouldNotContain("env-wake queued");
        }

        environmentsBad.CallsOf("curl")[1].Arguments[^1].ShouldBe("https://octo.example.test/api/Spaces-1/environments/all", "the project came from an object of projects");
        runFails.CallsOf("curl")[2].Arguments[^1].ShouldContain("/projects/Projects-4/");
        unreadable.ExitCode.ShouldBe(0, unreadable.Transcript);
        unreadable.OutputLines.ShouldBe(["wake_nonprod: env-wake queued in infra-nonprod, tasks (response not parsed); the build does not wait for it."], unreadable.Transcript);
    }

    /// <summary>A missing, placeholder or unresolved context variable is a warning before any request; a trailing slash of the URL is dropped.</summary>
    /// <param name="script">A copy of wake-nonprod.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-009")]
    public void Should_RunWakeNonprod_ContextMissingOrPlaceholder_WarnsWithoutCallingOctopus(string script)
    {
        using var sandbox = Sandbox();

        sandbox.Environment["OCTOPUS_URL"] = null;
        var noUrl = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_URL"] = "https://octo.example.test/";
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "<octopus-space-id>";
        var placeholder = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "Spaces-1";
        sandbox.Environment["OCTOPUS_API_KEY"] = "${{OCTOPUS_API_KEY}}";
        var unresolved = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_API_KEY"] = Key;
        var trailingSlash = sandbox.Run(script);

        foreach (var (run, variable) in new[] { (noUrl, "OCTOPUS_URL"), (placeholder, "OCTOPUS_SPACE_ID"), (unresolved, "OCTOPUS_API_KEY") })
        {
            run.ExitCode.ShouldBe(0, run.Transcript);
            run.Error.ShouldContain($"wake_nonprod: WARNING: {variable} is missing from context platform-octopus.", Case.Sensitive, run.Transcript);
            run.CallsOf("curl").ShouldBeEmpty();
        }

        trailingSlash.ExitCode.ShouldBe(0, trailingSlash.Transcript);
        trailingSlash.CallsOf("curl")[0].Arguments[^1].ShouldBe("https://octo.example.test/api/Spaces-1/projects/all");
    }

    private static AppScriptSandbox Sandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        sandbox.Stub("curl", Curl);
        sandbox.Environment["OCTOPUS_URL"] = "https://octo.example.test";
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "Spaces-1";
        sandbox.Environment["OCTOPUS_API_KEY"] = Key;
        sandbox.Environment["WAKE_PROJECTS"] = """[{"Id":"Projects-1","Name":"Other","Slug":"other"},{"Id":"Projects-7","Name":"Platform infrastructure","Slug":"platform-infrastructure"}]""";
        sandbox.Environment["WAKE_ENVIRONMENTS"] = """[{"Id":"Environments-1","Name":"infra-prod"},{"Id":"Environments-2","Name":"infra-nonprod"}]""";
        sandbox.Environment["WAKE_RUN"] = """{"Id":"RunbookRuns-1","TaskId":"ServerTasks-2","Queued":[{"TaskId":"ServerTasks-1"},{"TaskId":"ServerTasks-2"}]}""";
        return sandbox;
    }

    private static string Line(StubCall call) =>
        string.Join(' ', [call.Tool, .. call.Arguments.Select(argument => argument.StartsWith('@') ? Headers : argument)]);
}
