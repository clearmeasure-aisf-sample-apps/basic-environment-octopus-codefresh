using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-004, offline half: every app process and starter that inlines platform-sod-guard carries
/// octopus/step-templates/sod-guard.ps1 verbatim, so the guard that the live tests prove is the one every app runs.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class SeparationOfDutiesTests
{
    /// <summary>Each inline copy of sod-guard.ps1 equals the step-template script.</summary>
    [Test]
    [Capability("CAP-OCT-004")]
    public void Should_InlineSodGuard_EveryAppProcess_EqualsTheTemplateScript()
    {
        var canonical = OctopusRepository.CanonicalLines("sod-guard");
        var copies = OctopusRepository.AppProcesses()
            .SelectMany(file => OctopusRepository.InlineCopies(OctopusRepository.Read(file), "sod-guard").Select(copy => (file, copy)))
            .ToArray();

        copies.ShouldNotBeEmpty("no app process inlines sod-guard.ps1");
        foreach (var (file, copy) in copies)
        {
            copy.ShouldBe(canonical, $"{file}: the inline copy of sod-guard.ps1 differs from octopus/step-templates/sod-guard.ps1");
        }
    }
}
