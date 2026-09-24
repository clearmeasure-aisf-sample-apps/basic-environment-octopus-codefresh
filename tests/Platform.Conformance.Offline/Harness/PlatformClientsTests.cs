using Azure.Identity;
using NUnit.Framework.Interfaces;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves that a client whose secret or setting is missing is never created and the test becomes Inconclusive.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class PlatformClientsTests
{
    private const string FilledSettings = """
        {
          "OctopusUrl": "https://octopus.example.test",
          "OctopusSpaceId": "Spaces-1",
          "AzureSubscriptionId": "00000000-0000-0000-0000-000000000001",
          "Tiers": { "nonprod": { "ResourceGroup": "rg-platform-nonprod-aks" } }
        }
        """;

    private const string PlaceholderSettings = """
        { "OctopusUrl": "<OCTOPUS_URL>", "OctopusSpaceId": "<octopus-space-id>", "AzureSubscriptionId": "<AZURE_SUBSCRIPTION_ID>" }
        """;

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenOctopus_ApiKeyMissing_ThrowsInconclusiveNamingTheVariable()
    {
        using var clients = new PlatformClients(Settings(FilledSettings));

        var exception = Should.Throw<PlatformPrerequisiteException>(() => clients.Octopus);

        exception.ResultState.ShouldBe(ResultState.Inconclusive);
        exception.Message.ShouldContain("environment variable OCTOPUS_API_KEY is not set");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenOctopus_SettingsArePlaceholders_ThrowsInconclusiveNamingEverySetting()
    {
        using var clients = new PlatformClients(Settings(PlaceholderSettings, (EnvironmentVariableNames.OctopusApiKey, "<stub-octopus-api-key>")));

        var exception = Should.Throw<PlatformPrerequisiteException>(() => clients.Octopus);

        exception.Message.ShouldContain("setting OctopusUrl in settings.json is still the placeholder '<OCTOPUS_URL>'");
        exception.Message.ShouldContain("setting OctopusSpaceId in settings.json is still the placeholder '<octopus-space-id>'");
        exception.Message.ShouldNotContain("<stub-octopus-api-key>");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenCodefresh_ApiKeyMissing_ThrowsInconclusive()
    {
        using var clients = new PlatformClients(Settings(FilledSettings));

        var exception = Should.Throw<PlatformPrerequisiteException>(() => clients.Codefresh);

        exception.Message.ShouldContain("environment variable CODEFRESH_API_KEY is not set");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenAzure_SubscriptionIsPlaceholder_ThrowsInconclusiveNamingSettingAndVariable()
    {
        using var clients = new PlatformClients(Settings(PlaceholderSettings));

        var exception = Should.Throw<PlatformPrerequisiteException>(() => clients.Azure);

        exception.Message.ShouldContain("setting AzureSubscriptionId (or AZURE_SUBSCRIPTION_ID) in settings.json is still the placeholder '<AZURE_SUBSCRIPTION_ID>'");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenGitHub_TokenMissing_ThrowsInconclusive()
    {
        using var clients = new PlatformClients(Settings(FilledSettings));

        var exception = Should.Throw<PlatformPrerequisiteException>(() => clients.GitHub);

        exception.Message.ShouldContain("environment variable GITHUB_TOKEN is not set");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public async Task WhenKubernetesAsync_TierClusterNameMissing_ThrowsInconclusiveNamingTheTierSetting()
    {
        using var clients = new PlatformClients(Settings(FilledSettings));

        var exception = await Should.ThrowAsync<PlatformPrerequisiteException>(() => clients.KubernetesAsync(PlatformTier.NonProd));

        exception.Message.ShouldContain("Prerequisites missing for the Kubernetes API of the nonprod cluster: setting Tiers.nonprod.ClusterName is not set in settings.json");
        exception.Message.ShouldNotContain("Tiers.nonprod.ResourceGroup");
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenOctopus_AllPrerequisitesSet_CreatesTheClientWithoutCallingTheServer()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}"));
        using var clients = new PlatformClients(Settings(FilledSettings, (EnvironmentVariableNames.OctopusApiKey, "<stub-octopus-api-key>")), handlerFactory: () => handler);

        var octopus = clients.Octopus;

        octopus.ShouldBeOfType<OctopusApi>();
        clients.Octopus.ShouldBeSameAs(octopus);
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public async Task WhenGetSubscriptionAsync_NoAzureCredentialAvailable_ThrowsInconclusiveNamingTheVariables()
    {
        using var azure = new AzureApi(StubTokenCredential.Unavailable(), new AzureApiOptions { SubscriptionId = "00000000-0000-0000-0000-000000000001" });

        var exception = await Should.ThrowAsync<PlatformPrerequisiteException>(() => azure.GetSubscriptionAsync());

        exception.Message.ShouldStartWith("No Azure credential is available: set AZURE_CLIENT_ID, AZURE_CLIENT_SECRET and AZURE_TENANT_ID for a service principal");
        exception.InnerException.ShouldBeOfType<CredentialUnavailableException>();
    }

    [Test]
    [Capability("CAP-HARNESS-006")]
    public void WhenCreate_ServicePrincipalVariablesSet_UsesClientSecretCredentialElseDefaultCredential()
    {
        var servicePrincipal = Settings(
            FilledSettings,
            (EnvironmentVariableNames.AzureClientId, "00000000-0000-0000-0000-000000000003"),
            (EnvironmentVariableNames.AzureClientSecret, "<stub-client-secret>"),
            (EnvironmentVariableNames.AzureTenantId, "00000000-0000-0000-0000-000000000002"));
        var secretOnly = Settings(FilledSettings, (EnvironmentVariableNames.AzureClientSecret, "<stub-client-secret>"));

        var withServicePrincipal = AzureCredentialFactory.Create(servicePrincipal);
        var withoutServicePrincipal = AzureCredentialFactory.Create(secretOnly);

        withServicePrincipal.ShouldBeOfType<ClientSecretCredential>();
        withoutServicePrincipal.ShouldBeOfType<DefaultAzureCredential>();
    }

    private static PlatformSettings Settings(string json, params (string Name, string? Value)[] variables) =>
        PlatformSettings.Parse(json, "settings.json", new StubEnvironmentVariables(variables));
}
