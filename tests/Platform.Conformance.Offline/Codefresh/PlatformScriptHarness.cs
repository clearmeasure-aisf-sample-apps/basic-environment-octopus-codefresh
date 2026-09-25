using System.Diagnostics;
using System.Text;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>One call a stub tool received.</summary>
/// <param name="Tool">Stub name, for example <c>curl</c>.</param>
/// <param name="Arguments">The arguments as the process received them.</param>
/// <param name="Files">Files the call named with <c>@path</c> (curl headers, bodies and form values): path to content.</param>
/// <param name="PrivateFiles">Paths of those files that had mode 0600.</param>
internal sealed record StubCall(string Tool, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Files, IReadOnlySet<string> PrivateFiles)
{
    /// <summary>The first http or https argument.</summary>
    public string? Url => Arguments.FirstOrDefault(argument => argument.StartsWith("https://", StringComparison.Ordinal) || argument.StartsWith("http://", StringComparison.Ordinal));

    /// <summary>curl's method: the value of <c>-X</c>, else POST when the call sends data, else GET.</summary>
    public string Method => Value("-X") ?? (Arguments.Contains("--data-binary") || Arguments.Contains("--data-urlencode") ? "POST" : "GET");

    /// <summary>The <c>--data-binary</c> body, read from its file when it names one.</summary>
    public string? Body => Value("--data-binary") is { } body ? Resolve(body) : null;

    /// <summary>The header lines, read from the <c>-H @file</c> files.</summary>
    public IReadOnlyList<string> Headers => Values("-H").SelectMany(header => Resolve(header).Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToArray();

    /// <summary>The <c>-H</c> arguments that name a file, and whether each file had mode 0600.</summary>
    public IEnumerable<(string Path, bool Private)> HeaderFiles => Values("-H").Where(header => header.StartsWith('@')).Select(header => (header[1..], PrivateFiles.Contains(header[1..])));

    /// <summary>The value after the first occurrence of an option.</summary>
    /// <param name="option">For example <c>-X</c>.</param>
    public string? Value(string option) => Values(option).FirstOrDefault();

    /// <summary>The values after every occurrence of an option.</summary>
    /// <param name="option">For example <c>--data-urlencode</c>.</param>
    public IEnumerable<string> Values(string option)
    {
        for (var index = 0; index + 1 < Arguments.Count; index++)
        {
            if (Arguments[index] == option)
            {
                yield return Arguments[index + 1];
            }
        }
    }

    private string Resolve(string value) => value.StartsWith('@') && Files.TryGetValue(value[1..], out var content) ? content : value;
}

/// <summary>
/// Runs the scripts of codefresh/platform/scripts with pwsh against stub tools, for the offline tests of those scripts.
/// A temporary folder holds <c>bin/</c> with the stubs, put first on PATH, <c>volume/</c> as CF_VOLUME_PATH and a clean
/// HOME and TMPDIR; the script runs with only these variables and the test's own. Each stub records its call (and the
/// files a curl call names with <c>@path</c>, and whether they were private) and answers from the test's routes: the first
/// route of its tool whose match strings all occur in the call (its arguments and those files) prints its body, runs its
/// optional shell snippet with the call's arguments, and exits with its code. <c>cf_export NAME</c> also appends
/// <c>NAME=value</c> to <c>volume/env_vars_to_export</c>, as Codefresh documents. The stubs are POSIX sh, not pwsh,
/// because start-up time matters: one script makes dozens of calls. git can be the real git behind a recording stub.
/// </summary>
internal sealed class PlatformScriptHarness : IDisposable
{
    private const string Stub = """
        #!/bin/sh
        # Stub tool of the offline script tests (tests/Platform.Conformance.Offline/Codefresh/PlatformScriptHarness.cs).
        root='@ROOT@'
        tool=${0##*/}
        record=$(printf '%s\037' "$tool" "$@")
        files=''
        subject="$*"
        for argument in "$@"; do
          case $argument in
            @*) file=${argument#@} ;;
            *@/*) file=${argument#*@} ;;
            *) continue ;;
          esac
          [ -f "$file" ] || continue
          content=$(cat "$file"; printf x)
          content=${content%x}
          mode=no
          [ -n "$(find "$file" -perm 600 2>/dev/null)" ] && mode=yes
          files="$files$(printf '\035%s\037%s\037%s' "$file" "$mode" "$content")"
          subject="$subject $content"
        done
        printf '%s%s\036' "$record" "$files" >>"$root/calls.log"
        if [ "$tool" = cf_export ]; then
          for argument in "$@"; do
            case $argument in
              --mask) ;;
              *=*) printf '%s\n' "$argument" >>"$root/volume/env_vars_to_export" ;;
              *) printf '%s=%s\n' "$argument" "$(printenv "$argument")" >>"$root/volume/env_vars_to_export" ;;
            esac
          done
          exit 0
        fi
        for route in "$root/routes/$tool"/*; do
          [ -d "$route" ] || continue
          ok=yes
          if [ -f "$route/match" ]; then
            while IFS= read -r needle || [ -n "$needle" ]; do
              [ -n "$needle" ] || continue
              case $subject in *"$needle"*) ;; *) ok=no ;; esac
            done <"$route/match"
          fi
          [ $ok = yes ] || continue
          if [ -f "$route/times" ]; then
            used=0
            [ -f "$route/used" ] && read -r used <"$route/used"
            read -r times <"$route/times"
            [ "$used" -lt "$times" ] || continue
            printf '%s\n' $((used + 1)) >"$route/used"
          fi
          [ -f "$route/run" ] && sh "$route/run" "$@"
          [ -f "$route/stderr" ] && cat "$route/stderr" >&2
          [ -f "$route/body" ] && cat "$route/body"
          code=0
          [ -f "$route/exit" ] && read -r code <"$route/exit"
          exit "$code"
        done
        [ -n "$STUB_REAL_GIT" ] && [ "$tool" = git ] && exec "$STUB_REAL_GIT" "$@"
        printf '%s stub: no route for: %s\n' "$tool" "$*" >&2
        exit 97
        """;

    private static readonly object HostLock = new();
    private static string[]? pwshHost;
    private readonly Dictionary<string, string?> environment = new(StringComparer.Ordinal);
    private int routes;

    private PlatformScriptHarness(string root) => Root = root;

    /// <summary>The temporary folder.</summary>
    public string Root { get; }

    /// <summary>CF_VOLUME_PATH of the runs.</summary>
    public string Volume => Path.Combine(Root, "volume");

    /// <summary>The platform scripts folder of the repository.</summary>
    public static string ScriptsFolder => Path.Combine(KitToolbox.RepositoryRoot, "codefresh", "platform", "scripts");

    /// <summary>Creates the folder and a stub for each tool.</summary>
    /// <param name="tools">Stub names, for example <c>curl</c> and <c>cf_export</c>.</param>
    public static PlatformScriptHarness Create(params string[] tools)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("the stub tools are POSIX sh scripts; the script tests run on Linux and macOS");
        }

        KitToolbox.Require("sh");
        var harness = new PlatformScriptHarness(Directory.CreateTempSubdirectory("platform-scripts-").FullName);
        foreach (var folder in new[] { "bin", "volume", "home", "tmp", "routes" })
        {
            Directory.CreateDirectory(Path.Combine(harness.Root, folder));
        }

        File.WriteAllText(Path.Combine(harness.Root, "calls.log"), string.Empty);
        foreach (var tool in tools)
        {
            harness.AddStub(tool);
        }

        return harness;
    }

    /// <summary>Adds a stub.</summary>
    /// <param name="tool">Its name.</param>
    public void AddStub(string tool)
    {
        var path = Path.Combine(Root, "bin", tool);
        var text = Stub.Replace("@ROOT@", Root, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (tool == "cf_export")
        {
            // Codefresh's cf_export has no shebang line: a script that execs it directly fails, as in a build.
            text = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        }

        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A bare repository <c>&lt;name&gt;.git</c> in the folder with branch main (README.md and app.txt), the given branches
    /// at main, and optionally branch conformance-results with an earlier run's folder.
    /// </summary>
    /// <param name="name">Folder name without <c>.git</c>.</param>
    /// <param name="resultsBranch">Also create conformance-results.</param>
    /// <param name="branches">More branches at main.</param>
    public string SeedRepository(string name, bool resultsBranch, params string[] branches)
    {
        var bare = Path.Combine(Root, name + ".git");
        var seed = Path.Combine(Root, name + "-seed");
        Git(Root, "init", "--quiet", "--bare", "--initial-branch=main", bare);
        Git(Root, "init", "--quiet", "--initial-branch=main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "# sandbox\n");
        File.WriteAllText(Path.Combine(seed, "app.txt"), "app\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        Git(seed, "push", "--quiet", bare, "main");
        foreach (var branch in branches)
        {
            Git(seed, "push", "--quiet", bare, $"main:refs/heads/{branch}");
        }

        if (resultsBranch)
        {
            Git(seed, "checkout", "--quiet", "--orphan", "conformance-results");
            Git(seed, "rm", "-rf", "--quiet", ".");
            Directory.CreateDirectory(Path.Combine(seed, "results", "2026-09-01-rold"));
            File.WriteAllText(Path.Combine(seed, "README.md"), "# Conformance results\n");
            File.WriteAllText(Path.Combine(seed, "results", "2026-09-01-rold", "summary.md"), "old\n");
            Git(seed, "add", ".");
            Git(seed, "commit", "--quiet", "-m", "old results");
            Git(seed, "push", "--quiet", bare, "conformance-results");
        }

        return bare;
    }

    /// <summary>Runs the real git with a clean configuration and a fixed identity; fails the test when it fails.</summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="arguments">git arguments.</param>
    public string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(KitToolbox.Require("git"))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "home", ".gitconfig");
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "seed";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "seed@example.com";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Fail($"git {string.Join(' ', arguments)} failed: {error.Result}");
        }

        return output.Result;
    }

    /// <summary>Records every git call and then runs the real git (from PATH).</summary>
    public void RecordRealGit()
    {
        var git = KitToolbox.Require("git");
        AddStub("git");
        environment["STUB_REAL_GIT"] = git;
    }

    /// <summary>Adds a route; routes answer in the order they were added.</summary>
    /// <param name="tool">The stub.</param>
    /// <param name="match">Strings that must all occur in the call's arguments or in the files it names.</param>
    /// <param name="body">Standard output.</param>
    /// <param name="exitCode">Exit code.</param>
    /// <param name="times">How many calls the route answers; unlimited when <c>null</c>.</param>
    /// <param name="run">A shell snippet run with the call's arguments before the answer.</param>
    /// <param name="stderr">Standard error.</param>
    public void Route(string tool, string[] match, string body = "", int exitCode = 0, int? times = null, string? run = null, string? stderr = null)
    {
        var folder = Path.Combine(Root, "routes", tool, $"{++routes:D3}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "match"), string.Join('\n', match) + "\n");
        File.WriteAllText(Path.Combine(folder, "body"), body);
        File.WriteAllText(Path.Combine(folder, "exit"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        if (times is { } count)
        {
            File.WriteAllText(Path.Combine(folder, "times"), count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        if (run is not null)
        {
            File.WriteAllText(Path.Combine(folder, "run"), run.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        }

        if (stderr is not null)
        {
            File.WriteAllText(Path.Combine(folder, "stderr"), stderr);
        }
    }

    /// <summary>Sets a variable for the runs; <c>null</c> leaves it unset.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="value">Value.</param>
    public PlatformScriptHarness With(string name, string? value)
    {
        environment[name] = value;
        return this;
    }

    /// <summary>Runs <c>pwsh -NoProfile -NonInteractive -File &lt;script&gt; &lt;arguments&gt;</c> as a Codefresh step does.</summary>
    /// <param name="script">Script file name in codefresh/platform/scripts.</param>
    /// <param name="arguments">Script arguments.</param>
    public ProcessResult Run(string script, params string[] arguments) =>
        Start(["-NoProfile", "-NonInteractive", "-File", Path.Combine(ScriptsFolder, script), .. arguments]);

    /// <summary>Runs <c>pwsh -NoProfile -NonInteractive -Command &lt;command&gt;</c>, for the dot-sourced helpers.</summary>
    /// <param name="command">PowerShell command; <c>{scripts}</c> stands for the scripts folder.</param>
    public ProcessResult RunCommand(string command) =>
        Start(["-NoProfile", "-NonInteractive", "-Command", command.Replace("{scripts}", ScriptsFolder, StringComparison.Ordinal)]);

    /// <summary>The recorded calls, in order; of one tool when <paramref name="tool"/> is given.</summary>
    /// <param name="tool">Stub name.</param>
    public IReadOnlyList<StubCall> Calls(string? tool = null) =>
        File.ReadAllText(Path.Combine(Root, "calls.log"))
            .Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(Parse)
            .Where(call => tool is null || call.Tool == tool)
            .ToArray();

    /// <summary>The lines of <c>volume/env_vars_to_export</c>.</summary>
    public IReadOnlyList<string> Exports =>
        File.Exists(Path.Combine(Volume, "env_vars_to_export"))
            ? File.ReadAllLines(Path.Combine(Volume, "env_vars_to_export"))
            : [];

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
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The PowerShell that runs the scripts, by its real host: a pwsh on PATH may be a launcher that starts the .NET global
    /// tool as <c>dotnet pwsh.dll</c> with the dotnet found on PATH, which a dotnet stub would replace.
    /// </summary>
    private static string[] PwshHost()
    {
        lock (HostLock)
        {
            if (pwshHost is not null)
            {
                return pwshHost;
            }

            var pwsh = KitToolbox.Require("pwsh");
            var probe = KitToolbox.Run(pwsh, ["-NoProfile", "-NonInteractive", "-Command", "[Environment]::ProcessPath; $PSHOME"], Path.GetTempPath(), TimeSpan.FromMinutes(1));
            var lines = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (probe.ExitCode != 0 || lines.Length < 2)
            {
                Assert.Fail($"pwsh does not report its host:{Environment.NewLine}{probe.Transcript}");
            }

            pwshHost = Path.GetFileNameWithoutExtension(lines[0]) == "dotnet" ? [lines[0], Path.Combine(lines[1], "pwsh.dll")] : [lines[0]];
            return pwshHost;
        }
    }

    private static StubCall Parse(string record)
    {
        var groups = record.Split('\x1d');
        var fields = groups[0].Split('\x1f');
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var privateFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.Skip(1))
        {
            var parts = group.Split('\x1f', 3);
            files[parts[0]] = parts.Length > 2 ? parts[2] : string.Empty;
            if (parts.Length > 1 && parts[1] == "yes")
            {
                privateFiles.Add(parts[0]);
            }
        }

        // printf '%s\037' ends every field with a separator: the last element is empty.
        return new StubCall(fields[0], fields[1..^1], files, privateFiles);
    }

    private ProcessResult Start(IEnumerable<string> pwshArguments)
    {
        var host = PwshHost();
        var start = new ProcessStartInfo(host[0])
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in host.Skip(1).Concat(pwshArguments))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = Path.Combine(Root, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["HOME"] = Path.Combine(Root, "home");
        start.Environment["TMPDIR"] = Path.Combine(Root, "tmp");
        start.Environment["CF_VOLUME_PATH"] = Volume;
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "home", ".gitconfig");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var inherited in new[] { "DOTNET_ROOT", "PSModulePath" })
        {
            if (Environment.GetEnvironmentVariable(inherited) is { Length: > 0 } value)
            {
                start.Environment[inherited] = value;
            }
        }

        foreach (var (name, value) in environment)
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

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (error) { error.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{string.Join(' ', start.ArgumentList)} did not finish in 3 minutes{Environment.NewLine}{output}{error}");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }
}
