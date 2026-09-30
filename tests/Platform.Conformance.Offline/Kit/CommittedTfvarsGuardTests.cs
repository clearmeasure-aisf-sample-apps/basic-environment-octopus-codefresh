using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-007 (ADR-IR14, #51): the committed tfvars files hold no operator IP range and no e-mail address. Unit tests plant
/// small trees in a temporary folder; the integration test reads the real repository tree.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class CommittedTfvarsGuardTests
{
    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("tfvars-guard-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>Integration: the committed tfvars files of the repository pass the guard.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardTfvars_RepositoryTree_HoldNoRangeAndNoAddress()
    {
        var tree = new BoundaryTree(KitToolbox.RepositoryRoot);

        tree.TrackedFiles(".tfvars").ShouldNotBeEmpty("no committed tfvars file found");
        CommittedTfvarsGuard.Check(tree).ShouldBeEmpty();
    }

    /// <summary>Unit: empty values, null, comments and the same names in an .example file pass.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardTfvars_EmptyValuesAndExamples_Pass()
    {
        Write("terraform/tier/nonprod.tfvars",
            "tier = \"nonprod\"",
            "api_server_authorized_ip_ranges = []   # 203.0.113.0/24 would go through Octopus",
            "key_vault_allowed_ip_ranges = [",
            "  # nothing yet",
            "]",
            "oncall_email_receivers = {}",
            "tags = {}");
        Write("terraform/tier/nonprod.tfvars.example",
            "api_server_authorized_ip_ranges = [\"203.0.113.0/24\"]",
            "oncall_email_receivers = { ops = \"ops@example.com\" }");

        CommittedTfvarsGuard.Check(new BoundaryTree(root)).ShouldBeEmpty();
    }

    /// <summary>Unit: a non-empty range list or receiver map, on one line or several, is a finding for its file.</summary>
    /// <param name="name">The variable.</param>
    /// <param name="value">A non-empty value.</param>
    [TestCase("api_server_authorized_ip_ranges", "[\"203.0.113.7/32\"]")]
    [TestCase("key_vault_allowed_ip_ranges", "[\n  \"203.0.113.0/24\",\n]")]
    [TestCase("oncall_email_receivers", "{\n  ops = \"team\"\n}")]
    [Capability("CAP-KIT-007")]
    public void Should_GuardTfvars_NonEmptyValue_IsFound(string name, string value)
    {
        Write("terraform/tier/prod.tfvars", "tier = \"prod\"", $"{name} = {value}");

        CommittedTfvarsGuard.Check(new BoundaryTree(root)).ShouldBe([$"terraform/tier/prod.tfvars: {name} is not empty (the file is public; use an Octopus variable TF_VAR_*)"]);
    }

    /// <summary>Unit: an e-mail address on any line, even in a comment, is a finding with its line.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_GuardTfvars_EmailAddressAnywhere_IsFoundWithItsLine()
    {
        Write("terraform/tier/prod.tfvars", "tier = \"prod\"", "# contact: someone@example.com", "tags = {}");

        CommittedTfvarsGuard.Check(new BoundaryTree(root)).ShouldBe(["terraform/tier/prod.tfvars:2: an e-mail address (the file is public; use an Octopus variable TF_VAR_*)"]);
    }

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }
}
