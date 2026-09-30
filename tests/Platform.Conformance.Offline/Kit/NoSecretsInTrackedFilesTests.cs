using Platform.Conformance.Harness;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-007 (public-repository half, #47): no tracked file holds a credential-shaped string. The repository is public,
/// so this offline check backs gitleaks with a scan that needs no tool: private key PEM blocks, GitHub tokens, Octopus API
/// keys, Azure storage account keys, Slack tokens and AWS access key IDs, with the placeholder exception of
/// <c>.gitleaks.toml</c> (<c>&lt;...&gt;</c> tokens and <c>${...}</c> references). The unit tests scan small trees in a temporary
/// folder; every secret-shaped fixture is assembled at run time from pieces, so this source file holds no credential shape
/// and the scan of the real tree stays clean.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class NoSecretsInTrackedFilesTests
{
    private string root = null!;

    /// <summary>Creates an empty tree.</summary>
    [SetUp]
    public void CreateTree() => root = Directory.CreateTempSubdirectory("no-secrets-").FullName;

    /// <summary>Deletes the tree.</summary>
    [TearDown]
    public void DeleteTree() => Directory.Delete(root, recursive: true);

    /// <summary>Integration: no tracked file of the real repository holds a credential shape.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_ScanTrackedFiles_RepositoryTree_FindNoSecretShape()
    {
        var tree = new BoundaryTree(KitToolbox.RepositoryRoot);

        tree.TrackedFiles(string.Empty).ShouldNotBeEmpty("no tracked file found");
        SecretPatternScanner.Scan(tree).Select(finding => finding.ToString()).ShouldBeEmpty();
    }

    /// <summary>Unit: each credential kind is found, with its file, line and kind, and the value is never printed.</summary>
    /// <param name="kind">The expected kind in the finding.</param>
    /// <param name="fixtureName">The fixture to plant.</param>
    [TestCase("private key PEM block", nameof(PemKey))]
    [TestCase("GitHub token", nameof(GitHubPersonalToken))]
    [TestCase("GitHub token", nameof(GitHubOAuthToken))]
    [TestCase("GitHub token", nameof(GitHubServerToken))]
    [TestCase("GitHub fine-grained token", nameof(GitHubFineGrained))]
    [TestCase("Octopus API key", nameof(OctopusKey))]
    [TestCase("Azure storage account key", nameof(AzureStorageKey))]
    [TestCase("Slack token", nameof(SlackBotToken))]
    [TestCase("AWS access key ID", nameof(AwsKeyId))]
    [Capability("CAP-KIT-007")]
    public void Should_ScanTrackedFiles_SecretShapedFixtures_AreFoundWithoutTheValue(string kind, string fixtureName)
    {
        var secret = Fixture(fixtureName);
        Write("config/settings.txt", "first line", $"value = {secret}", "last line");
        Write("clean.txt", "nothing to see");

        var findings = SecretPatternScanner.Scan(new BoundaryTree(root));

        findings.Select(finding => (finding.Path, finding.Line, finding.Rule)).ShouldBe([("config/settings.txt", 2, kind)]);
        findings.Select(finding => finding.ToString()).ShouldAllBe(text => !text.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>Unit: placeholders, references and the documented AWS example are not findings.</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_ScanTrackedFiles_PlaceholdersAndReferences_Pass()
    {
        Write("docs/example.md",
            "OCTOPUS_API_KEY=<OCTOPUS_API_KEY>",
            "Octopus key: API-<octopus-api-key>",
            "AccountKey=<storage-account-key>",
            "AccountKey=${STORAGE_ACCOUNT_KEY}",
            "token: ${{ secrets.GITHUB_TOKEN }}",
            "token: ghp_${{ secrets.TOKEN_SUFFIX }}",
            "-----BEGIN " + "PRIVATE KEY-----",
            "<the key goes here>",
            "The AWS documentation example is " + "AK" + "IA" + "IOSFODNN7EXAMPLE",
            "A short API-KEY and API-Key-Name are not keys");

        SecretPatternScanner.Scan(new BoundaryTree(root)).ShouldBeEmpty();
    }

    /// <summary>Unit: the placeholder rule is the one of <c>.gitleaks.toml</c>.</summary>
    /// <param name="value">A whole secret value.</param>
    /// <param name="placeholder">Whether it is an obvious placeholder.</param>
    [TestCase("<AZURE_TENANT_ID>", true)]
    [TestCase("<kv-platform-nonprod>", true)]
    [TestCase("${VERSION}", true)]
    [TestCase("${{VERSION}}", true)]
    [TestCase("${{ secrets.NAME }}", true)]
    [TestCase("plain-value", false)]
    [TestCase("<a> and <b>", false)]
    [TestCase("prefix<TOKEN>", false)]
    [Capability("CAP-KIT-007")]
    public void Should_ReadValue_PlaceholderRule_MatchesTheGitleaksAllowList(string value, bool placeholder) =>
        SecretPatternScanner.IsPlaceholder(value).ShouldBe(placeholder);

    /// <summary>Unit: a binary file and a file holding a secret in another folder are handled (binary skipped, nested found).</summary>
    [Test]
    [Capability("CAP-KIT-007")]
    public void Should_ScanTrackedFiles_BinaryFilesAreSkippedAndNestedFilesAreScanned()
    {
        Directory.CreateDirectory(Path.Combine(root, "art"));
        File.WriteAllBytes(Path.Combine(root, "art", "image.png"), [0x89, 0x50, 0x00, .. System.Text.Encoding.UTF8.GetBytes(Fixture(nameof(AwsKeyId)))]);
        Write("deep/er/still/file.yaml", "key: " + Fixture(nameof(GitHubPersonalToken)));

        SecretPatternScanner.Scan(new BoundaryTree(root)).Select(finding => finding.Path).ShouldBe(["deep/er/still/file.yaml"]);
    }

    // The fixtures are assembled from pieces, so no credential shape appears in this source.
    private static string PemKey() => "-----BEGIN " + "RSA PRIVATE" + " KEY-----\n" + new string('A', 64);

    private static string GitHubPersonalToken() => "gh" + "p_" + new string('a', 36);

    private static string GitHubOAuthToken() => "gh" + "o_" + new string('b', 36);

    private static string GitHubServerToken() => "gh" + "s_" + new string('c', 36);

    private static string GitHubFineGrained() => "github" + "_pat_" + new string('d', 22) + "_" + new string('e', 59);

    private static string OctopusKey() => "API" + "-" + "ABCDEFGHIJKLMNOPQRSTUVWXYZ" + "0";

    private static string AzureStorageKey() => "Account" + "Key=" + new string('A', 86) + "==";

    private static string SlackBotToken() => "xo" + "xb-" + "123456789012-" + "abcdefghijklmnop";

    private static string AwsKeyId() => "AK" + "IA" + "ABCDEFGHIJKLMNOP";

    private static string Fixture(string name) => name switch
    {
        nameof(PemKey) => PemKey(),
        nameof(GitHubPersonalToken) => GitHubPersonalToken(),
        nameof(GitHubOAuthToken) => GitHubOAuthToken(),
        nameof(GitHubServerToken) => GitHubServerToken(),
        nameof(GitHubFineGrained) => GitHubFineGrained(),
        nameof(OctopusKey) => OctopusKey(),
        nameof(AzureStorageKey) => AzureStorageKey(),
        nameof(SlackBotToken) => SlackBotToken(),
        nameof(AwsKeyId) => AwsKeyId(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture"),
    };

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }
}
