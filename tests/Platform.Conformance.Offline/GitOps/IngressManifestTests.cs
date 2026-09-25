using Platform.Conformance.Harness;
using Platform.Conformance.Tests.GitOps;

namespace Platform.Conformance.Offline.GitOps;

/// <summary>
/// CAP-GIT-012, offline part: Terraform alone owns the Azure DNS label of <c>pip-platform-&lt;tier&gt;-ingress</c> (R35,
/// <c>terraform/tier</c> <c>ingress_domain_name_label</c>). No manifest of the GitOps or Argo CD trees sets the Azure
/// cloud-provider annotation <c>service.beta.kubernetes.io/azure-dns-label-name</c>, so the cloud provider never writes or
/// clears the label, while the Envoy Service still names the tier's address through <c>azure-pip-name</c>.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class IngressManifestTests
{
    private const string DnsLabelAnnotation = "service.beta.kubernetes.io/azure-dns-label-name";
    private const string PipNameAnnotation = "service.beta.kubernetes.io/azure-pip-name";

    [Test]
    [Capability("CAP-GIT-012")]
    public void Should_Declare_NoManifest_SetsTheCloudProviderDnsLabel()
    {
        var root = GitOpsRepository.Root;

        var offenders = new[] { "gitops", "argocd" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".yaml", StringComparison.Ordinal) || file.EndsWith(".yml", StringComparison.Ordinal) || file.EndsWith(".tpl", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (Line: line, Number: index + 1))
                .Where(entry => !entry.Line.TrimStart().StartsWith('#') && entry.Line.Contains("azure-dns-label-name", StringComparison.Ordinal))
                .Select(entry => $"{Path.GetRelativePath(root, file)}:{entry.Number}: {entry.Line.Trim()}"))
            .ToArray();

        offenders.ShouldBeEmpty($"{DnsLabelAnnotation} would let the cloud provider own the DNS label that terraform/tier sets (R35)");
    }

    [Test]
    [Capability("CAP-GIT-012")]
    public void Should_Declare_EveryTierOverlay_NamesItsIngressAddress()
    {
        var root = GitOpsRepository.Root;

        Assert.Multiple(() =>
        {
            foreach (var tier in new[] { "nonprod", "prod" })
            {
                var overlay = File.ReadAllText(Path.Combine(root, "gitops", "platform", "ingress", "overlays", tier, "kustomization.yaml"));
                overlay.ShouldContain(PipNameAnnotation.Replace("/", "~1", StringComparison.Ordinal), Case.Sensitive, $"the {tier} overlay patches {PipNameAnnotation}");
                overlay.ShouldContain($"value: pip-platform-{tier}-ingress", Case.Sensitive, $"the {tier} Envoy Service claims pip-platform-{tier}-ingress, the address that carries the label");
            }
        });
    }
}
