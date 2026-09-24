using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-005: files agree with the contracts (<c>scripts/checks/consistency.sh</c>, which needs python3 with PyYAML).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ContractConsistencyTests
{
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_ConsistencySh_RepositoryTree_Passes()
    {
        KitToolbox.Require("python3", "PYTHON");

        var result = KitToolbox.Bash("scripts/checks/consistency.sh", "--root", KitToolbox.RepositoryRoot);

        if (result.ExitCode == 3)
        {
            Assert.Inconclusive($"consistency.sh skipped: {result.Output.Trim()}");
        }

        result.ExitCode.ShouldBe(0, Failures(result));
    }

    internal static string Failures(ProcessResult result) =>
        string.Join(Environment.NewLine, result.Output.Split('\n').Where(line => line.StartsWith("FAIL", StringComparison.Ordinal)).Take(60)) + Environment.NewLine + result.Error;
}

/// <summary>CAP-KIT-006: tool boundaries hold across the tree (<c>scripts/checks/tool-boundaries.sh</c>).</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ToolBoundaryTests
{
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_ToolBoundariesSh_RepositoryTree_Passes()
    {
        var result = KitToolbox.Bash("scripts/checks/tool-boundaries.sh", "--root", KitToolbox.RepositoryRoot);

        result.ExitCode.ShouldBe(0, result.Output + result.Error);
    }
}

/// <summary>CAP-KIT-007: the repository holds no secret (gitleaks with <c>.gitleaks.toml</c>).</summary>
[TestFixture]
[Category(Categories.Offline)]
public class SecretScanTests
{
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_Gitleaks_RepositoryTree_FindsNoSecret()
    {
        var gitleaks = KitToolbox.Require("gitleaks", "GITLEAKS");
        var root = KitToolbox.RepositoryRoot;

        var result = KitToolbox.Run(gitleaks, ["dir", root, "--config", Path.Combine(root, ".gitleaks.toml"), "--redact", "--no-banner", "--exit-code", "1"], root);

        result.ExitCode.ShouldBe(0, result.Transcript);
    }
}
