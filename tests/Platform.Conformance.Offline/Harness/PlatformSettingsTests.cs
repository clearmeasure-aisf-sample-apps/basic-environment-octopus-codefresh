using NUnit.Framework.Interfaces;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the settings loader: values from the file, secrets only from the environment, placeholders as missing.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class PlatformSettingsTests
{
    private const string SettingsJson = """
        {
          "$comment": "test settings",
          "OctopusUrl": "https://octopus.example.test",
          "OctopusSpaceId": "Spaces-7",
          "AzureSubscriptionId": "<AZURE_SUBSCRIPTION_ID>",
          "AzureTenantId": "<AZURE_TENANT_ID>",
          "RegistryLoginServer": "<acr-name>.azurecr.io",
          "GitHubOrg": "example-org",
          "EnvRepo": "example-org/env",
          "AppRepos": [ "example-org/app" ],
          "Tiers": {
            "nonprod": { "ResourceGroup": "rg-nonprod", "ClusterName": "aks-nonprod", "ResourceGroups": [ "rg-tdd", "rg-uat" ] }
          },
          "TimeLimits": { "PollIntervalSeconds": 5, "RunbookMinutes": 40 }
        }
        """;

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_FileWithoutSecrets_ReadsSettingsAndLeavesEverySecretMissing()
    {
        var settings = PlatformSettings.Parse(SettingsJson, "settings.json", new StubEnvironmentVariables());

        settings.OctopusUrl.ShouldBe("https://octopus.example.test");
        settings.OctopusSpaceId.ShouldBe("Spaces-7");
        settings.CodefreshUrl.ShouldBe(PlatformSettings.DefaultCodefreshUrl);
        settings.AppRepos.ShouldBe(["example-org/app"]);
        settings.Tier(PlatformTier.NonProd).ClusterName.ShouldBe("aks-nonprod");
        settings.Tier(PlatformTier.NonProd).ResourceGroups.ShouldBe(["rg-tdd", "rg-uat"]);
        settings.Tier(PlatformTier.Prod).ShouldBe(PlatformTierSettings.Empty);
        settings.TimeLimits.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        settings.TimeLimits.RunbookTimeout.ShouldBe(TimeSpan.FromMinutes(40));
        settings.TimeLimits.BuildTimeout.ShouldBe(TimeSpan.FromMinutes(60));
        settings.Secrets.OctopusApiKey.ShouldBeNull();
        settings.Secrets.CodefreshApiKey.ShouldBeNull();
        settings.Secrets.AzureClientSecret.ShouldBeNull();
        settings.Secrets.GitHubToken.ShouldBeNull();
        settings.TlsSystemTrust.ShouldBeFalse();
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_SecretsInEnvironment_ReadsThemButNeverPrintsThem()
    {
        var environment = new StubEnvironmentVariables(
            (EnvironmentVariableNames.OctopusApiKey, "<stub-octopus-api-key>"),
            (EnvironmentVariableNames.CodefreshApiKey, "   "),
            (EnvironmentVariableNames.GitHubToken, "<stub-github-token>"));

        var settings = PlatformSettings.Parse(SettingsJson, "settings.json", environment);

        settings.Secrets.OctopusApiKey.ShouldBe("<stub-octopus-api-key>");
        settings.Secrets.CodefreshApiKey.ShouldBeNull();
        settings.Secrets.ToString().ShouldBe("PlatformSecrets { OCTOPUS_API_KEY = set, CODEFRESH_API_KEY = missing, AZURE_CLIENT_ID = missing, AZURE_CLIENT_SECRET = missing, GITHUB_TOKEN = set }");
        settings.Secrets.ToString().ShouldNotContain("stub-");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_AzureIdsInEnvironment_OverrideThePlaceholdersOfTheFile()
    {
        var environment = new StubEnvironmentVariables(
            (EnvironmentVariableNames.AzureSubscriptionId, "00000000-0000-0000-0000-000000000001"),
            (EnvironmentVariableNames.AzureTenantId, "00000000-0000-0000-0000-000000000002"));

        var settings = PlatformSettings.Parse(SettingsJson, "settings.json", environment);

        settings.AzureSubscriptionId.ShouldBe("00000000-0000-0000-0000-000000000001");
        settings.AzureTenantId.ShouldBe("00000000-0000-0000-0000-000000000002");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_TlsSystemTrustVariableIsTrue_EnablesSystemTrust()
    {
        var settings = PlatformSettings.Parse(SettingsJson, "settings.json", new StubEnvironmentVariables((EnvironmentVariableNames.TlsSystemTrust, "TRUE")));

        settings.TlsSystemTrust.ShouldBeTrue();
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_FileHoldsSecretLikeProperty_ThrowsNamingItAndTheSecretVariables()
    {
        const string json = """
            {
              "OctopusUrl": "https://octopus.example.test",
              "OctopusApiKey": "<not-a-real-key>"
            }
            """;

        var exception = Should.Throw<PlatformSettingsException>(() => PlatformSettings.Parse(json, "settings.json", new StubEnvironmentVariables()));

        exception.Message.ShouldStartWith("settings.json:3: invalid settings file");
        exception.Message.ShouldContain("Property 'OctopusApiKey' looks like a secret: secrets never go in the settings file; set OCTOPUS_API_KEY, CODEFRESH_API_KEY, AZURE_CLIENT_SECRET, GITHUB_TOKEN in the environment instead.");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_UnknownTier_ThrowsNamingTheAllowedTiers()
    {
        const string json = """{ "Tiers": { "staging": { "ClusterName": "aks-staging" } } }""";

        var exception = Should.Throw<PlatformSettingsException>(() => PlatformSettings.Parse(json, "settings.json", new StubEnvironmentVariables()));

        exception.Message.ShouldBe("settings.json: unknown tier 'staging' under Tiers; use build, nonprod or prod.");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_HttpOctopusUrl_ThrowsBecauseOnlyHttpsIsAllowed()
    {
        const string json = """{ "OctopusUrl": "http://octopus.example.test" }""";

        var exception = Should.Throw<PlatformSettingsException>(() => PlatformSettings.Parse(json, "settings.json", new StubEnvironmentVariables()));

        exception.Message.ShouldContain("OctopusUrl must be an absolute https URL");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenParse_NonPositiveTimeLimit_ThrowsNamingTheLimit()
    {
        const string json = """{ "TimeLimits": { "WakeMinutes": 0 } }""";

        var exception = Should.Throw<PlatformSettingsException>(() => PlatformSettings.Parse(json, "settings.json", new StubEnvironmentVariables()));

        exception.Message.ShouldBe("settings.json: TimeLimits.WakeMinutes must be positive (was 0).");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenLoad_SettingsFileVariableNamesMissingFile_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var exception = Should.Throw<PlatformSettingsException>(() => PlatformSettings.Load(new StubEnvironmentVariables((EnvironmentVariableNames.SettingsFile, missing))));

        exception.Message.ShouldBe($"PLATFORM_SETTINGS_FILE names {missing}, which does not exist.");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenLoad_NoRepositoryRootAndNoFile_TreatsEverySettingAsMissing()
    {
        var settings = PlatformSettings.Load(new StubEnvironmentVariables(), Path.GetTempPath());

        settings.SourceFile.ShouldBeNull();
        settings.SettingsSource.ShouldContain("no settings file");
        settings.OctopusUrl.ShouldBeNull();
        settings.CodefreshUrl.ShouldBe(PlatformSettings.DefaultCodefreshUrl);
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenLoad_CommittedSettingsFile_ParsesAndHoldsPlaceholdersInsteadOfEnvironmentValues()
    {
        var root = RepositoryRoot.Find(TestContext.CurrentContext.TestDirectory);

        var settings = PlatformSettings.Load(new StubEnvironmentVariables(), root);

        settings.SourceFile.ShouldBe(Path.Combine(root, "tests", PlatformSettings.DefaultFileName));
        PlatformSettings.IsPlaceholder(settings.OctopusUrl).ShouldBeTrue();
        PlatformSettings.IsPlaceholder(settings.OctopusSpaceId).ShouldBeTrue();
        PlatformSettings.IsPlaceholder(settings.AzureSubscriptionId).ShouldBeTrue();
        PlatformSettings.IsPlaceholder(settings.RegistryLoginServer).ShouldBeTrue();
        settings.Tier(PlatformTier.NonProd).ClusterName.ShouldBe("aks-platform-nonprod");
        settings.Tier(PlatformTier.Prod).ClusterName.ShouldBe("aks-platform-prod");
    }

    [TestCase(null, true)]
    [TestCase("", true)]
    [TestCase("   ", true)]
    [TestCase("<OCTOPUS_URL>", true)]
    [TestCase("<acr-name>.azurecr.io", true)]
    [TestCase("https://example.octopus.app", false)]
    [TestCase("Spaces-1", false)]
    [Capability("CAP-HARNESS-006")]
    public void WhenIsMissing_Value_TreatsBlankAndPlaceholdersAsMissing(string? value, bool expected)
    {
        var missing = PlatformSettings.IsMissing(value);

        missing.ShouldBe(expected);
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenThrowIfMissing_SettingAndSecretMissing_ThrowsInconclusiveNamingBoth()
    {
        var check = new PrerequisiteCheck("the Octopus API", "tests/platform.settings.json")
            .Setting("OctopusUrl", "<OCTOPUS_URL>")
            .Setting("OctopusSpaceId", "Spaces-1")
            .Secret(EnvironmentVariableNames.OctopusApiKey, null);

        var exception = Should.Throw<PlatformPrerequisiteException>(check.ThrowIfMissing);

        exception.ResultState.ShouldBe(ResultState.Inconclusive);
        exception.ShouldBeAssignableTo<ResultStateException>();
        check.Missing.Count.ShouldBe(2);
        exception.Message.ShouldBe(
            "Prerequisites missing for the Octopus API: setting OctopusUrl in tests/platform.settings.json is still the placeholder '<OCTOPUS_URL>' (the provisioning step fills it in); "
            + "environment variable OCTOPUS_API_KEY is not set (secrets come only from the environment). "
            + "A live test without its prerequisites is Inconclusive, never passed or failed; see tests/README.md, section Running the live tests.");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenThrowIfMissing_EverythingPresent_DoesNotThrow()
    {
        var check = new PrerequisiteCheck("the GitHub API", "settings.json").Secret(EnvironmentVariableNames.GitHubToken, "<stub-github-token>");

        Should.NotThrow(check.ThrowIfMissing);

        check.IsSatisfied.ShouldBeTrue();
    }
}
