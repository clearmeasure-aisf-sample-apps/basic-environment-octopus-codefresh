using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 for the release-step scripts of apps and starters: TB14 and TB18 exempt, by name, the scripts that steps
/// wake_nonprod and octopus_preflight run (<c>scripts/wake-nonprod.ps1</c>, <c>scripts/octopus-preflight.ps1</c>) and no
/// other script of an app or starter. Each test checks one rule against a small tree in a temporary folder.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ToolBoundaryAppScriptTests
{
    private const string RunUrl = "$runUrl = \"$base/spaces/$space/projects/$projectId/refs%2Fheads%2Fmain/runbooks/env-wake/run/v1\"";

    private string root = null!;
    private string report = string.Empty;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("tool-boundaries-apps-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>TB14: OCTOPUS_API_KEY and the X-Octopus-ApiKey header in the wake and preflight scripts only; another key name fails even there.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB14_AppScriptTree_OctopusKeyOnlyInTheWakeAndPreflightScripts()
    {
        Write("codefresh/apps/demo/scripts/wake-nonprod.ps1",
            "foreach ($key in @('OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY')) { }",
            "[System.IO.File]::WriteAllText($headers, \"X-Octopus-ApiKey: $($env:OCTOPUS_API_KEY)`n\")");
        Write("codefresh/templates/minimal/scripts/octopus-preflight.ps1", "foreach ($key in @('OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY')) { }");
        Write("codefresh/templates/minimal/scripts/wake-nonprod.ps1", "$key = $env:OCTO_API_KEY");
        Write("codefresh/apps/demo/scripts/prepare.ps1", "$key = $env:OCTOPUS_API_KEY");
        Write("codefresh/apps/demo/scripts/wake-nonprod.sh", "printf 'X-Octopus-ApiKey: %s\\n' \"$OCTOPUS_API_KEY\" >\"$headers\"");

        var findings = Findings("TB14");

        findings.ShouldBe(
            [
                "codefresh/apps/demo/scripts/prepare.ps1:1",
                "codefresh/apps/demo/scripts/wake-nonprod.sh:1",
                "codefresh/templates/minimal/scripts/wake-nonprod.ps1:1",
            ],
            ignoreOrder: true,
            customMessage: report);
    }

    /// <summary>TB18: a runbook-run call in wake-nonprod.ps1 of an app or starter only; any other script of theirs fails.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB18_AppScriptTree_RunbookRunsOnlyInTheWakeScript()
    {
        Write("codefresh/apps/demo/scripts/wake-nonprod.ps1", RunUrl);
        Write("codefresh/templates/multi-image/scripts/wake-nonprod.ps1", RunUrl);
        Write("codefresh/apps/demo/scripts/octopus-preflight.ps1", RunUrl);
        Write("codefresh/templates/minimal/scripts/wake.ps1", "& octopus runbook run --project platform-infrastructure --name env-wake");

        var findings = Findings("TB18");

        findings.ShouldBe(["codefresh/apps/demo/scripts/octopus-preflight.ps1:1", "codefresh/templates/minimal/scripts/wake.ps1:1"], ignoreOrder: true, customMessage: report);
    }

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    // The rule's report is the failure message only: the findings of these trees are expected.
    private string[] Findings(string ruleId)
    {
        var result = ToolBoundaryRules.Check(new BoundaryTree(root), ruleId);
        report = result.Report();
        result.Skipped.ShouldBeFalse(report);
        return result.Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();
    }
}
