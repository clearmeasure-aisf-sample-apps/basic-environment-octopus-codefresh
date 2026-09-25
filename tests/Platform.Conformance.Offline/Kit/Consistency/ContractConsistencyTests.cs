using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>
/// CAP-KIT-005: platform files agree with the contracts and the descriptors. One test per check of
/// <see cref="ConsistencyChecks"/> (the C# port of <c>scripts/checks/consistency.sh</c>) over the repository tree
/// (<c>PLATFORM_REPO_ROOT</c> names another tree). A check passes when it reports no FAIL; WARN and SKIP are advisory, as
/// in the script. Every line a check reports is written to the test output as the script prints it
/// (<c>STATUS ID [owner] path: message</c>), and a failure lists every finding the same way.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ContractConsistencyTests
{
    /// <summary>C01: the contracts file parses and holds every section.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C01ContractsParse_RepositoryTree_Passes() => AssertPasses("C01");

    /// <summary>C02: the tenant chart renders the Octopus annotations; no platform file carries a forbidden one.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C02OctopusAnnotations_RepositoryTree_Passes() => AssertPasses("C02");

    /// <summary>C03: ApplicationSet <c>apps</c> of each tier renders one tenant per descriptor.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C03ApplicationSetApps_RepositoryTree_Passes() => AssertPasses("C03");

    /// <summary>C04: each cluster holds exactly the platform AppProjects, and <c>default</c> stays locked.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C04AppProjects_RepositoryTree_Passes() => AssertPasses("C04");

    /// <summary>C05: the platform namespaces exist with label <c>tier: platform</c>; no app namespace is declared.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C05PlatformNamespaces_RepositoryTree_Passes() => AssertPasses("C05");

    /// <summary>C06: the root application and the Argo CD bootstrap values of each tier match §7.0.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C06ArgoCdBootstrap_RepositoryTree_Passes() => AssertPasses("C06");

    /// <summary>C09: every app overlay renders with kustomize inside the app's fence (Inconclusive without kustomize, failed when CI=true).</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C09RenderedAppOverlays_RepositoryTree_Passes() => AssertPasses("C09", RequireKustomize());

    /// <summary>C10: database connection strings stay inside the app's own namespaces.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C10DatabaseEndpoints_RepositoryTree_Passes() => AssertPasses("C10");

    /// <summary>C11: platform secrets come from the platform vault; app secrets only from stores &lt;app&gt;-&lt;env&gt;.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C11StoresAndVaults_RepositoryTree_Passes() => AssertPasses("C11");

    /// <summary>C12: the platform runbooks exist and match §7.0; retired runbooks are gone.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C12PlatformRunbooks_RepositoryTree_Passes() => AssertPasses("C12");

    /// <summary>C15: octopus/terraform names the space objects of §7.0 and builds the per-app shells from the descriptors.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C15OctopusTerraform_RepositoryTree_Passes() => AssertPasses("C15");

    /// <summary>C18: the tenant chart renders the per-app signer policy; the generic Kyverno policies are present.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C18SignerAndKyverno_RepositoryTree_Passes() => AssertPasses("C18");

    /// <summary>C19: every Terraform layer exists and declares no Azure SQL.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C19TerraformLayers_RepositoryTree_Passes() => AssertPasses("C19");

    /// <summary>C20: retired paths are gone and no platform file names a retired name.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C20RetiredPathsAndNames_RepositoryTree_Passes() => AssertPasses("C20");

    /// <summary>C21: no platform file, descriptor or fixture carries a retired placeholder.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C21Placeholders_RepositoryTree_Passes() => AssertPasses("C21");

    /// <summary>C22: every TF_VAR_* a platform runbook sets is a variable of the layer it runs.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C22RunbookInputs_RepositoryTree_Passes() => AssertPasses("C22");

    /// <summary>C23: sleep and wake run on the dynamic pool; app processes and in-cluster runbooks wake the cluster first.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C23SleepAndWake_RepositoryTree_Passes() => AssertPasses("C23");

    /// <summary>C24: every descriptor parses, has the schema version and is named like its file; the fixture descriptor exists.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C24Descriptors_RepositoryTree_Passes() => AssertPasses("C24");

    /// <summary>C25: fork events are off, contexts stay in their lane, and pipelines and starters keep handshake M1 and M3.</summary>
    [Test]
    [Capability("CAP-KIT-005")]
    public void Should_C25CodefreshHandshake_RepositoryTree_Passes() => AssertPasses("C25");

    private static void AssertPasses(string id, string? kustomize = null)
    {
        var findings = new ConsistencyChecks(KitToolbox.RepositoryRoot, kustomize: kustomize).Run(id);
        foreach (var finding in findings)
        {
            TestContext.Out.WriteLine(finding.ToString());
        }

        var failures = findings.Count(finding => finding.Status == FindingStatus.Fail);

        failures.ShouldBe(0, Describe(id, findings));
    }

    /// <summary>Every finding but the PASS lines, one per line, as the script prints them.</summary>
    private static string Describe(string id, IReadOnlyList<ConsistencyFinding> findings) =>
        $"{id} findings (STATUS ID [owner] path: message):" + Environment.NewLine
        + string.Join(Environment.NewLine, findings.Where(finding => finding.Status != FindingStatus.Pass));

    /// <summary>kustomize for C09, or the end of the test: Inconclusive locally, failed when <c>CI=true</c> (env-checks must provide it).</summary>
    private static string RequireKustomize()
    {
        if (Kustomize.Find() is { } kustomize)
        {
            return kustomize;
        }

        const string message = "kustomize not found in KUSTOMIZE or on PATH; C09 renders the app overlays where kustomize is installed";
        if (KitToolbox.IsCi)
        {
            Assert.Fail($"{message} (CI=true: env-checks must provide it)");
        }

        Assert.Inconclusive(message);
        return string.Empty;
    }
}
