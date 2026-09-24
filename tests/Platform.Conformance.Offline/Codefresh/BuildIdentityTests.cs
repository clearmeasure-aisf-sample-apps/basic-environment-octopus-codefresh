using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-012, offline half: nothing declares a grant or a cloud identity for the build cluster. terraform/build has no
/// role assignment, no federated identity credential and workload identity off; codefresh/runner/values.yaml gives the
/// runner, engine and dind no Azure credential or workload identity. The live half reads the role assignments in ARM.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class BuildIdentityTests
{
    /// <summary>terraform/build and the runner values declare no grant and no cloud identity.</summary>
    [Test]
    [Capability("CAP-CF-012")]
    public void Should_ReadTerraformBuildAndRunnerValues_DeclareNoGrantOrCloudIdentity()
    {
        var terraform = Directory.EnumerateFiles(Path.Combine(CodefreshRepository.Root, "terraform", "build"), "*.tf")
            .Select(file => (File: Path.GetFileName(file), Text: File.ReadAllText(file)))
            .ToArray();
        var values = CodefreshRepository.Read("codefresh/runner/values.yaml");

        var grants = terraform.SelectMany(file => CodeLines(file.Text).Where(line => Grant().IsMatch(line)).Select(line => $"terraform/build/{file.File}: {line.Trim()}"));
        var identities = CodeLines(values).Where(line => RunnerIdentity().IsMatch(line)).Select(line => $"codefresh/runner/values.yaml: {line.Trim()}");

        terraform.ShouldNotBeEmpty("terraform/build holds no Terraform file");
        grants.ShouldBeEmpty("grants or cloud identities declared for the build cluster");
        identities.ShouldBeEmpty("cloud identities in the runner values");
    }

    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(line => !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    [GeneratedRegex(@"azurerm_role_assignment|azuread_\w*role_assignment|azurerm_federated_identity_credential|workload_identity_enabled\s*=\s*true")]
    private static partial Regex Grant();

    [GeneratedRegex(@"azure\.workload\.identity|AZURE_CLIENT_(ID|SECRET)|ARM_CLIENT_SECRET|AZURE_FEDERATED_TOKEN")]
    private static partial Regex RunnerIdentity();
}
