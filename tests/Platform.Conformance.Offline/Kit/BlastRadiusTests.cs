using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit;

/// <summary>
/// CAP-KIT-004: an onboarding change touches only app-scoped paths. <c>Platform.Onboarding check --changes</c> fails
/// every fixture diff under Kit/TestData/changes named <c>fail-*</c> and passes every <c>pass-*</c>.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class BlastRadiusTests
{
    public static IEnumerable<TestCaseData> FailingChanges() => Changes("fail-");

    public static IEnumerable<TestCaseData> PassingChanges() => Changes("pass-");

    [TestCaseSource(nameof(FailingChanges))]
    [Capability("CAP-KIT-004")]
    public void Should_Check_OnboardingChangeOutsideAppPaths_Fails(string fixture)
    {
        using var workspace = KitWorkspace.Create();

        var result = workspace.Onboarding("check", "--scope", "descriptors", "--changes", Path.Combine(KitToolbox.TestData, "changes", fixture));

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain("blast-radius");
    }

    [TestCaseSource(nameof(PassingChanges))]
    [Capability("CAP-KIT-004")]
    public void Should_Check_ChangeWithinAppPaths_Passes(string fixture)
    {
        using var workspace = KitWorkspace.Create();

        var result = workspace.Onboarding("check", "--scope", "descriptors", "--changes", Path.Combine(KitToolbox.TestData, "changes", fixture));

        result.ExitCode.ShouldBe(0, result.Transcript);
    }

    private static IEnumerable<TestCaseData> Changes(string prefix) =>
        Directory.EnumerateFiles(Path.Combine(KitToolbox.TestData, "changes"), prefix + "*.txt")
            .Order(StringComparer.Ordinal)
            .Select(path => new TestCaseData(Path.GetFileName(path)).SetArgDisplayNames(Path.GetFileNameWithoutExtension(path)));
}
