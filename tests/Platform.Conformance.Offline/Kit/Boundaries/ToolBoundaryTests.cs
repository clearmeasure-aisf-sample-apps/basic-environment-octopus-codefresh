using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// CAP-KIT-006: tool boundaries hold across the tree, and no platform file names an app. One test per rule of
/// <see cref="ToolBoundaryRules"/>, the C# port of <c>scripts/checks/tool-boundaries.sh</c> (TB01 to TB22), plus TB23
/// (scripts are PowerShell 7) and TB24 (GitHub runs only board-only and alert-only workflows). A rule whose paths are absent from a
/// partial tree is Inconclusive. The failure message lists every finding as the script prints it; the report of each
/// rule also goes to the test output.
/// </summary>
[TestFixture]
public class ToolBoundaryTests
{
    private BoundaryTree tree = null!;

    /// <summary>Reads the repository tree once for all rules.</summary>
    [OneTimeSetUp]
    public void ReadRepositoryTree() => tree = new BoundaryTree(KitToolbox.RepositoryRoot);

    /// <summary>TB01: Codefresh builds; no deploy, approval, helm or launch-composition step.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB01_RepositoryTree_CodefreshOnlyBuilds() => AssertRuleHolds("TB01");

    /// <summary>TB02: Codefresh never reaches an app cluster (argocd, kubectl, helm install/upgrade, az aks credentials).</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB02_RepositoryTree_CodefreshNeverReachesAppClusters() => AssertRuleHolds("TB02");

    /// <summary>TB03: the Codefresh GitOps Runtime and Promotions stay off.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB03_RepositoryTree_GitOpsRuntimeAndPromotionsStayOff() => AssertRuleHolds("TB03");

    /// <summary>TB04: Octopus is the only image-tag writer; no Argo CD Image Updater.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB04_RepositoryTree_NoArgoCdImageUpdater() => AssertRuleHolds("TB04");

    /// <summary>TB05: freezes live in Octopus; no Argo CD syncWindows.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB05_RepositoryTree_NoArgoCdSyncWindows() => AssertRuleHolds("TB05");

    /// <summary>TB06: no floating <c>latest</c> tag in desired state, deployment config or pipelines.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB06_RepositoryTree_NoFloatingLatestTag() => AssertRuleHolds("TB06");

    /// <summary>TB07: Octopus never applies Kubernetes state (kubectl, Helm, Kubernetes steps, argocd app sync).</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB07_RepositoryTree_OctopusNeverAppliesKubernetesState() => AssertRuleHolds("TB07");

    /// <summary>TB08: the tier layers hold no role assignment, role definition, lock or policy assignment.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB08_RepositoryTree_TierLayersHoldNoGrantLockOrPolicy() => AssertRuleHolds("TB08");

    /// <summary>TB09: Octopus annotations only in the tenant chart; no tenant annotation.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB09_RepositoryTree_OctopusAnnotationsOnlyInTenantChart() => AssertRuleHolds("TB09");

    /// <summary>TB10: trigger sync stays off in the Argo CD step.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB10_RepositoryTree_TriggerSyncStaysOff() => AssertRuleHolds("TB10");

    /// <summary>TB11: Codefresh creates releases; no feed or built-in release trigger.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB11_RepositoryTree_NoFeedOrBuiltInReleaseTrigger() => AssertRuleHolds("TB11");

    /// <summary>TB12: no Octopus deployment target on an app cluster.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB12_RepositoryTree_NoDeploymentTargetOnAppClusters() => AssertRuleHolds("TB12");

    /// <summary>TB13a: no Octopus project uses the stored Azure Runtime Provisioner.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB13a_RepositoryTree_NoProjectUsesStoredProvisioner() => AssertRuleHolds("TB13a");

    /// <summary>TB13b: no pipeline attaches the stored Codefresh contexts.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB13b_RepositoryTree_NoPipelineAttachesStoredContext() => AssertRuleHolds("TB13b");

    /// <summary>TB13c: no project includes the stored variable sets.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB13c_RepositoryTree_NoProjectIncludesStoredVariableSet() => AssertRuleHolds("TB13c");

    /// <summary>TB14: the Octopus API key only as OCTOPUS_API_KEY or X-Octopus-ApiKey, in release and conformance pipelines.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB14_RepositoryTree_OctopusKeyOnlyInReleaseAndConformancePipelines() => AssertRuleHolds("TB14");

    /// <summary>TB15: Kyverno image verification never mutates image references.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB15_RepositoryTree_ImageVerificationNeverMutatesDigest() => AssertRuleHolds("TB15");

    /// <summary>TB16: Codefresh never commits or pushes, except the conformance pipelines.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB16_RepositoryTree_CodefreshNeverCommitsOrPushes() => AssertRuleHolds("TB16");

    /// <summary>TB17: cluster start, stop and alert-rule toggles only in env-wake and env-sleep.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB17_RepositoryTree_ClusterPowerOnlyInEnvWakeAndEnvSleep() => AssertRuleHolds("TB17");

    /// <summary>TB18: runbook-run REST calls only in the platform places.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB18_RepositoryTree_RunbookRunsOnlyInPlatformPlaces() => AssertRuleHolds("TB18");

    /// <summary>TB19: no platform account or Azure start right in app projects, starters or app grants.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB19_RepositoryTree_AppsHoldNoClusterStartRight() => AssertRuleHolds("TB19");

    /// <summary>TB20: Octopus key variables only in platform-infrastructure and platform-wake; no literal API key.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB20_RepositoryTree_OctopusKeyVariablesOnlyInPlatformProjects() => AssertRuleHolds("TB20");

    /// <summary>TB21: trust boundary TB2; one runtime, no grant on the build cluster, no cloud identity for pipelines.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB21_RepositoryTree_BuildClusterHasOneRuntimeAndNoCloudIdentity() => AssertRuleHolds("TB21");

    /// <summary>TB22: no platform file names an app outside the app-scoped paths (name lint).</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB22_RepositoryTree_PlatformFilesNameNoApp() => AssertRuleHolds("TB22");

    /// <summary>TB23: scripts are PowerShell 7; shell scripts and Bash steps only from the exception and pending lists.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB23_RepositoryTree_ScriptsArePowerShell7() => AssertRuleHolds("TB23");

    /// <summary>TB24: GitHub runs no platform workflow; only the listed board-only and alert-only workflows, which stay in their lane.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    [Category(Categories.Offline)]
    public void Should_TB24_RepositoryTree_OnlyBoardOnlyGitHubWorkflows() => AssertRuleHolds("TB24");

    private void AssertRuleHolds(string ruleId)
    {
        var result = ToolBoundaryRules.Check(tree, ruleId);

        TestContext.Out.Write(result.Report());
        if (result.Skipped)
        {
            Assert.Inconclusive(result.Report());
        }

        result.Findings.Select(finding => finding.ToString()).ShouldBeEmpty(result.Report());
    }
}
