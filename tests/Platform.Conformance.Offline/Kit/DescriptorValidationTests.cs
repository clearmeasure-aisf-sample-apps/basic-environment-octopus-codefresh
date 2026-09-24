using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-002: descriptors are validated. The committed descriptors pass <c>Platform.Onboarding check</c>; every
/// invalid fixture under Kit/TestData/descriptors/invalid is rejected with the message its first line names
/// (<c># expect: &lt;text&gt;</c>).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class DescriptorValidationTests
{
    private const string ExpectPrefix = "# expect:";

    public static IEnumerable<TestCaseData> InvalidDescriptors() =>
        Directory.EnumerateFiles(Path.Combine(KitToolbox.TestData, "descriptors", "invalid"), "*.yaml")
            .Order(StringComparer.Ordinal)
            .Select(path => new TestCaseData(Path.GetFileName(path)).SetArgDisplayNames(Path.GetFileNameWithoutExtension(path)));

    [Test]
    [Capability("CAP-KIT-002")]
    public void Should_Check_CommittedDescriptors_AreAccepted()
    {
        var root = KitToolbox.RepositoryRoot;

        var result = KitToolbox.Onboarding(root, "check", "--scope", "descriptors", "--root", root);

        result.ExitCode.ShouldBe(0, result.Transcript);
        Directory.GetFiles(Path.Combine(root, "apps"), "*.yaml").Length.ShouldBeGreaterThanOrEqualTo(2);
    }

    [TestCaseSource(nameof(InvalidDescriptors))]
    [Capability("CAP-KIT-002")]
    public void Should_Check_InvalidDescriptor_IsRejected(string fixture)
    {
        using var workspace = KitWorkspace.Create();
        var source = Path.Combine(KitToolbox.TestData, "descriptors", "invalid", fixture);
        var expected = File.ReadLines(source).First(line => line.StartsWith(ExpectPrefix, StringComparison.Ordinal))[ExpectPrefix.Length..].Trim();
        File.Copy(source, workspace.PathOf($"apps/{fixture}"));

        var result = workspace.Onboarding("check", "--scope", "descriptors");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain(expected, Case.Sensitive, result.Transcript);
    }
}
