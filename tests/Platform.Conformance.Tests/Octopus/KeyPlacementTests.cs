using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>
/// CAP-OCT-013: the Space Manager key never reaches an app project. No project of an <c>app-*</c> group includes library
/// variable set Platform Automation, and none holds <c>Platform.OctopusApiKey</c>, <c>PlatformWake.OctopusApiKey</c> or
/// any other variable named like an Octopus API key, in the database or in Git. The offline half checks that the key is
/// scoped to the platform steps in octopus/terraform.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class KeyPlacementTests : OctopusCapabilityTestBase
{
    /// <summary>App projects neither include Platform Automation nor hold any Octopus API key variable.</summary>
    [Test]
    [Capability("CAP-OCT-013")]
    [CancelAfter(15 * 60 * 1000)]
    public async Task Should_ProjectVariables_AppProjects_HoldNoPlatformKey()
    {
        var rest = Rest("the key placement test");
        var automationSet = await rest.FindLibraryVariableSetAsync("Platform Automation", Token)
            ?? throw new InvalidOperationException("library variable set Platform Automation does not exist");
        var appGroups = (await rest.GetAllProjectGroupsAsync(Token))
            .Where(group => group.GetProperty("Name").GetString()?.StartsWith("app-", StringComparison.Ordinal) == true)
            .Select(group => group.GetProperty("Id").GetString())
            .ToHashSet();
        var appProjects = (await rest.GetAllProjectsAsync(Token))
            .Where(project => appGroups.Contains(project.GetProperty("ProjectGroupId").GetString()))
            .ToArray();

        appProjects.ShouldNotBeEmpty("no project in an app-* group");
        var findings = new List<string>();
        foreach (var project in appProjects)
        {
            var name = project.GetProperty("Name").GetString()!;
            var included = project.GetProperty("IncludedLibraryVariableSetIds").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (included.Contains(automationSet.GetProperty("Id").GetString()))
            {
                findings.Add($"{name} includes Platform Automation");
            }

            var stored = await Octopus.GetVariableSetAsync(project.GetProperty("VariableSetId").GetString()!, Token);
            findings.AddRange(KeyLike(stored).Select(variable => $"{name} holds {variable} in the database"));
            if (IsVersionControlled(project))
            {
                var inGit = await Octopus.GetProjectVariablesAsync(project.GetProperty("Id").GetString()!, OctopusRunbookRunRequest.MainBranch, Token);
                findings.AddRange(KeyLike(inGit).Select(variable => $"{name} holds {variable} in Git"));
            }
        }

        findings.ShouldBeEmpty();
    }

    private static IEnumerable<string> KeyLike(OctopusVariableSet variables) =>
        variables.Variables
            .Where(variable => variable.Name.Contains("OctopusApiKey", StringComparison.OrdinalIgnoreCase)
                || variable.Name.StartsWith("PlatformWake.", StringComparison.OrdinalIgnoreCase)
                || variable.Name.Equals("OCTOPUS_API_KEY", StringComparison.OrdinalIgnoreCase))
            .Select(variable => variable.Name)
            .Distinct();

    private static bool IsVersionControlled(JsonElement project) =>
        project.TryGetProperty("IsVersionControlled", out var value) && value.ValueKind == JsonValueKind.True;
}
