using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-008: an app deployment wakes its cluster in step <c>wake-environment</c>, which the app may switch off with
/// <c>Wake.Skip</c> = <c>true</c>. That is safe only while automatic sleeping is paused (<c>Sleep.Enabled</c> =
/// <c>false</c>): with sleeping on, nothing else wakes the prod tier for a deployment, so no release reaches prod
/// (defect #81, 2026-09-30 to 2026-10-06).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class WakeSkipGuardTests
{
    private const string InfrastructureVariables = ".octopus/platform-infrastructure/variables.ocl";

    /// <summary>While <c>Sleep.Enabled</c> is <c>true</c>, no app project sets <c>Wake.Skip</c> to <c>true</c>.</summary>
    [Test]
    [Capability("CAP-OCT-008")]
    public void Should_ReadAppVariables_SleepingEnabled_NoAppSkipsTheWake()
    {
        var sleepEnabled = ValuesOf(OctopusRepository.WithoutComments(OctopusRepository.Read(InfrastructureVariables)), "Sleep.Enabled");
        sleepEnabled.ShouldNotBeEmpty($"{InfrastructureVariables} declares variable Sleep.Enabled");

        var skipping = OctopusRepository.OclFiles(".octopus/apps")
            .Where(file => Path.GetFileName(file) == "variables.ocl")
            .Where(file => ValuesOf(OctopusRepository.WithoutComments(OctopusRepository.Read(file)), "Wake.Skip").Contains("true"))
            .ToArray();

        if (sleepEnabled.Contains("true"))
        {
            skipping.ShouldBeEmpty("Wake.Skip is true while Sleep.Enabled is true: these deployments never wake the prod tier");
        }
    }

    /// <summary>Every value of one variable in an OCL variables file (a variable may carry several scoped values).</summary>
    private static string[] ValuesOf(string variables, string name)
    {
        var block = Regex.Match(variables, "variable \"" + Regex.Escape(name) + "\" \\{(.*?)\n\\}", RegexOptions.Singleline);
        return block.Success
            ? [.. ValuePattern().Matches(block.Groups[1].Value).Select(match => match.Groups[1].Value)]
            : [];
    }

    [GeneratedRegex("value \"([^\"]*)\"")]
    private static partial Regex ValuePattern();
}
