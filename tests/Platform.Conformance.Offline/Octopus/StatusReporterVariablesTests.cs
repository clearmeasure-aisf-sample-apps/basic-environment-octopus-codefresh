using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-017 (R16, ADR-IR27): the workorders project carries the App ID and installation ID of the statuses-only GitHub
/// App <c>aisf-octopus-status-reporter</c> (neither is a secret), keeps <c>GitHub.StatusEnabled</c> at <c>False</c> until the
/// owner has stored the private key, and never holds the key itself in a repository file.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class StatusReporterVariablesTests
{
    private const string VariablesFile = ".octopus/apps/workorders/workorders/variables.ocl";

    private static string ValueOf(string variables, string name)
    {
        var match = Regex.Match(variables, "variable \"" + Regex.Escape(name) + "\" \\{\\s*value \"([^\"]*)\"");
        match.Success.ShouldBeTrue($"{VariablesFile} declares variable {name}");
        return match.Groups[1].Value;
    }

    /// <summary>The App and installation IDs are the real ones, not the 0 placeholder.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void Should_ReadWorkorders_StatusAppIds_AreSetToTheRegisteredApp()
    {
        var variables = OctopusRepository.Read(VariablesFile);

        ValueOf(variables, "GitHub.StatusAppId").ShouldBe("5130161");
        ValueOf(variables, "GitHub.StatusAppInstallationId").ShouldBe("166359160");
    }

    /// <summary>The flag stays False and no private key value sits in the variables file until the owner enables it.</summary>
    [Test]
    [Capability("CAP-OCT-017")]
    public void Should_ReadWorkorders_StatusReporting_StaysDisabledAndHoldsNoPrivateKey()
    {
        var variables = OctopusRepository.Read(VariablesFile);

        ValueOf(variables, "GitHub.StatusEnabled").ShouldBe("False");
        variables.ShouldNotContain("BEGIN RSA PRIVATE KEY");
        variables.ShouldNotContain("BEGIN PRIVATE KEY");
        variables.ShouldNotContain("GitHub.StatusAppPrivateKey");
    }
}
