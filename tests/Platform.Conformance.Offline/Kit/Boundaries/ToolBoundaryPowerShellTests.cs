using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006 for the conformance scripts in PowerShell 7: the rules that exempt them by name (TB14, TB16, TB18) exempt
/// only the <c>.ps1</c> scripts and still find each forbidden use written in PowerShell elsewhere, the cluster-power rule
/// (TB17) exempts no conformance script at all, and TB23 no longer lists the converted Bash scripts. Each test checks one
/// rule against a small tree in a temporary folder.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ToolBoundaryPowerShellTests
{
    private string root = null!;
    private string report = string.Empty;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("tool-boundaries-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>TB14: the Octopus key only in the conformance scripts, never as a command-line option, and no longer in the Bash names.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB14_PowerShellTree_OctopusKeyOnlyInConformanceScriptsAndNeverAsAnOption()
    {
        Write("codefresh/platform/scripts/octopus-runbook.ps1",
            "Write-PrivateFile $headerFile \"X-Octopus-ApiKey: $($env:OCTOPUS_API_KEY)`n\"",
            "foreach ($name in 'OCTOPUS_URL', 'OCTOPUS_SPACE_ID', 'OCTOPUS_API_KEY') { }");
        Write("codefresh/platform/scripts/conformance-teardown.ps1", "# OCTOPUS_API_KEY reaches octopus-runbook.ps1 through the environment");
        Write("codefresh/platform/scripts/conformance-run.ps1", "Invoke-Octopus -ApiKey $env:OCTOPUS_API_KEY");
        Write("codefresh/platform/scripts/registry-retention.ps1", "$key = $env:OCTOPUS_API_KEY");
        Write("codefresh/platform/scripts/octopus-runbook.sh", "printf 'X-Octopus-ApiKey: %s\\n' \"$OCTOPUS_API_KEY\" >\"$work/headers\"");
        Write("codefresh/apps/demo/scripts/handoff.ps1",
            "& octopus release create --project demo --api-key $key",
            "Invoke-OctopusRelease -Project demo -ApiKey:$key",
            "$headers = @{ 'X-Octopus-ApiKey' = $env:OCTOPUS_API_KEY }");

        var findings = Findings("TB14");

        findings.ShouldBe(
            [
                "codefresh/apps/demo/scripts/handoff.ps1:1",
                "codefresh/apps/demo/scripts/handoff.ps1:2",
                "codefresh/apps/demo/scripts/handoff.ps1:3",
                "codefresh/platform/scripts/conformance-run.ps1:1",
                "codefresh/platform/scripts/octopus-runbook.sh:1",
                "codefresh/platform/scripts/registry-retention.ps1:1",
            ],
            ignoreOrder: true,
            customMessage: report);
    }

    /// <summary>TB16: pushes and commits, also through a PowerShell Git wrapper or with quoted or array arguments, only in the conformance scripts.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB16_PowerShellTree_PushOrCommitOnlyInConformanceScripts()
    {
        Write("codefresh/platform/scripts/conformance-arm.ps1",
            "Invoke-SandboxGit commit --quiet -m $Message | Out-Host",
            "Invoke-SandboxGit push --quiet --force origin \"HEAD:refs/heads/$Branch\" | Out-Host");
        Write("codefresh/platform/scripts/conformance-publish.ps1", "Invoke-SandboxGit push --quiet origin HEAD:refs/heads/conformance-results");
        Write("codefresh/platform/scripts/conformance-arm.sh", "sandbox_git push --quiet origin HEAD:refs/heads/main");
        Write("codefresh/apps/demo/scripts/tag.ps1",
            "git push origin \"v$Version\"",
            "& 'git' 'commit' -m 'bump'",
            "Invoke-DemoGit push origin main",
            "git @('commit', '--allow-empty', '-m', $message)",
            "& git.exe push",
            "git log --oneline -1",
            "$pushed = git rev-parse HEAD",
            "# git push is not allowed here");

        var findings = Findings("TB16");

        findings.ShouldBe(
            [
                "codefresh/apps/demo/scripts/tag.ps1:1",
                "codefresh/apps/demo/scripts/tag.ps1:2",
                "codefresh/apps/demo/scripts/tag.ps1:3",
                "codefresh/apps/demo/scripts/tag.ps1:4",
                "codefresh/apps/demo/scripts/tag.ps1:5",
                "codefresh/platform/scripts/conformance-arm.sh:1",
            ],
            ignoreOrder: true,
            customMessage: report);
    }

    /// <summary>TB17: starting or stopping a cluster, in PowerShell too, is allowed in env-wake and env-sleep only, never in a conformance script.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB17_PowerShellTree_ClusterPowerNotEvenInConformanceScripts()
    {
        Write(".octopus/platform-infrastructure/runbooks/env-sleep.ocl", "step \"stop\" {", "    az aks stop --name $cluster", "}");
        Write("codefresh/platform/scripts/aks-power.ps1",
            "$lines = & curl -fsS -H \"@$dir/arm-headers\" \"https://management.azure.com/subscriptions/$sub/resourceGroups/$group/providers/Microsoft.ContainerService/managedClusters/${name}?api-version=2024-10-01\"",
            "Invoke-RestMethod -Method Post -Uri \"https://management.azure.com/subscriptions/$sub/resourceGroups/$group/providers/Microsoft.ContainerService/managedClusters/$name/stop?api-version=2024-10-01\"");
        Write("codefresh/platform/scripts/conformance-arm.ps1", "Start-AzAksCluster -ResourceGroupName $group -Name $name");

        var findings = Findings("TB17");

        findings.ShouldBe(["codefresh/platform/scripts/aks-power.ps1:2", "codefresh/platform/scripts/conformance-arm.ps1:1"], ignoreOrder: true, customMessage: report);
    }

    /// <summary>TB18: runbook-run calls in PowerShell only in the conformance scripts that run runbooks.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB18_PowerShellTree_RunbookRunsOnlyInConformanceScripts()
    {
        Write("codefresh/platform/scripts/octopus-runbook.ps1",
            "$url = \"$base/projects/$projectId/$ref/runbooks/$runbookId/runbookRuns/preview/$environmentId\"",
            "$url = \"$base/projects/$projectId/$ref/runbooks/$runbookId/run/v1\"");
        Write("codefresh/platform/scripts/octopus-runbook.sh", "octo -X POST --data-binary @\"$work/run.json\" \"$base/projects/$project_id/$ref/runbooks/$runbook_id/run/v1\"");
        Write("codefresh/platform/scripts/aks-power.ps1", "Invoke-RestMethod -Method Post -Uri \"$octopus/api/$space/runbookRuns\" -Headers $headers");
        Write("codefresh/apps/demo/scripts/wake.ps1",
            "& octopus runbook run --project platform-infrastructure --name env-wake",
            "Invoke-RestMethod -Method Post -Uri \"$base/projects/$project/runbooks/$runbook/run/v1\" -Body $body");

        var findings = Findings("TB18");

        findings.ShouldBe(
            [
                "codefresh/apps/demo/scripts/wake.ps1:1",
                "codefresh/apps/demo/scripts/wake.ps1:2",
                "codefresh/platform/scripts/aks-power.ps1:1",
                "codefresh/platform/scripts/octopus-runbook.sh:1",
            ],
            ignoreOrder: true,
            customMessage: report);
    }

    /// <summary>TB23: the seven converted conformance scripts are no longer pending, so a Bash copy of one fails the rule; their .ps1 files are fine.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_TB23_ConvertedConformanceScripts_AreNoLongerPendingShellScripts()
    {
        string[] converted = ["aks-power", "conformance-arm", "conformance-publish", "conformance-run", "conformance-teardown", "octopus-runbook", "sandbox-git"];
        foreach (var name in converted)
        {
            Write($"codefresh/platform/scripts/{name}.ps1", "Set-StrictMode -Version Latest");
        }

        Write("codefresh/platform/scripts/conformance-arm.sh", "#!/usr/bin/env bash");
        Write("codefresh/platform/scripts/sandbox-git.sh", "#!/usr/bin/env bash");

        var result = ToolBoundaryRules.Check(new BoundaryTree(root), "TB23");

        ScriptLanguageRule.PendingShellScripts.Where(entry => entry.StartsWith("codefresh/platform/", StringComparison.Ordinal)).ShouldBeEmpty();
        result.Findings.Where(finding => finding.Path.StartsWith("codefresh/platform/", StringComparison.Ordinal)).Select(finding => finding.Path)
            .ShouldBe(["codefresh/platform/scripts/conformance-arm.sh", "codefresh/platform/scripts/sandbox-git.sh"], ignoreOrder: true, customMessage: result.Report());
        result.Findings.ShouldAllBe(finding => !finding.Path.EndsWith(".ps1", StringComparison.Ordinal));
    }

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    // The rule's report is the failure message only: the findings of these trees are expected, and a log line
    // "FAIL TB14" from a passing test would mislead a reader of the env-checks log.
    private string[] Findings(string ruleId)
    {
        var result = ToolBoundaryRules.Check(new BoundaryTree(root), ruleId);
        report = result.Report();
        result.Skipped.ShouldBeFalse(report);
        return result.Findings.Select(finding => $"{finding.Path}:{finding.Line}").ToArray();
    }
}
