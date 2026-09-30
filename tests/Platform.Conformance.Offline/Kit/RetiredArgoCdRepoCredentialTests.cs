using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-006 (trust boundary TB7, ADR-IR15): the environment repository is public, so Argo CD reads it anonymously and
/// the interim read credential (R11) is retired. No live file of the platform names the credential's Terraform
/// variables, its Octopus variable, its vault secret or its ExternalSecret, and <c>terraform/tier/bootstrap.tf</c> keeps
/// the <c>removed</c> block with <c>destroy = false</c>: removing the resource block alone would make the next plan
/// delete the live Secret and cut Argo CD off before the owner has removed the credential on purpose.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class RetiredArgoCdRepoCredentialTests
{
    /// <summary>Live code and configuration; docs, design and tests describe the retirement and are not scanned.</summary>
    private static readonly string[] LiveRoots = ["terraform", "octopus", ".octopus", "argocd", "gitops", "scripts", "codefresh", "catalogue"];

    private static readonly string[] ScannedExtensions = [".tf", ".tfvars", ".example", ".ocl", ".yaml", ".yml", ".ps1", ".sh", ".json", ".md"];

    private static readonly string[] RetiredNames =
    [
        "argocd_repo_read_credential",
        "argocd_repo_private",
        "ArgoCD.RepoReadCredential",
        "argocd-repo-read-credential",
    ];

    private static readonly Regex ExternalSecretName = new(@"^\s*name:\s*argocd-repo-creds\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex RemovedBlock = new(
        @"removed\s*\{\s*from\s*=\s*kubernetes_secret_v1\.argocd_repo_creds\s*lifecycle\s*\{\s*destroy\s*=\s*false\s*\}\s*\}",
        RegexOptions.CultureInvariant);

    /// <summary>No live file names a retired variable, Octopus variable or vault secret of the read credential.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_ArgoCdRepoCredential_LiveTree_NamesNoRetiredVariableOrVaultSecret()
    {
        var findings = LiveFiles()
            .SelectMany(file => RetiredNames
                .Where(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Relative(file)}: {name}"))
            .ToList();

        findings.ShouldBeEmpty("R11 is retired (ADR-IR15): Argo CD reads the public repository anonymously");
    }

    /// <summary>The GitOps manifests hold no ExternalSecret or Secret named argocd-repo-creds.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_ArgoCdRepoCredential_GitOpsManifests_DefineNoRepoCredsExternalSecret()
    {
        var findings = LiveFiles()
            .Where(file => Path.GetExtension(file) is ".yaml" or ".yml")
            .Where(file => ExternalSecretName.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .ToList();

        findings.ShouldBeEmpty("the ExternalSecret and Secret argocd-repo-creds are retired");
    }

    /// <summary>The bootstrap forgets the Secret in state and never deletes it, so the sequencing cannot be lost.</summary>
    [Test]
    [Capability("CAP-KIT-006")]
    public void Should_ArgoCdRepoCredential_TierBootstrap_ForgetsTheSecretWithoutDestroying()
    {
        var root = KitToolbox.RepositoryRoot;
        var bootstrap = File.ReadAllText(Path.Join(root, "terraform", "tier", "bootstrap.tf"));
        var versions = File.ReadAllText(Path.Join(root, "terraform", "tier", "versions.tf"));

        RemovedBlock.IsMatch(bootstrap).ShouldBeTrue(
            "terraform/tier/bootstrap.tf must keep: removed { from = kubernetes_secret_v1.argocd_repo_creds  lifecycle { destroy = false } }");
        bootstrap.ShouldNotContain("resource \"kubernetes_secret_v1\" \"argocd_repo_creds\"");
        Regex.IsMatch(versions, @"required_version\s*=\s*"">=\s*1\.(1[1-9]|[2-9]\d)", RegexOptions.CultureInvariant)
            .ShouldBeTrue("the removed block needs Terraform 1.7; the write-only arguments need 1.11 or later");
    }

    private static IEnumerable<string> LiveFiles()
    {
        var root = KitToolbox.RepositoryRoot;
        return LiveRoots
            .Select(name => Path.Join(root, name))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(file => ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsBuildOutput(file));
    }

    private static bool IsBuildOutput(string file)
    {
        var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj") || segments.Contains(".terraform") || segments.Contains("node_modules");
    }

    private static string Relative(string file) => Path.GetRelativePath(KitToolbox.RepositoryRoot, file).Replace('\\', '/');
}
