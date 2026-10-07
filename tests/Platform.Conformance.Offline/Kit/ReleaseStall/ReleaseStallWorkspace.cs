using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Conformance.Offline.Kit.GitHubApp;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>
/// A temporary folder for one test of <c>scripts/release/release-stall-check.ps1</c>: the app folders the script reads
/// (<c>apps/&lt;app&gt;/envs/&lt;environment&gt;</c>, its <c>-AppsRoot</c>), pin logs, and a runner that starts the script in a
/// real <c>pwsh</c> with a closed (or supplied) standard input and a scrubbed environment.
/// </summary>
internal sealed class ReleaseStallWorkspace : IDisposable
{
    /// <summary>The script under test, repository-relative.</summary>
    public const string Script = "scripts/release/release-stall-check.ps1";

    private int logs;

    /// <summary>Creates the folder with the app of the recorded history and all three environments.</summary>
    public ReleaseStallWorkspace()
    {
        Root = Directory.CreateTempSubdirectory("release-stall-").FullName;
        Directory.CreateDirectory(AppsRoot);
        App("workorders", "tdd", "uat", "prod");
    }

    /// <summary>The temporary folder.</summary>
    public string Root { get; }

    /// <summary>The folder passed as <c>-AppsRoot</c>.</summary>
    public string AppsRoot => Path.Combine(Root, "apps");

    /// <summary>The recorded pin commits of this repository, 2026-09-29 to 2026-10-07.</summary>
    public static string RecordedLog => Path.Combine(KitToolbox.TestData, "release-stall", "pins-recorded.log");

    /// <summary>A pin line as <c>git log --format='%H %cI %s'</c> prints it; the commit is derived from the text.</summary>
    /// <param name="when">Committer date, ISO 8601.</param>
    /// <param name="app">App name.</param>
    /// <param name="version">Release version.</param>
    /// <param name="environment">Environment.</param>
    /// <param name="deployment">Number of the deployment ID.</param>
    public static string Pin(string when, string app, string version, string environment, int deployment = 1)
    {
        var subject = $"Pin {app} {version} in {environment} (Deployments-{deployment})";
        return $"{Sha(when + subject)} {when} {subject}";
    }

    /// <summary>The first seven characters of the commit of <see cref="Pin"/>.</summary>
    /// <param name="line">A pin line.</param>
    public static string Short(string line) => line[..7];

    /// <summary>Declares an app with its environments (replacing an earlier declaration).</summary>
    /// <param name="name">App name.</param>
    /// <param name="environments">Its environment folders.</param>
    public ReleaseStallWorkspace App(string name, params string[] environments)
    {
        var folder = Path.Combine(AppsRoot, name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        foreach (var environment in environments)
        {
            Directory.CreateDirectory(Path.Combine(folder, "envs", environment));
        }

        return this;
    }

    /// <summary>Writes pin lines to a file, newest first as git log does, and returns its path.</summary>
    /// <param name="lines">Lines in the order they were committed (oldest first).</param>
    public string Log(params string[] lines)
    {
        var path = Path.Combine(Root, $"pins-{++logs}.log");
        File.WriteAllLines(path, lines.Reverse());
        return path;
    }

    /// <summary>Runs the script with <c>-AppsRoot</c> of this workspace and <c>-Json</c>.</summary>
    /// <param name="log">The pin log.</param>
    /// <param name="now">The moment to judge.</param>
    /// <param name="arguments">More arguments.</param>
    public ProcessResult Check(string log, string now, params string[] arguments) =>
        Run(null, [], ["-PinLog", log, "-Now", now, "-AppsRoot", AppsRoot, "-Json", .. arguments]);

    /// <summary>Runs <c>pwsh -NoProfile -File &lt;script&gt; &lt;arguments&gt;</c> from the repository root.</summary>
    /// <param name="standardInput">Text for standard input (the script's pipeline); <c>null</c> closes it empty.</param>
    /// <param name="set">Environment variables to set on top of the scrubbed environment.</param>
    /// <param name="arguments">Script arguments.</param>
    public static ProcessResult Run(string? standardInput, (string Name, string Value)[] set, params string[] arguments)
    {
        var start = new ProcessStartInfo(GitHubScriptHost.Pwsh)
        {
            WorkingDirectory = KitToolbox.RepositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-NoProfile", "-File", GitHubScriptHost.Script(Script), .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in GitHubScriptHost.Environment(null, null, [("GITHUB_REPOSITORY", string.Empty), .. set]))
        {
            if (string.IsNullOrEmpty(value))
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start pwsh");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{Script} {string.Join(' ', arguments)} did not finish in time");
        }

        return new ProcessResult(process.ExitCode, output.Result, error.Result);
    }

    /// <summary>The JSON array the script wrote after its log lines.</summary>
    /// <param name="result">A run with <c>-Json</c>.</param>
    public static JsonArray Rows(ProcessResult result)
    {
        var lines = result.Output.ReplaceLineEndings("\n").Split('\n');
        var first = Array.FindIndex(lines, line => line.StartsWith('['));
        first.ShouldBeGreaterThanOrEqualTo(0, result.Transcript);
        return JsonNode.Parse(string.Join('\n', lines[first..]))!.AsArray();
    }

    /// <summary>Each row as <c>app version environment&gt;next State</c>.</summary>
    /// <param name="result">A run with <c>-Json</c>.</param>
    public static string[] States(ProcessResult result) =>
        Rows(result).Select(row => $"{Text(row, "App")} {Text(row, "Version")} {Text(row, "Environment")}>{Text(row, "NextEnvironment")} {Text(row, "State")}").ToArray();

    /// <summary>A text property of a row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="name">Property name.</param>
    public static string Text(JsonNode? row, string name) => row![name]!.GetValue<string>();

    /// <summary>A number property of a row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="name">Property name.</param>
    public static int Number(JsonNode? row, string name) => row![name]!.GetValue<int>();

    /// <summary>Deletes the folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..40];
}
