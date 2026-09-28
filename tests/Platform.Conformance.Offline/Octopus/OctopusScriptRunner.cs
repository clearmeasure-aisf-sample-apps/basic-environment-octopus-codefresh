using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>A tool call that a stub recorded.</summary>
/// <param name="Tool">Tool name, for example <c>kubectl</c>.</param>
/// <param name="Arguments">Its arguments.</param>
internal sealed record StubCall(string Tool, IReadOnlyList<string> Arguments)
{
    /// <summary>The arguments joined by spaces.</summary>
    public string Line => string.Join(' ', Arguments);
}

/// <summary>One answer of a stub tool.</summary>
/// <param name="Output">Standard output.</param>
/// <param name="ExitCode">Exit code.</param>
/// <param name="Command">A POSIX sh command whose output is the answer instead of <paramref name="Output"/>.</param>
/// <param name="RealTool">Absolute path of the real tool to run with the same arguments instead.</param>
internal sealed record StubAnswer(string Output = "", int ExitCode = 0, string? Command = null, string? RealTool = null);

/// <summary>What a script did under the stub Octopus runtime.</summary>
/// <param name="ExitCode">Exit code of the step.</param>
/// <param name="FailMessage">The Fail-Step message, or <c>null</c>.</param>
/// <param name="Outputs">Output variables (Set-OctopusVariable).</param>
/// <param name="Highlights">Write-Highlight messages.</param>
/// <param name="Warnings">Write-Warning messages.</param>
/// <param name="Sleeps">Start-Sleep durations in seconds.</param>
/// <param name="Calls">Tool calls, in order.</param>
/// <param name="Log">Standard output and standard error.</param>
/// <param name="Artifacts">Names of the artifacts attached with New-OctopusArtifact, in order.</param>
internal sealed record OctopusScriptResult(
    int ExitCode,
    string? FailMessage,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Sleeps,
    IReadOnlyList<StubCall> Calls,
    string Log,
    IReadOnlyList<string> Artifacts)
{
    /// <summary><c>true</c> when the step failed (Fail-Step or a non-zero exit code).</summary>
    public bool Failed => FailMessage is not null || ExitCode != 0;

    /// <summary>The calls of one tool.</summary>
    /// <param name="tool">Tool name.</param>
    public IReadOnlyList<StubCall> CallsOf(string tool) => Calls.Where(call => call.Tool == tool).ToArray();

    /// <summary>A summary for assertion messages.</summary>
    public override string ToString() =>
        $"exit {ExitCode}, fail {FailMessage ?? "none"}{Environment.NewLine}calls:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", Calls.Select(call => $"{call.Tool} {call.Line}"))}{Environment.NewLine}log:{Environment.NewLine}{Log}";
}

/// <summary>
/// Runs an Octopus PowerShell script the way Calamari does: a bootstrap defines <c>$OctopusParameters</c>,
/// <c>Set-OctopusVariable</c>, <c>Fail-Step</c>, <c>Write-Highlight</c>, <c>Write-Warning</c> and <c>New-OctopusArtifact</c>, dot-sources the script and
/// ends with the exit code of its last native command. Stub tools first on <c>PATH</c> (POSIX sh) record their arguments
/// and answer from rules; kubectl, curl and az never reach a real system, and git runs the real git unless a rule says
/// otherwise. <c>Start-Sleep</c> only records the wait. Needs pwsh and sh: on Windows, or without pwsh, the test is
/// Inconclusive (failed when <c>CI=true</c>).
/// </summary>
internal sealed partial class OctopusScriptRunner : IDisposable
{
    private const string StubScript = """
        #!/bin/sh
        # Stub tool of the offline Octopus script tests: records the call, then answers from the rules.
        tool=${0##*/}
        dir="$OCTOPUS_STUB_DIR"
        arguments=" $* "
        # A stage that reads standard input records its call once the stage before it has finished.
        case "$arguments" in
            *" --filename - "*|*" --config - "*) cat > /dev/null ;;
        esac
        {
            printf '%s' "$tool"
            for argument in "$@"; do printf '\t%s' "$argument"; done
            printf '\n'
        } >> "$dir/calls.tsv"
        index=0
        while [ -f "$dir/rules/$tool.$index.match" ]; do
            pattern=$(cat "$dir/rules/$tool.$index.match")
            case "$arguments" in
                *"$pattern"*)
                    count=$(cat "$dir/rules/$tool.$index.count" 2>/dev/null || echo 0)
                    echo $((count + 1)) > "$dir/rules/$tool.$index.count"
                    answer="$dir/rules/$tool.$index.$count"
                    [ -f "$answer.exit" ] || answer="$dir/rules/$tool.$index.last"
                    if [ -f "$answer.real" ]; then exec "$(cat "$answer.real")" "$@"; fi
                    if [ -f "$answer.command" ]; then sh "$answer.command"; else cat "$answer.out"; fi
                    exit "$(cat "$answer.exit")"
                    ;;
            esac
            index=$((index + 1))
        done
        if [ -f "$dir/rules/$tool.default" ]; then exec "$(cat "$dir/rules/$tool.default")" "$@"; fi
        exit 0

        """;

    private const string Bootstrap = """
        param([string]$OctopusStubScript, [string]$OctopusStubVariables, [string]$OctopusStubRecord)
        # Calamari runs scripts with Legacy native argument passing; a script that needs quotes kept sets Standard itself.
        $PSNativeCommandArgumentPassing = 'Legacy'
        $OctopusParameters = [System.Collections.Generic.Dictionary[string, string]]::new()
        $octopusStubValues = [System.IO.File]::ReadAllText($OctopusStubVariables) | ConvertFrom-Json -AsHashtable
        foreach ($octopusStubName in $octopusStubValues.Keys) { $OctopusParameters[$octopusStubName] = [string]$octopusStubValues[$octopusStubName] }
        function Write-OctopusStubRecord([string]$Kind, [string]$Name, [string]$Value) {
            Add-Content -LiteralPath $OctopusStubRecord -Value (@{ kind = $Kind; name = $Name; value = $Value } | ConvertTo-Json -Compress)
        }
        function Set-OctopusVariable([string]$name, [string]$value, [switch]$sensitive) { Write-OctopusStubRecord 'output' $name $value }
        function Fail-Step([string]$message) { Write-OctopusStubRecord 'fail' '' $message; Write-Host "Fail-Step: $message"; exit -1 }
        function Write-Highlight([string]$message) { Write-OctopusStubRecord 'highlight' '' $message; Write-Host $message }
        function Write-Warning([string]$Message) { Write-OctopusStubRecord 'warning' '' $Message; Write-Host "WARNING: $Message" }
        function Start-Sleep([double]$Seconds) { Write-OctopusStubRecord 'sleep' '' ([string]$Seconds) }
        function New-OctopusArtifact([string]$Path, [string]$Name) { Write-OctopusStubRecord 'artifact' $Name $Path }
        . $OctopusStubScript
        if (Test-Path variable:global:LASTEXITCODE) { exit $LASTEXITCODE }

        """;

    /// <summary>Tools that are always stubbed, so that a script never reaches a real system.</summary>
    private static readonly string[] StubbedTools = ["kubectl", "curl", "az", "git"];

    private readonly Dictionary<string, List<(string Match, StubAnswer[] Answers)>> rules = new(StringComparer.Ordinal);

    /// <summary>Creates a runner with its own temporary folder; the test is Inconclusive where pwsh or sh is missing.</summary>
    public OctopusScriptRunner()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("the stub tools are POSIX sh scripts; the Octopus script tests run on Linux and macOS (env-checks)");
        }

        Pwsh = KitToolbox.Require("pwsh");
        RealGit = KitToolbox.Find("git");
        Root = Path.Combine(Path.GetTempPath(), "platform-octopus-script-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "stubs"));
        Directory.CreateDirectory(Path.Combine(Root, "rules"));
        Directory.CreateDirectory(Path.Combine(Root, "tmp"));
    }

    /// <summary>The runner's temporary folder.</summary>
    public string Root { get; }

    /// <summary>The real git, or <c>null</c> when git is not on PATH.</summary>
    public string? RealGit { get; }

    private string Pwsh { get; }

    /// <summary>
    /// The PowerShell body of <c>Octopus.Action.Script.ScriptBody</c> of a step of an OCL file, without the heredoc
    /// indentation (the least indentation of its lines).
    /// </summary>
    /// <param name="oclPath">Repository-relative OCL path.</param>
    /// <param name="stepSlug">Step slug, for example <c>sod-guard</c>.</param>
    public static string ScriptBody(string oclPath, string stepSlug)
    {
        var step = OctopusRepository.Steps(OctopusRepository.Read(oclPath)).SingleOrDefault(candidate => candidate.Slug == stepSlug);
        step.ShouldNotBeNull($"{oclPath} has no step {stepSlug}");
        var match = Heredoc().Match(step.Text);
        match.Success.ShouldBeTrue($"step {stepSlug} of {oclPath} has no inline script body");
        var lines = match.Groups["body"].Value.Split('\n');
        var indent = lines.Where(line => line.Trim().Length > 0).Min(line => line.Length - line.TrimStart(' ').Length);
        return string.Join('\n', lines.Select(line => line.Trim().Length == 0 ? string.Empty : line[indent..])) + "\n";
    }

    /// <summary>Adds a rule: the calls of <paramref name="tool"/> whose arguments contain <paramref name="match"/> get the answers in turn, the last one repeating.</summary>
    /// <param name="tool">Tool name.</param>
    /// <param name="match">Text the space-joined arguments must contain; empty matches every call.</param>
    /// <param name="answers">Answers, in call order.</param>
    public OctopusScriptRunner Answer(string tool, string match, params StubAnswer[] answers)
    {
        answers.ShouldNotBeEmpty();
        if (!rules.TryGetValue(tool, out var list))
        {
            rules[tool] = list = [];
        }

        list.Add((match, answers));
        return this;
    }

    /// <summary>Runs a script body.</summary>
    /// <param name="script">PowerShell text.</param>
    /// <param name="variables">Octopus variables of the step.</param>
    public OctopusScriptResult Run(string script, IReadOnlyDictionary<string, string> variables)
    {
        var stubs = Path.Combine(Root, "stubs");
        foreach (var tool in StubbedTools.Concat(rules.Keys).Distinct(StringComparer.Ordinal))
        {
            var path = Path.Combine(stubs, tool);
            File.WriteAllText(path, StubScript.ReplaceLineEndings("\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        WriteRules();
        var scriptPath = Path.Combine(Root, "script.ps1");
        var variablesPath = Path.Combine(Root, "variables.json");
        var recordPath = Path.Combine(Root, "record.jsonl");
        var bootstrapPath = Path.Combine(Root, "bootstrap.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        File.WriteAllText(variablesPath, JsonSerializer.Serialize(variables), new UTF8Encoding(false));
        File.WriteAllText(bootstrapPath, Bootstrap, new UTF8Encoding(false));
        File.Delete(recordPath);
        File.Delete(Path.Combine(Root, "calls.tsv"));

        var result = RunPwsh([bootstrapPath, scriptPath, variablesPath, recordPath], stubs);
        var records = File.Exists(recordPath)
            ? File.ReadAllLines(recordPath).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<Dictionary<string, string>>(line)!).ToArray()
            : [];
        string[] Of(string kind) => records.Where(record => record["kind"] == kind).Select(record => record["value"]).ToArray();
        return new OctopusScriptResult(
            result.ExitCode,
            Of("fail").FirstOrDefault(),
            records.Where(record => record["kind"] == "output").GroupBy(record => record["name"]).ToDictionary(group => group.Key, group => group.Last()["value"]),
            Of("highlight"),
            Of("warning"),
            Of("sleep"),
            ReadCalls(),
            result.Transcript,
            records.Where(record => record["kind"] == "artifact").Select(record => record["name"]).ToArray());
    }

    /// <summary>Deletes the temporary folder.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder does not fail a test.
        }
    }

    private void WriteRules()
    {
        var folder = Path.Combine(Root, "rules");
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            File.Delete(file);
        }

        if (RealGit is not null)
        {
            File.WriteAllText(Path.Combine(folder, "git.default"), RealGit);
        }

        foreach (var (tool, list) in rules)
        {
            for (var index = 0; index < list.Count; index++)
            {
                var (match, answers) = list[index];
                var prefix = Path.Combine(folder, $"{tool}.{index}");
                File.WriteAllText($"{prefix}.match", match);
                for (var number = 0; number <= answers.Length; number++)
                {
                    var answer = answers[Math.Min(number, answers.Length - 1)];
                    var name = number < answers.Length ? $"{prefix}.{number}" : $"{prefix}.last";
                    File.WriteAllText($"{name}.exit", answer.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText($"{name}.out", answer.Output);
                    if (answer.Command is not null)
                    {
                        File.WriteAllText($"{name}.command", answer.Command);
                    }

                    if (answer.RealTool is not null)
                    {
                        File.WriteAllText($"{name}.real", answer.RealTool);
                    }
                }
            }
        }
    }

    private IReadOnlyList<StubCall> ReadCalls()
    {
        var path = Path.Combine(Root, "calls.tsv");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => line.Split('\t')).Select(parts => new StubCall(parts[0], parts[1..])).ToArray()
            : [];
    }

    private ProcessResult RunPwsh(IReadOnlyList<string> arguments, string stubs)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Pwsh)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = stubs + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["OCTOPUS_STUB_DIR"] = Root;
        start.Environment["TMPDIR"] = Path.Combine(Root, "tmp");
        start.Environment["HOME"] = Root;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["POWERSHELL_UPDATECHECK"] = "Off";
        using var process = new System.Diagnostics.Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"the script did not finish within two minutes:{Environment.NewLine}{output}");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString(), string.Empty);
    }

    [GeneratedRegex(@"Octopus\.Action\.Script\.ScriptBody = <<-EOT[ \t]*\n(?<body>.*?)\n[ \t]*EOT[ \t]*\n", RegexOptions.Singleline)]
    private static partial Regex Heredoc();
}
