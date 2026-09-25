using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-013, offline half: the Space Manager key is scoped to platform steps. In octopus/terraform,
/// <c>PlatformWake.OctopusApiKey</c> lives only in library variable set Platform Automation, which only platform-wake
/// includes; the step-scoped <c>Platform.OctopusApiKey</c> of platform-infrastructure names exactly the runbooks and steps
/// that read it (in PowerShell <c>$OctopusParameters['Platform.OctopusApiKey']</c>, in Bash
/// <c>get_octopusvariable "Platform.OctopusApiKey"</c>); and no app process or starter mentions an Octopus API key.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class KeyPlacementTests
{
    /// <summary>The key reaches only the platform steps that call the Octopus REST API.</summary>
    [Test]
    [Capability("CAP-OCT-013")]
    public void Should_TerraformAndOcl_PlatformKey_ScopedToPlatformSteps()
    {
        var sets = OctopusRepository.Read("octopus/terraform/library-variable-sets.tf");
        var projects = OctopusRepository.Read("octopus/terraform/projects.tf");
        var processes = OctopusRepository.TerraformList(sets, "infrastructure_key_processes");
        var actions = OctopusRepository.TerraformList(sets, "infrastructure_key_actions");
        var readers = OctopusRepository.OclFiles(".octopus/platform-infrastructure/runbooks")
            .SelectMany(file => OctopusRepository.Steps(OctopusRepository.Read(file))
                .Where(step => KeyRead().IsMatch(step.Text))
                .Select(step => (runbook: Path.GetFileNameWithoutExtension(file), step: step.Slug)))
            .ToArray();
        var appFiles = OctopusRepository.OclFiles(".octopus/apps").Concat(OctopusRepository.OclFiles("octopus/templates"));

        readers.ShouldNotBeEmpty();
        foreach (var (runbook, step) in readers)
        {
            processes.ShouldContain(runbook, $"runbook {runbook} reads Platform.OctopusApiKey but is not in infrastructure_key_processes");
            actions.ShouldContain(step, $"step {step} of {runbook} reads Platform.OctopusApiKey but is not in infrastructure_key_actions");
        }

        actions.ShouldBe(readers.Select(reader => reader.step).Distinct(), ignoreOrder: true, "infrastructure_key_actions names a step that never reads the key");
        Regex.Matches(sets, @"owner_id\s*=\s*octopusdeploy_library_variable_set\.platform_automation\.id").Count.ShouldBe(1, "only PlatformWake.OctopusApiKey belongs to Platform Automation");
        sets.ShouldContain("name            = \"PlatformWake.OctopusApiKey\"");
        Regex.Matches(projects, @"octopusdeploy_library_variable_set\.platform_automation\.id").Count.ShouldBe(2, "Platform Automation is included by platform-wake only (and checked by its precondition)");
        Regex.IsMatch(projects, @"resource ""octopusdeploy_project"" ""platform_wake"" \{[^}]*included_library_variable_sets\s*=\s*\[octopusdeploy_library_variable_set\.platform_automation\.id\]", RegexOptions.Singleline)
            .ShouldBeTrue("platform-wake includes Platform Automation");
        foreach (var file in appFiles)
        {
            OctopusRepository.Read(file).ShouldNotContain("OctopusApiKey", Case.Insensitive, $"{file} names an Octopus API key");
        }
    }

    [GeneratedRegex(@"get_octopusvariable ""Platform\.OctopusApiKey""|\$OctopusParameters\[(?:'Platform\.OctopusApiKey'|""Platform\.OctopusApiKey"")\]")]
    private static partial Regex KeyRead();
}
