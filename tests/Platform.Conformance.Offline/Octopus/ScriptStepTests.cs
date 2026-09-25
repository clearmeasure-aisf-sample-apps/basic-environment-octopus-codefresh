using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-KIT-006 (TB23: scripts are PowerShell 7), for the inline scripts of the OCL files under .octopus/ and
/// octopus/templates/ (docs/scripting.md): every step with <c>Octopus.Action.Script.Syntax = "PowerShell"</c> parses as
/// PowerShell and calls none of the Bash script functions (get_octopusvariable, set_octopusvariable, fail_step,
/// write_highlight, write_warning), and no inline script holds a dollar-brace or percent-brace sequence, which an OCL
/// heredoc reads as template syntax. Without pwsh the test is Inconclusive, and failed when <c>CI=true</c>.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class ScriptStepTests
{
    private const string ParseScript = """
        param([string]$List)
        foreach ($path in [System.IO.File]::ReadAllLines($List)) {
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
            foreach ($problem in $errors) {
                [Console]::Out.WriteLine("$path`t$($problem.Extent.StartLineNumber)`t$($problem.Message)")
            }
        }

        """;

    /// <summary>The PowerShell script steps parse, and no inline script holds OCL template syntax.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_ReadOcl_PowerShellScriptSteps_ParseAndAvoidTemplateSyntax()
    {
        var pwsh = KitToolbox.Require("pwsh");
        var folder = Path.Combine(Path.GetTempPath(), "platform-script-step-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var problems = new List<string>();
            var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in OctopusRepository.OclFiles(".octopus").Concat(OctopusRepository.OclFiles("octopus/templates")))
            {
                foreach (var step in OctopusRepository.Steps(OctopusRepository.Read(file)))
                {
                    var body = ScriptBody().Match(step.Text);
                    if (!body.Success)
                    {
                        continue;
                    }

                    var where = $"{file} step {step.Slug}";
                    var text = body.Groups["body"].Value;
                    if (TemplateSyntax().IsMatch(text))
                    {
                        problems.Add($"{where}: the script holds a dollar-brace or percent-brace sequence (OCL template syntax)");
                    }

                    if (!PowerShellSyntax().IsMatch(step.Text))
                    {
                        continue;
                    }

                    if (BashFunction().Match(text) is { Success: true } bash)
                    {
                        problems.Add($"{where}: PowerShell step calls the Bash script function {bash.Value}");
                    }

                    var path = Path.Combine(folder, $"{bodies.Count}.ps1");
                    File.WriteAllText(path, text);
                    bodies[path] = where;
                }
            }

            var list = Path.Combine(folder, "bodies.txt");
            File.WriteAllLines(list, bodies.Keys);
            var parser = Path.Combine(folder, "parse.ps1");
            File.WriteAllText(parser, ParseScript);

            var result = KitToolbox.Run(pwsh, ["-NoProfile", "-NonInteractive", "-File", parser, list], folder);

            result.ExitCode.ShouldBe(0, result.Transcript);
            problems.AddRange(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t', 3))
                .Select(parts => $"{bodies[parts[0]]}, script line {parts[1]}: {parts[2]}"));
            bodies.ShouldNotBeEmpty("no PowerShell script step under .octopus/ or octopus/templates/");
            problems.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex(@"Octopus\.Action\.Script\.ScriptBody = <<-EOT[ \t]*\n(?<body>.*?)\n[ \t]*EOT[ \t]*\n", RegexOptions.Singleline)]
    private static partial Regex ScriptBody();

    [GeneratedRegex(@"^[ \t]*Octopus\.Action\.Script\.Syntax = ""PowerShell""", RegexOptions.Multiline)]
    private static partial Regex PowerShellSyntax();

    [GeneratedRegex(@"\$\{|%\{")]
    private static partial Regex TemplateSyntax();

    [GeneratedRegex(@"\b(get_octopusvariable|set_octopusvariable|fail_step|write_highlight|write_warning)\b")]
    private static partial Regex BashFunction();
}
