using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>A call of a stub tool.</summary>
/// <param name="Tool">Tool name, for example <c>az</c>; <c>sleep</c> for <c>Start-Sleep</c>.</param>
/// <param name="Arguments">Arguments as the process received them.</param>
/// <param name="Input">Standard input.</param>
/// <param name="File">Content of the file named by a <c>--file</c> argument, or <c>null</c>.</param>
internal sealed record ToolCall(string Tool, IReadOnlyList<string> Arguments, string Input, string? File)
{
    /// <summary>The tool and its arguments joined by spaces, the text a reply pattern matches.</summary>
    public string Line => Arguments.Count == 0 ? Tool : $"{Tool} {string.Join(' ', Arguments)}";

    /// <summary>The value after an option such as <c>--name</c>, or <c>null</c>.</summary>
    /// <param name="name">Option name.</param>
    public string? Option(string name)
    {
        for (var index = 0; index + 1 < Arguments.Count; index++)
        {
            if (Arguments[index] == name)
            {
                return Arguments[index + 1];
            }
        }

        return null;
    }

    /// <summary><c>true</c> when <see cref="Line"/> matches the regular expression.</summary>
    /// <param name="pattern">.NET regular expression.</param>
    public bool Matches(string pattern) => Regex.IsMatch(Line, pattern);

    /// <inheritdoc />
    public override string ToString() => Line;
}

/// <summary>What one run of an inline script left: exit code, tool calls and the Octopus events of its log.</summary>
/// <param name="ExitCode">Exit code of the PowerShell process.</param>
/// <param name="Calls">Tool calls in order.</param>
/// <param name="Events">
/// Log lines as <c>LOG text</c>, <c>HIGHLIGHT text</c>, <c>WARNING text</c>, <c>OUTPUT name=value</c>, <c>FAIL message</c>
/// and <c>ARTIFACT name</c>.
/// </param>
/// <param name="Error">Standard error.</param>
internal sealed record ScriptRun(int ExitCode, IReadOnlyList<ToolCall> Calls, IReadOnlyList<string> Events, string Error)
{
    /// <summary>Output variables set with <c>Set-OctopusVariable</c>.</summary>
    public IReadOnlyDictionary<string, string> Outputs =>
        Events.Where(e => e.StartsWith("OUTPUT ", StringComparison.Ordinal))
            .Select(e => e["OUTPUT ".Length..].Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    /// <summary>The message of <c>Fail-Step</c>, or <c>null</c>.</summary>
    public string? Failure => Events.LastOrDefault(e => e.StartsWith("FAIL ", StringComparison.Ordinal))?["FAIL ".Length..];

    /// <summary>Warnings.</summary>
    public IReadOnlyList<string> Warnings => Lines("WARNING ");

    /// <summary>Highlights.</summary>
    public IReadOnlyList<string> Highlights => Lines("HIGHLIGHT ");

    /// <summary>Plain log lines.</summary>
    public IReadOnlyList<string> Log => Lines("LOG ");

    /// <summary><c>true</c> when the step succeeded: exit code 0 and no <c>Fail-Step</c>.</summary>
    public bool Succeeded => ExitCode == 0 && Failure is null;

    /// <summary>The calls whose line matches a .NET regular expression.</summary>
    /// <param name="pattern">Pattern over <see cref="ToolCall.Line"/>.</param>
    public IReadOnlyList<ToolCall> CallsMatching(string pattern) => Calls.Where(call => call.Matches(pattern)).ToArray();

    /// <summary>Index of the first call matching a pattern, or -1.</summary>
    /// <param name="pattern">Pattern over <see cref="ToolCall.Line"/>.</param>
    public int IndexOf(string pattern)
    {
        for (var index = 0; index < Calls.Count; index++)
        {
            if (Calls[index].Matches(pattern))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Events, calls and standard error, for assertion messages.</summary>
    public string Transcript =>
        $"exit {ExitCode}\nevents:\n  {string.Join("\n  ", Events)}\ncalls:\n  {string.Join("\n  ", Calls.Select(call => call.Line))}\nstderr:\n{Error}";

    private string[] Lines(string prefix) => Events.Where(e => e.StartsWith(prefix, StringComparison.Ordinal)).Select(e => e[prefix.Length..]).ToArray();
}

/// <summary>
/// Runs the inline PowerShell script of an OCL step under a stub Octopus runtime, the way Calamari runs it: a bootstrap
/// defines <c>$OctopusParameters</c>, <c>Set-OctopusVariable</c>, <c>Fail-Step</c>, <c>Write-Highlight</c>,
/// <c>Write-Warning</c> and <c>New-OctopusArtifact</c>, dot-sources the script and exits with its exit code. The stub tools
/// <c>az</c>, <c>kubectl</c>, <c>curl</c> and <c>kubelogin</c> come first on PATH: POSIX shell scripts that record every
/// call (arguments, standard input, the file of a <c>--file</c> argument) and reply from the first matching rule (a POSIX
/// extended regular expression over <c>tool arg arg ...</c>); a call without a rule fails with exit code 97.
/// <c>Start-Sleep</c> records a <c>sleep</c> call instead of sleeping. Needs pwsh and sh: Inconclusive without them
/// locally, failed with <c>CI=true</c>.
/// </summary>
internal sealed partial class RunbookScript
{
    private static readonly string[] StubTools = ["az", "kubectl", "curl", "kubelogin"];
    private readonly List<(string Pattern, string Output, int ExitCode, string? Error, int? Times)> replies = [];

    private RunbookScript(string source, string body)
    {
        Source = source;
        Body = body;
    }

    /// <summary>Where the script comes from: <c>file#step</c>.</summary>
    public string Source { get; }

    /// <summary>The script as Octopus runs it: the heredoc body with its indentation removed.</summary>
    public string Body { get; }

    /// <summary>The variables of the run (<c>$OctopusParameters</c>), case-insensitive as in Calamari.</summary>
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The inline script of a step, which must be a PowerShell step.</summary>
    /// <param name="relativePath">OCL file, for example <c>.octopus/platform-infrastructure/runbooks/env-sleep.ocl</c>.</param>
    /// <param name="step">Step slug.</param>
    public static RunbookScript Of(string relativePath, string step)
    {
        var steps = OctopusRepository.Steps(OctopusRepository.Read(relativePath));
        var text = steps.SingleOrDefault(candidate => candidate.Slug == step)?.Text;
        text.ShouldNotBeNull($"{relativePath} has no step {step}");
        Regex.IsMatch(text!, @"^\s*Octopus\.Action\.Script\.Syntax = ""PowerShell""", RegexOptions.Multiline).ShouldBeTrue($"{relativePath} step {step} is not a PowerShell step");
        return new RunbookScript($"{relativePath}#{step}", InlineBody(text!) ?? throw new InvalidOperationException($"{relativePath} step {step} has no inline script"));
    }

    /// <summary>The ScriptBody heredoc of a step, with the smallest indentation of its lines removed, or <c>null</c>.</summary>
    /// <param name="stepText">Text of the step.</param>
    public static string? InlineBody(string stepText)
    {
        var lines = stepText.Split('\n');
        var start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^\s*Octopus\.Action\.Script\.ScriptBody = <<-EOT\s*$"));
        if (start < 0)
        {
            return null;
        }

        var end = Array.FindIndex(lines, start + 1, line => line.Trim() == "EOT");
        end.ShouldBeGreaterThan(start, "the ScriptBody heredoc has no EOT line");
        var body = lines[(start + 1)..end];
        var indent = body.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', body.Select(line => line.Trim().Length == 0 ? string.Empty : line[indent..])) + "\n";
    }

    /// <summary>Sets a variable.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="value">Value.</param>
    public RunbookScript With(string name, string value)
    {
        Parameters[name] = value;
        return this;
    }

    /// <summary>Adds a reply rule; rules are tried in the order they were added.</summary>
    /// <param name="pattern">POSIX extended regular expression over <c>tool arg arg ...</c>.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="exitCode">Exit code.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="times">How many calls the rule answers; unlimited when <c>null</c>.</param>
    public RunbookScript Reply(string pattern, string output = "", int exitCode = 0, string? error = null, int? times = null)
    {
        replies.Add((pattern, output, exitCode, error, times));
        return this;
    }

    /// <summary>Runs the script and returns what it did.</summary>
    /// <param name="timeout">Longest run (default one minute).</param>
    public ScriptRun Run(TimeSpan? timeout = null)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("the stub tools of the runbook script tests are POSIX shell scripts");
        }

        var pwsh = KitToolbox.Require("pwsh");
        KitToolbox.Require("sh");
        var root = Path.Combine(Path.GetTempPath(), "platform-runbook-tests", Guid.NewGuid().ToString("N"));
        try
        {
            return RunIn(root, pwsh, timeout ?? TimeSpan.FromMinutes(1));
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("PLATFORM_KEEP_SCRIPT_RUNS") is not ("true" or "1"))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static ToolCall ReadCall(string folder)
    {
        var arguments = Directory.EnumerateFiles(folder, "arg.*").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToArray();
        var input = Path.Combine(folder, "stdin");
        var file = Path.Combine(folder, "file");
        return new ToolCall(
            File.ReadAllText(Path.Combine(folder, "tool")),
            arguments,
            File.Exists(input) ? File.ReadAllText(input) : string.Empty,
            File.Exists(file) ? File.ReadAllText(file) : null);
    }

    private static List<string> ParseEvents(string output)
    {
        var events = new List<string>();
        var mode = "LOG";
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            switch (line)
            {
                case "##octopus[stdout-highlight]":
                    mode = "HIGHLIGHT";
                    continue;
                case "##octopus[stdout-warning]":
                    mode = "WARNING";
                    continue;
                case "##octopus[stdout-default]":
                    mode = "LOG";
                    continue;
            }

            if (ServiceMessage().Match(line) is { Success: true } message)
            {
                var values = ServiceValue().Matches(message.Groups["values"].Value)
                    .ToDictionary(value => value.Groups["key"].Value, value => Encoding.UTF8.GetString(Convert.FromBase64String(value.Groups["value"].Value)), StringComparer.Ordinal);
                events.Add(message.Groups["name"].Value switch
                {
                    "setVariable" => $"OUTPUT {values["name"]}={values["value"]}",
                    "resultMessage" => $"FAIL {values["message"]}",
                    "createArtifact" => $"ARTIFACT {values["name"]}",
                    var other => $"LOG {other}",
                });
                continue;
            }

            if (line.Length > 0 || mode != "LOG")
            {
                events.Add($"{mode} {line}");
            }
        }

        return events;
    }

    private ScriptRun RunIn(string root, string pwsh, TimeSpan timeout)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var stub = Directory.CreateDirectory(Path.Combine(root, "stub")).FullName;
        Directory.CreateDirectory(Path.Combine(stub, "calls"));
        var rules = Directory.CreateDirectory(Path.Combine(stub, "rules")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var temp = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        foreach (var tool in StubTools)
        {
            var path = Path.Combine(bin, tool);
            File.WriteAllText(path, StubScript.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        for (var index = 0; index < replies.Count; index++)
        {
            var reply = replies[index];
            var folder = Directory.CreateDirectory(Path.Combine(rules, index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))).FullName;
            File.WriteAllText(Path.Combine(folder, "pattern"), reply.Pattern);
            File.WriteAllText(Path.Combine(folder, "stdout"), reply.Output);
            File.WriteAllText(Path.Combine(folder, "exit"), reply.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (reply.Error is not null)
            {
                File.WriteAllText(Path.Combine(folder, "stderr"), reply.Error);
            }

            if (reply.Times is { } count)
            {
                File.WriteAllText(Path.Combine(folder, "times"), count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var variables = Path.Combine(root, "variables.json");
        File.WriteAllText(variables, JsonSerializer.Serialize(Parameters.ToDictionary(pair => pair.Key, pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)))));
        var script = Path.Combine(root, "script.ps1");
        File.WriteAllText(script, Body);
        var bootstrap = Path.Combine(root, "bootstrap.ps1");
        File.WriteAllText(bootstrap, Bootstrap);

        var start = new ProcessStartInfo(pwsh)
        {
            WorkingDirectory = work,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", bootstrap })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["STUB_DIR"] = stub;
        start.Environment["OCTO_VARIABLES"] = variables;
        start.Environment["OCTO_SCRIPT"] = script;
        start.Environment["HOME"] = root;
        start.Environment["TMPDIR"] = temp;
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["POWERSHELL_UPDATECHECK"] = "Off";
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
            Assert.Fail($"{Source} did not finish within {timeout.TotalSeconds:0} s:\n{output}\n{error}");
        }

        process.WaitForExit();
        var calls = Directory.EnumerateDirectories(Path.Combine(stub, "calls")).Order(StringComparer.Ordinal).Select(ReadCall).ToArray();
        return new ScriptRun(process.ExitCode, calls, ParseEvents(output.ToString()), error.ToString());
    }

    [GeneratedRegex(@"^##octopus\[(?<name>[A-Za-z]+)(?<values>( [a-zA-Z]+='[^']*')*)\]$")]
    private static partial Regex ServiceMessage();

    [GeneratedRegex(@"(?<key>[a-zA-Z]+)='(?<value>[^']*)'")]
    private static partial Regex ServiceValue();

    private const string StubScript = """
        #!/bin/sh
        # Stub of a command-line tool for the runbook script tests: records the call, then replies from the first rule
        # whose pattern matches "tool arg arg ...".
        tool="$(basename "$0")"
        dir="$STUB_DIR"
        n=$(( $(cat "$dir/seq" 2>/dev/null || echo 0) + 1 ))
        echo "$n" > "$dir/seq"
        call="$dir/calls/$(printf '%05d' "$n")"
        mkdir -p "$call"
        printf '%s' "$tool" > "$call/tool"
        i=0
        previous=""
        for argument in "$@"; do
          i=$((i + 1))
          printf '%s' "$argument" > "$call/arg.$(printf '%03d' "$i")"
          if [ "$previous" = "--file" ] && [ -f "$argument" ]; then cp "$argument" "$call/file"; fi
          previous="$argument"
        done
        cat > "$call/stdin"
        line="$(printf '%s' "$tool $*" | tr '\n' ' ')"
        for rule in "$dir/rules"/*; do
          [ -d "$rule" ] || continue
          if [ -f "$rule/times" ]; then
            left="$(cat "$rule/times")"
            [ "$left" -gt 0 ] || continue
          fi
          printf '%s\n' "$line" | grep -Eq -- "$(cat "$rule/pattern")" || continue
          if [ -f "$rule/times" ]; then echo $((left - 1)) > "$rule/times"; fi
          cat "$rule/stdout"
          if [ -f "$rule/stderr" ]; then cat "$rule/stderr" >&2; fi
          exit "$(cat "$rule/exit")"
        done
        echo "stub $tool: no reply for: $line" >&2
        exit 97

        """;

    private const string Bootstrap = """
        $PSStyle.OutputRendering = 'PlainText'
        # Calamari runs scripts with Legacy native argument passing; a script that needs quotes kept sets Standard itself.
        $PSNativeCommandArgumentPassing = 'Legacy'
        $OctopusParameters = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in (Get-Content -Raw -LiteralPath $env:OCTO_VARIABLES | ConvertFrom-Json -AsHashtable).GetEnumerator()) {
            $OctopusParameters[$entry.Key] = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($entry.Value))
        }
        function ConvertTo-ServiceMessageValue([string] $value) { [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($value)) }
        function Set-OctopusVariable([string] $name, [string] $value, [switch] $sensitive) { Write-Host "##octopus[setVariable name='$(ConvertTo-ServiceMessageValue $name)' value='$(ConvertTo-ServiceMessageValue $value)']" }
        function Fail-Step([string] $message) { if ($message) { Write-Host "##octopus[resultMessage message='$(ConvertTo-ServiceMessageValue $message)']" }; exit -1 }
        function Write-Highlight([string] $message) { Write-Host '##octopus[stdout-highlight]'; Write-Host $message; Write-Host '##octopus[stdout-default]' }
        function Write-Warning([Parameter(Position = 0)] [string] $Message) { Write-Host '##octopus[stdout-warning]'; Write-Host $Message; Write-Host '##octopus[stdout-default]' }
        function New-OctopusArtifact([string] $path, [string] $name) { Write-Host "##octopus[createArtifact path='$(ConvertTo-ServiceMessageValue $path)' name='$(ConvertTo-ServiceMessageValue $name)']" }
        function Start-Sleep {
            param([Parameter(Position = 0)] [double] $Seconds, [int] $Milliseconds)
            $sequence = Join-Path $env:STUB_DIR 'seq'
            $number = 1 + $(if (Test-Path -LiteralPath $sequence) { [int] (Get-Content -Raw -LiteralPath $sequence) } else { 0 })
            Set-Content -LiteralPath $sequence -Value $number -NoNewline
            $call = Join-Path (Join-Path $env:STUB_DIR 'calls') ('{0:d5}' -f $number)
            New-Item -ItemType Directory -Path $call | Out-Null
            Set-Content -LiteralPath (Join-Path $call 'tool') -Value 'sleep' -NoNewline
            Set-Content -LiteralPath (Join-Path $call 'arg.001') -Value $(if ($PSBoundParameters.ContainsKey('Milliseconds')) { "$($Milliseconds / 1000)" } else { "$Seconds" }) -NoNewline
        }
        try {
            . $env:OCTO_SCRIPT
        }
        catch {
            Write-Host "UNHANDLED: $_"
            throw
        }
        if ((Test-Path variable:global:lastexitcode)) {
            exit $LASTEXITCODE
        }

        """;
}
