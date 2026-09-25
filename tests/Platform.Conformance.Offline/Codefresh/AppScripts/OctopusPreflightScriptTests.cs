using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>
/// CAP-CF-008, offline half: step <c>octopus_preflight</c> (every copy of <c>scripts/octopus-preflight.ps1</c>) fails
/// closed, with no network call, before the handoff reaches Octopus: context platform-octopus complete, no placeholder,
/// an https URL, and a working octopus CLI (stub). The key is never printed. The copies of the .NET starter (they have
/// changed-paths.ps1 next to them) also name the server and the space.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class OctopusPreflightScriptTests
{
    private const string Key = "fake-octopus-key";

    private static IEnumerable<string> Scripts() => AppScriptSandbox.Copies("octopus-preflight.ps1");

    /// <summary>A complete https context: octopus version runs, and the key appears nowhere.</summary>
    /// <param name="script">A copy of octopus-preflight.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunOctopusPreflight_CompleteHttpsContext_RunsTheCliWithoutPrintingTheKey(string script)
    {
        using var sandbox = Sandbox();

        var run = sandbox.Run(script);

        run.ExitCode.ShouldBe(0, run.Transcript);
        run.Calls.Select(call => call.ToString()).ShouldBe(["octopus version"]);
        string[] expected = NamesTheContext(script) ? ["2.20.1", "Octopus context present: https://octo.example.test, space Spaces-1"] : ["2.20.1"];
        run.OutputLines.ShouldBe(expected, run.Transcript);
        (run.Output + run.Error).ShouldNotContain(Key);
    }

    /// <summary>A missing, placeholder or unresolved variable, a URL other than https, or a failing CLI fails the step.</summary>
    /// <param name="script">A copy of octopus-preflight.ps1.</param>
    [TestCaseSource(nameof(Scripts))]
    [Capability("CAP-CF-008")]
    public void Should_RunOctopusPreflight_IncompleteContextOrBrokenCli_FailsClosed(string script)
    {
        using var sandbox = Sandbox();

        sandbox.Environment["OCTOPUS_API_KEY"] = null;
        var noKey = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_API_KEY"] = Key;
        sandbox.Environment["OCTOPUS_URL"] = "https://<octopus-host>";
        var placeholder = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_URL"] = "https://octo.example.test";
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "${{OCTOPUS_SPACE_ID}}";
        var unresolved = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "Spaces-1";
        sandbox.Environment["OCTOPUS_URL"] = "HTTPS://octo.example.test";
        var notHttps = sandbox.Run(script);
        sandbox.Environment["OCTOPUS_URL"] = "https://octo.example.test";
        sandbox.Stub("octopus", "echo 'octopus: broken' >&2; exit 3");
        var brokenCli = sandbox.Run(script);

        foreach (var (run, message) in new[]
        {
            (noKey, "octopus_preflight: OCTOPUS_API_KEY is missing from context platform-octopus"),
            (placeholder, "octopus_preflight: OCTOPUS_URL is missing from context platform-octopus"),
            (unresolved, "octopus_preflight: OCTOPUS_SPACE_ID is missing from context platform-octopus"),
            (notHttps, "octopus_preflight: OCTOPUS_URL must be https"),
        })
        {
            run.ExitCode.ShouldBe(1, run.Transcript);
            run.Error.ShouldContain(message, Case.Sensitive, run.Transcript);
            run.Calls.ShouldBeEmpty("no CLI call before the context is complete");
        }

        brokenCli.ExitCode.ShouldBe(1, brokenCli.Transcript);
        brokenCli.Error.ShouldContain("octopus: broken");
        brokenCli.Output.ShouldNotContain("Octopus context present");
    }

    private static AppScriptSandbox Sandbox()
    {
        var sandbox = AppScriptSandbox.Create();
        sandbox.Stub("octopus", "[ \"$1\" = version ] && echo 2.20.1");
        sandbox.Environment["OCTOPUS_URL"] = "https://octo.example.test";
        sandbox.Environment["OCTOPUS_SPACE_ID"] = "Spaces-1";
        sandbox.Environment["OCTOPUS_API_KEY"] = Key;
        return sandbox;
    }

    private static bool NamesTheContext(string script) =>
        File.Exists(Path.Combine(AppScriptSandbox.RepositoryRoot, Path.GetDirectoryName(script)!, "changed-paths.ps1"));
}
