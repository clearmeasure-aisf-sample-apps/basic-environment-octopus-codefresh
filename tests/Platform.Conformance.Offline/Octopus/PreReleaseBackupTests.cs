using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-015, offline half: every app process with a pre-release backup inlines octopus/step-templates/db-backup.sh
/// verbatim, and runs the backup, in prod, before the step that writes the pins.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PreReleaseBackupTests
{
    /// <summary>Each inline copy of db-backup.sh equals the step-template script.</summary>
    [Test]
    [Capability("CAP-OCT-015")]
    public void Should_InlineDbBackup_EveryAppProcess_EqualsTheTemplateScript()
    {
        var canonical = OctopusRepository.CanonicalLines("db-backup");
        var copies = OctopusRepository.AppProcesses()
            .SelectMany(file => OctopusRepository.InlineCopies(OctopusRepository.Read(file), "db-backup").Select(copy => (file, copy)))
            .ToArray();

        copies.ShouldNotBeEmpty("no app process inlines db-backup.sh");
        foreach (var (file, copy) in copies)
        {
            copy.ShouldBe(canonical, $"{file}: the inline copy of db-backup.sh differs from octopus/step-templates/db-backup.sh");
        }
    }

    /// <summary>In every process with step pre-release-backup, that step is scoped to prod and precedes the pin step.</summary>
    [Test]
    [Capability("CAP-OCT-015")]
    public void Should_PreReleaseBackup_EveryAppProcess_RunsInProdBeforeThePinStep()
    {
        var processes = OctopusRepository.AppProcesses()
            .Select(file => (file, steps: OctopusRepository.Steps(OctopusRepository.Read(file))))
            .Where(process => process.steps.Any(step => step.Slug == "pre-release-backup"))
            .ToArray();

        processes.ShouldNotBeEmpty("no app process has step pre-release-backup");
        foreach (var (file, steps) in processes)
        {
            var slugs = steps.Select(step => step.Slug).ToList();
            var backup = slugs.IndexOf("pre-release-backup");
            var pin = slugs.IndexOf("update-argo-cd-image-tags");
            pin.ShouldBeGreaterThan(backup, $"{file}: the pin step must follow pre-release-backup");
            steps[backup].Text.ShouldContain("environments = [\"prod\"]", Case.Sensitive, $"{file}: pre-release-backup runs in prod only");
        }
    }
}
