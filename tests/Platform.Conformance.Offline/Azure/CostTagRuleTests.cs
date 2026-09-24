using System.Text.RegularExpressions;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Tests.Azure;

namespace Platform.Conformance.Offline.Azure;

/// <summary>
/// CAP-AZ-013 (offline half): Azure's own smart-detection action group is the one exemption from the cost tags, the rules
/// Azure generates are not, and terraform/apps/tier deletes those rules when it creates a component.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class CostTagRuleTests
{
    private static readonly Dictionary<string, string> NoTags = new(StringComparer.Ordinal);

    [Test]
    [Capability("CAP-AZ-013")]
    public void Should_ReadAzureGenerated_SmartDetectionGroupAndGeneratedRule_ExemptOnlyTheGroup()
    {
        Governance.IsAzureGenerated("microsoft.insights/actiongroups", "Application Insights Smart Detection").ShouldBeTrue();
        Governance.IsAzureGenerated("Microsoft.Insights/actionGroups", "ag-platform-oncall-nonprod").ShouldBeFalse();
        Governance.IsAzureGenerated("microsoft.alertsmanagement/smartDetectorAlertRules", "Failure Anomalies - appi-sandbox-uat").ShouldBeFalse();
        Governance.MissingCostTags("rg-platform-nonprod-apps", "Failure Anomalies - appi-sandbox-uat", NoTags)
            .ShouldBe(["platform-tier", "platform-component"]);
    }

    [Test]
    [Capability("CAP-AZ-013")]
    public void Should_ReadAppsTierProvider_ApplicationInsights_DisableTheGeneratedRule()
    {
        var providers = Path.Combine(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance), "terraform", "apps", "tier", "providers.tf");

        var text = File.ReadAllText(providers);

        DisableGeneratedRule().IsMatch(text).ShouldBeTrue($"{providers}: features.application_insights must set disable_generated_rule = true");
    }

    [GeneratedRegex(@"application_insights\s*\{[^}]*?^\s*disable_generated_rule\s*=\s*true\s*$", RegexOptions.Multiline)]
    private static partial Regex DisableGeneratedRule();
}
