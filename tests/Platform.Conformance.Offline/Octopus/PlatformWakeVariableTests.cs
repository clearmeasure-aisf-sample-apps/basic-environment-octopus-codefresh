using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-014: platform-wake reads only <c>PlatformWake.*</c> and system variables. A Deploy a Release step passes the
/// parent deployment's variables to the child, and passed variables win over the child's own (E53), so platform-wake must
/// not read a name an app could define, and no app may define or read <c>PlatformWake.*</c>.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class PlatformWakeVariableTests
{
    /// <summary>Every variable platform-wake reads is <c>PlatformWake.*</c> or <c>Octopus.*</c>, and it defines none.</summary>
    [Test]
    [Capability("CAP-OCT-014")]
    public void Should_PlatformWakeProcess_ReadsOnlyNamespacedAndSystemVariables()
    {
        var process = OctopusRepository.WithoutComments(OctopusRepository.Read(".octopus/platform-wake/deployment_process.ocl"));
        var variables = OctopusRepository.WithoutComments(OctopusRepository.Read(".octopus/platform-wake/variables.ocl"));

        var reads = VariableReads(process);

        reads.ShouldNotBeEmpty();
        reads.ShouldAllBe(name => name.StartsWith("PlatformWake.", StringComparison.Ordinal) || name.StartsWith("Octopus.", StringComparison.Ordinal));
        reads.ShouldContain("PlatformWake.OctopusApiKey");
        variables.ShouldNotContain("variable \"");
    }

    /// <summary>No app process, runbook, variable file or starter defines or reads a <c>PlatformWake.*</c> variable.</summary>
    [Test]
    [Capability("CAP-OCT-014")]
    public void Should_AppConfiguration_PlatformWakeVariables_AreRejected()
    {
        var offenders = OctopusRepository.OclFiles(".octopus/apps").Concat(OctopusRepository.OclFiles("octopus/templates"))
            .Where(file => MentionsPlatformWake(OctopusRepository.Read(file)))
            .ToArray();

        MentionsPlatformWake("variable \"PlatformWake.Target\" {\n    value \"prod\" {}\n}\n").ShouldBeTrue("the check must reject a PlatformWake.* variable");
        MentionsPlatformWake("Octopus.Action.Script.ScriptBody = \"echo #{PlatformWake.OctopusApiKey}\"\n").ShouldBeTrue("the check must reject a PlatformWake.* read");
        offenders.ShouldBeEmpty();
    }

    private static IReadOnlyList<string> VariableReads(string ocl) =>
        GetVariable().Matches(ocl).Select(match => match.Groups["name"].Value)
            .Concat(Substitution().Matches(ocl).Select(match => match.Groups["name"].Value))
            .Distinct()
            .ToArray();

    private static bool MentionsPlatformWake(string ocl) =>
        OctopusRepository.WithoutComments(ocl).Contains("PlatformWake.", StringComparison.Ordinal);

    [GeneratedRegex(@"get_octopusvariable\s+""(?<name>[^""]+)""")]
    private static partial Regex GetVariable();

    [GeneratedRegex(@"#\{(?!/|if |unless |each |else)(?<name>[A-Za-z][A-Za-z0-9_.\[\]-]*)")]
    private static partial Regex Substitution();
}
