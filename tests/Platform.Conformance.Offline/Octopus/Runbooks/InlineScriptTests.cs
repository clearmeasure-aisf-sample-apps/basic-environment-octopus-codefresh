using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-KIT-006: the inline PowerShell of the platform projects' OCL (<c>.octopus/platform-infrastructure</c> and
/// <c>.octopus/platform-wake</c>) meets the scripting standard, as <c>validate-all.ps1 powershell</c> does for .ps1 files
/// (docs/scripting.md): every body parses, passes PSScriptAnalyzer with <c>PSScriptAnalyzerSettings.psd1</c>, starts with
/// strict mode and stop-on-error, never names a variable that ends in <c>?</c> (PowerShell reads <c>$x?</c> as one
/// name), and holds none of the OCL hazards: <c>${</c> and <c>%{</c> start HCL templates, <c>#{</c> an Octopus
/// substitution, so these scripts read variables through <c>$OctopusParameters</c> only.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class InlineScriptTests
{
    private static readonly string[] Folders = [".octopus/platform-infrastructure", ".octopus/platform-wake"];

    /// <summary>Every inline PowerShell script of the platform projects parses, passes the analyzer and avoids the hazards.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_InlinePowerShell_PlatformOcl_MeetsTheScriptingStandard()
    {
        var bodies = PowerShellBodies();
        var problems = new List<string>();
        foreach (var (name, body) in bodies)
        {
            problems.AddRange(new[] { "${", "%{", "#{" }.Where(hazard => body.Contains(hazard, StringComparison.Ordinal)).Select(hazard => $"{name}: contains {hazard}"));
            if (!Preamble().IsMatch(body))
            {
                problems.Add($"{name}: does not start with Set-StrictMode -Version Latest, $ErrorActionPreference = 'Stop' and $PSNativeCommandUseErrorActionPreference = $true");
            }
        }

        bodies.ShouldNotBeEmpty("no inline PowerShell script under .octopus/platform-infrastructure or .octopus/platform-wake");
        problems.AddRange(Analyze(bodies));
        problems.ShouldBeEmpty();
    }

    private static List<(string Name, string Body)> PowerShellBodies() =>
        Folders.SelectMany(OctopusRepository.OclFiles)
            .SelectMany(file => OctopusRepository.Steps(OctopusRepository.Read(file))
                .Where(step => Regex.IsMatch(step.Text, @"^\s*Octopus\.Action\.Script\.Syntax = ""PowerShell""", RegexOptions.Multiline))
                .Select(step => (Name: $"{file}#{step.Slug}", Body: RunbookScript.InlineBody(step.Text))))
            .Where(item => item.Body is not null)
            .Select(item => (item.Name, item.Body!))
            .ToList();

    /// <summary>Parses every body and runs PSScriptAnalyzer over it in one pwsh process.</summary>
    private static IEnumerable<string> Analyze(List<(string Name, string Body)> bodies)
    {
        var pwsh = KitToolbox.Require("pwsh");
        var folder = Path.Combine(Path.GetTempPath(), "platform-runbook-tests", "lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var index = new List<string>();
            for (var number = 0; number < bodies.Count; number++)
            {
                var path = Path.Combine(folder, $"{number:D3}.ps1");
                File.WriteAllText(path, bodies[number].Body);
                index.Add($"{path}\t{bodies[number].Name}");
            }

            File.WriteAllLines(Path.Combine(folder, "index.tsv"), index);
            var lint = Path.Combine(folder, "lint.ps1");
            File.WriteAllText(lint, LintScript);
            var settings = Path.Combine(KitToolbox.RepositoryRoot, "PSScriptAnalyzerSettings.psd1");
            var result = KitToolbox.Run(pwsh, ["-NoProfile", "-NonInteractive", "-File", lint, Path.Combine(folder, "index.tsv"), settings], folder, TimeSpan.FromMinutes(5));
            if (result.ExitCode == 3)
            {
                var message = "PSScriptAnalyzer is not installed";
                if (KitToolbox.IsCi)
                {
                    Assert.Fail($"{message} (CI=true: env-checks must provide it)");
                }

                Assert.Inconclusive($"{message}; the analyzer part runs where it is installed (env-checks)");
            }

            result.ExitCode.ShouldBeOneOf([0, 1], result.Transcript);
            return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex(@"\ASet-StrictMode -Version Latest\n\$ErrorActionPreference = 'Stop'\n\$PSNativeCommandUseErrorActionPreference = \$true\n")]
    private static partial Regex Preamble();

    private const string LintScript = """
        param([string] $Index, [string] $Settings)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
            exit 3
        }
        Import-Module PSScriptAnalyzer
        $status = 0
        foreach ($line in Get-Content -LiteralPath $Index) {
            $path, $name = $line -split "`t", 2
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $tokens, [ref] $errors)
            foreach ($parseError in $errors) {
                Write-Output "$($name):$($parseError.Extent.StartLineNumber) does not parse: $($parseError.Message)"
                $status = 1
            }
            $variables = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)
            foreach ($variable in $variables | Where-Object { $_.VariablePath.UserPath.EndsWith('?') }) {
                Write-Output "$($name):$($variable.Extent.StartLineNumber) variable `$$($variable.VariablePath.UserPath) ends with '?': write `$(`$x)? instead"
                $status = 1
            }
            foreach ($finding in @(Invoke-ScriptAnalyzer -Path $path -Settings $Settings)) {
                Write-Output "$($name):$($finding.Line) $($finding.RuleName): $($finding.Message)"
                $status = 1
            }
        }
        exit $status

        """;
}
