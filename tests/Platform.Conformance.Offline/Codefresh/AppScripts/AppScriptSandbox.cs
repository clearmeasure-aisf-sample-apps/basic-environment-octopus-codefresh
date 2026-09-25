using System.Diagnostics;
using System.Text;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Codefresh.AppScripts;

/// <summary>One call of a stub tool: the tool name and its arguments, exactly as the script passed them.</summary>
/// <param name="Tool">Tool name, for example <c>az</c>.</param>
/// <param name="Arguments">The arguments.</param>
internal sealed record StubCall(string Tool, IReadOnlyList<string> Arguments)
{
    /// <summary>The call as one line, arguments separated by spaces.</summary>
    public override string ToString() => string.Join(' ', [Tool, .. Arguments]);
}

/// <summary>The outcome of one script run in an <see cref="AppScriptSandbox"/>.</summary>
/// <param name="ExitCode">Exit code of pwsh.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="Calls">The stub calls, in order.</param>
/// <param name="Exports">The lines of <c>env_vars_to_export</c> on the volume, in order.</param>
internal sealed record ScriptRun(int ExitCode, string Output, string Error, IReadOnlyList<StubCall> Calls, IReadOnlyList<string> Exports)
{
    /// <summary>The standard output lines, without the trailing empty line.</summary>
    public IReadOnlyList<string> OutputLines => Output.Split('\n', StringSplitOptions.None).Select(line => line.TrimEnd('\r')).Reverse().SkipWhile(string.IsNullOrEmpty).Reverse().ToArray();

    /// <summary>Everything the run printed and called, for assertion messages.</summary>
    public string Transcript =>
        $"exit {ExitCode}{Environment.NewLine}--- stdout{Environment.NewLine}{Output}--- stderr{Environment.NewLine}{Error}--- calls{Environment.NewLine}{string.Join(Environment.NewLine, Calls)}{Environment.NewLine}--- exports{Environment.NewLine}{string.Join(Environment.NewLine, Exports)}";

    /// <summary>The exported value of a variable, or <c>null</c>.</summary>
    /// <param name="name">Variable name.</param>
    public string? Export(string name) =>
        Exports.Where(line => line.StartsWith(name + "=", StringComparison.Ordinal)).Select(line => line[(name.Length + 1)..]).LastOrDefault();

    /// <summary>The calls of one tool.</summary>
    /// <param name="tool">Tool name.</param>
    public IReadOnlyList<StubCall> CallsOf(string tool) => Calls.Where(call => call.Tool == tool).ToArray();
}

/// <summary>
/// A throw-away sandbox for the Codefresh scripts of the apps and starters (<c>codefresh/{apps,templates}/*/scripts</c>):
/// a working folder, a Codefresh-like volume (<c>CF_VOLUME_PATH</c>) and stub tools first on PATH. Each stub is a POSIX
/// shell script that records its arguments and then runs a canned body, so the tests see exactly which tools a script
/// calls, with which arguments and in which order, and never reach a registry, Azure, Octopus or Sigstore. Scripts
/// start as the pipelines start them: <c>pwsh -NoProfile -File &lt;script&gt; &lt;arguments&gt;</c>. The stubs need a
/// POSIX shell, so the tests are ignored on Windows; pwsh and git missing make them Inconclusive locally and failed with
/// <c>CI=true</c>.
/// </summary>
internal sealed class AppScriptSandbox : IDisposable
{
    private const char Unit = '\u001f';
    private const char Record = '\u001e';

    /// <summary>Variables of the calling environment that would leak Codefresh or pipeline state into a script.</summary>
    private static readonly string[] ClearedPrefixes = ["CF_", "GATE_", "ACR_", "OCTOPUS_"];

    private static readonly string[] ClearedNames =
    [
        "RELEASE_BRANCH", "CODE_CHANGED", "VERSION", "BUILD_BUILDNUMBER", "IS_RELEASE", "ARTIFACTS_DIR", "IMAGES_REUSED",
        "DOCKER_CONFIG", "PIPELINE_YAML", "PREVIEW_BUILD", "CI_SQL_SA_PASSWORD",
    ];

    private readonly string pwsh;
    private readonly string git;
    private bool withoutCfExport;

    private AppScriptSandbox(string root, string pwsh, string git)
    {
        Root = root;
        this.pwsh = pwsh;
        this.git = git;
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Volume);
        Directory.CreateDirectory(Stubs);
        File.WriteAllText(CallLog, string.Empty);
    }

    /// <summary>The sandbox folder.</summary>
    public string Root { get; }

    /// <summary>The working directory of the scripts (the application checkout by default).</summary>
    public string Work => Path.Combine(Root, "work");

    /// <summary>The Codefresh volume, passed as <c>CF_VOLUME_PATH</c>.</summary>
    public string Volume => Path.Combine(Root, "volume");

    /// <summary>The folder of the stub tools, first on PATH.</summary>
    public string Stubs => Path.Combine(Root, "stubs");

    /// <summary>Environment variables of the next runs (a <c>null</c> value removes a variable).</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

    private string CallLog => Path.Combine(Root, "calls.log");

    /// <summary>The repository root that holds codefresh/.</summary>
    public static string RepositoryRoot => CodefreshRepository.Root;

    /// <summary>Creates a sandbox, or ends the test when the platform or a tool does not allow one.</summary>
    public static AppScriptSandbox Create()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The stub tools of the script tests are POSIX shell scripts; the tests run on Linux and macOS.");
        }

        var pwsh = KitToolbox.Require("pwsh");
        var git = KitToolbox.Require("git");
        var root = Path.Combine(Path.GetTempPath(), "platform-app-scripts", Guid.NewGuid().ToString("N"));
        return new AppScriptSandbox(root, pwsh, git);
    }

    /// <summary>
    /// The copies of a script in every app and starter, repository-relative (for example
    /// <c>codefresh/templates/minimal/scripts/version.ps1</c>), sorted.
    /// </summary>
    /// <param name="name">Script file name.</param>
    public static IEnumerable<string> Copies(string name) =>
        CodefreshRepository.Scripts
            .Where(script => CodefreshRepository.IsAppFile(script) && Path.GetFileName(script) == name)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Writes a stub tool: it records the call, then runs <paramref name="body"/> (POSIX shell; <c>"$@"</c> holds the
    /// arguments) and exits 0 unless the body exits.
    /// </summary>
    /// <param name="tool">Tool name.</param>
    /// <param name="body">Shell commands, for example <c>echo reuse</c>.</param>
    public void Stub(string tool, string body = "")
    {
        var path = Path.Combine(Stubs, tool);
        var script = new StringBuilder().Append("#!/bin/sh\n");
        if (tool == "dotnet")
        {
            // Where pwsh is a .NET global tool, its shim starts pwsh.dll with the dotnet on PATH: that one is not stubbed.
            script.Append("""
                case "${1:-}" in
                  *pwsh.dll)
                    IFS=':'
                    for folder in $PATH; do
                      if [ "$folder" != "$APP_SCRIPT_STUBS" ] && [ -x "$folder/dotnet" ]; then unset IFS; exec "$folder/dotnet" "$@"; fi
                    done
                    echo "no dotnet on PATH to start pwsh" >&2
                    exit 127 ;;
                esac

                """);
        }

        script
            .Append("{ printf '%s' \"$(basename \"$0\")\"; for a in \"$@\"; do printf '\\037%s' \"$a\"; done; printf '\\036\\n'; } >>\"$APP_SCRIPT_CALLS\"\n")
            .Append(body.ReplaceLineEndings("\n"))
            .Append("\nexit 0\n");
        File.WriteAllText(path, script.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }

    /// <summary>
    /// Installs a <c>cf_export</c> that behaves like Codefresh's: <c>NAME=value</c> or <c>NAME</c> (the value from the
    /// environment) lines appended to <c>$CF_VOLUME_PATH/env_vars_to_export</c>; <c>--mask</c> is recorded in the call.
    /// </summary>
    public void CfExport() => Stub("cf_export", """
        for a in "$@"; do
          case "$a" in
            --mask) ;;
            *=*) printf '%s\n' "$a" >>"$CF_VOLUME_PATH/env_vars_to_export" ;;
            *) printf '%s=%s\n' "$a" "$(printenv "$a")" >>"$CF_VOLUME_PATH/env_vars_to_export" ;;
          esac
        done
        """);

    /// <summary>Removes every PATH folder that holds a <c>cf_export</c> (Codefresh's own, inside a pipeline) from the next runs.</summary>
    public void WithoutCfExport()
    {
        File.Delete(Path.Combine(Stubs, "cf_export"));
        withoutCfExport = true;
    }

    /// <summary>Runs a script of this repository with pwsh in the working folder.</summary>
    /// <param name="script">Repository-relative script path.</param>
    /// <param name="arguments">Script arguments.</param>
    public ScriptRun Run(string script, params string[] arguments) =>
        RunIn(Work, script, arguments);

    /// <summary>Runs a script of this repository with pwsh in a given folder.</summary>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="script">Repository-relative script path, or an absolute path.</param>
    /// <param name="arguments">Script arguments.</param>
    public ScriptRun RunIn(string workingDirectory, string script, params string[] arguments)
    {
        File.WriteAllText(CallLog, string.Empty);
        var exportFile = Path.Combine(Volume, "env_vars_to_export");
        File.Delete(exportFile);
        var path = Path.IsPathRooted(script) ? script : Path.Combine(RepositoryRoot, script);
        var start = new ProcessStartInfo(pwsh) { WorkingDirectory = workingDirectory };
        foreach (var argument in (string[])["-NoProfile", "-File", path, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys.Where(key => ClearedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) || ClearedNames.Contains(key)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["PATH"] = SearchPath();
        start.Environment["APP_SCRIPT_CALLS"] = CallLog;
        start.Environment["APP_SCRIPT_STUBS"] = Stubs;
        start.Environment["CF_VOLUME_PATH"] = Volume;
        foreach (var (name, value) in Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var (exitCode, output, error) = Execute(start, TimeSpan.FromMinutes(3));
        var exports = File.Exists(exportFile) ? File.ReadAllLines(exportFile) : [];
        return new ScriptRun(exitCode, output, error, ReadCalls(), exports);
    }

    /// <summary>Runs the real git in a folder, with a fixed identity and date; fails the test when git fails.</summary>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="arguments">Git arguments.</param>
    /// <returns>The standard output, trimmed.</returns>
    public string Git(string workingDirectory, params string[] arguments)
    {
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(git) { WorkingDirectory = workingDirectory };
        foreach (var argument in (string[])["-c", "user.name=Conformance", "-c", "user.email=conformance@example.test", "-c", "init.defaultBranch=master", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_AUTHOR_DATE"] = "2026-09-01T10:00:00Z";
        start.Environment["GIT_COMMITTER_DATE"] = "2026-09-01T10:00:00Z";
        var (exitCode, output, error) = Execute(start, TimeSpan.FromMinutes(1));
        exitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} in {workingDirectory}: {error}");
        return output.Trim();
    }

    /// <summary>Writes a file under the sandbox (or at an absolute path) and returns its full path.</summary>
    /// <param name="path">Path relative to the sandbox, or absolute.</param>
    /// <param name="content">Content; line feeds are kept.</param>
    public string Write(string path, string content)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Commits every change of a repository with a message; returns the commit ID.</summary>
    /// <param name="repository">Repository folder.</param>
    /// <param name="message">Commit message.</param>
    public string Commit(string repository, string message)
    {
        Git(repository, "add", "-A");
        Git(repository, "commit", "-q", "--allow-empty", "-m", message);
        return Git(repository, "rev-parse", "HEAD");
    }

    /// <summary>Deletes the sandbox.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (int ExitCode, string Output, string Error) Execute(ProcessStartInfo start, TimeSpan timeout)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.UseShellExecute = false;
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.Append(e.Data).Append('\n'); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (error) { error.Append(e.Data).Append('\n'); } } };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{Path.GetFileName(start.FileName)} {string.Join(' ', start.ArgumentList)} did not finish in time");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString(), error.ToString());
    }

    private string SearchPath()
    {
        var folders = (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(folder => !withoutCfExport || !File.Exists(Path.Combine(folder, "cf_export")));
        return string.Join(Path.PathSeparator, [Stubs, .. folders]);
    }

    private IReadOnlyList<StubCall> ReadCalls() =>
        File.ReadAllText(CallLog)
            .Split(Record + "\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(Unit))
            .Select(parts => new StubCall(parts[0], parts[1..]))
            .ToArray();
}
