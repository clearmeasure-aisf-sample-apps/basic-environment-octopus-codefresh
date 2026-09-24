using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Offline.Azure;

/// <summary>
/// CAP-AZ-007 (offline half): prod has no destroy runbook. Every runbook action of <c>platform-infrastructure</c>
/// (<c>.octopus/platform-infrastructure/runbooks/*.ocl</c>) that destroys (a Terraform destroy or plan-destroy step, or a
/// script that runs <c>terraform destroy</c>, <c>az aks delete</c> or <c>az group delete</c>) is scoped to
/// <c>infra-nonprod</c> only, and no step of <c>env-destroy</c> runs in <c>infra-prod</c>. The live half is
/// Platform.Conformance.Tests.Azure.TierRebuildTests.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class TierRebuildTests
{
    [Test]
    [Capability("CAP-AZ-007")]
    [Category(Categories.Destructive)]
    [Category(Categories.NonProd)]
    public void Should_ReadEnvDestroyRunbook_InfraProd_HaveNoDestroy()
    {
        var folder = Path.Combine(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance), ".octopus", "platform-infrastructure", "runbooks");

        var actions = Directory.EnumerateFiles(folder, "*.ocl").Order(StringComparer.Ordinal).SelectMany(ReadActions).ToArray();
        var destroying = actions.Where(action => action.Destroys).ToArray();
        var envDestroy = actions.Where(action => action.Runbook == "env-destroy").ToArray();

        envDestroy.ShouldNotBeEmpty($"runbook env-destroy is missing from {folder}");
        destroying.ShouldNotBeEmpty("env-destroy must hold the Terraform destroy step");
        destroying.Where(action => !action.Environments.SequenceEqual(["infra-nonprod"]))
            .Select(action => $"{action.Runbook}/{action.Step} ({action.Type}): environments [{string.Join(", ", action.Environments)}]")
            .ShouldBeEmpty("every destroying action must be scoped to infra-nonprod only");
        envDestroy.Where(action => action.Environments.Count == 0 || action.Environments.Contains("infra-prod"))
            .Select(action => $"{action.Step}: environments [{string.Join(", ", action.Environments)}]")
            .ShouldBeEmpty("no step of env-destroy may run in infra-prod (an action without environments runs everywhere)");
    }

    private static IEnumerable<RunbookAction> ReadActions(string path)
    {
        var text = File.ReadAllText(path);
        var runbook = RunbookName().Match(text) is { Success: true } name ? name.Groups["name"].Value : Path.GetFileNameWithoutExtension(path);
        foreach (var step in StepStart().Split(text).Skip(1))
        {
            var stepName = StepName().Match(step) is { Success: true } title ? title.Groups["name"].Value : "?";
            foreach (var action in ActionStart().Split(step).Skip(1))
            {
                var header = action.Split("properties = {", 2)[0];
                var type = ActionType().Match(header) is { Success: true } kind ? kind.Groups["type"].Value : string.Empty;
                var environments = EnvironmentList().Match(header) is { Success: true } list
                    ? Quoted().Matches(list.Groups["list"].Value).Select(item => item.Groups["value"].Value).ToArray()
                    : [];
                var destroys = type is "Octopus.TerraformDestroy" or "Octopus.TerraformPlanDestroy" || DestroyCommand().IsMatch(action);
                yield return new RunbookAction(runbook, stepName, type, environments, destroys);
            }
        }
    }

    [GeneratedRegex("^name = \"(?<name>[^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex RunbookName();

    [GeneratedRegex("^\\s*step \"[^\"]*\" \\{", RegexOptions.Multiline)]
    private static partial Regex StepStart();

    [GeneratedRegex("^\\s*name = \"(?<name>[^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex StepName();

    [GeneratedRegex("^\\s*action \\{", RegexOptions.Multiline)]
    private static partial Regex ActionStart();

    [GeneratedRegex("action_type = \"(?<type>[^\"]+)\"")]
    private static partial Regex ActionType();

    [GeneratedRegex("environments = \\[(?<list>[^\\]]*)\\]")]
    private static partial Regex EnvironmentList();

    [GeneratedRegex("\"(?<value>[^\"]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"terraform\s+(?:-chdir=\S+\s+)?(?:destroy|apply\s+(?:\S+\s+)*-destroy)|az\s+aks\s+delete|az\s+group\s+delete")]
    private static partial Regex DestroyCommand();

    private sealed record RunbookAction(string Runbook, string Step, string Type, IReadOnlyList<string> Environments, bool Destroys);
}
