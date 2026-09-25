using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

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
