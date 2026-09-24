using System.Net;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the registry data-plane flow: Entra token, <c>/oauth2/exchange</c>, <c>/oauth2/token</c>, then the tag read.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class AzureRegistryTests
{
    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetRegistryTagAsync_ExchangesTheEntraTokenThenReadsTheTagWithARepositoryScopedToken()
    {
        var credential = StubTokenCredential.Returning("<stub-entra-token>");
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            "/oauth2/exchange" => StubHttpMessageHandler.Json("""{ "refresh_token": "<stub-acr-refresh-token>" }"""),
            "/oauth2/token" => StubHttpMessageHandler.Json("""{ "access_token": "<stub-acr-access-token>" }"""),
            "/acr/v1/workorders/ui-server/_tags/2.5.120" => StubHttpMessageHandler.Json("""{ "registry": "registry.example.test", "imageName": "workorders/ui-server", "tag": { "name": "2.5.120", "digest": "sha256:abc", "createdTime": "2026-09-24T01:02:03Z", "lastUpdateTime": "2026-09-24T01:02:03Z", "signed": false, "changeableAttributes": { "deleteEnabled": false, "writeEnabled": false, "readEnabled": true, "listEnabled": true } } }"""),
            _ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound),
        });
        using var azure = new AzureApi(credential, new AzureApiOptions { SubscriptionId = "00000000-0000-0000-0000-000000000001", TenantId = "00000000-0000-0000-0000-000000000002", RegistryLoginServer = "registry.example.test" }, handler);

        var tag = await azure.GetRegistryTagAsync("workorders/ui-server", "2.5.120");

        tag.Digest.ShouldBe("sha256:abc");
        tag.Attributes.ShouldBe(new AcrChangeableAttributes(DeleteEnabled: false, WriteEnabled: false, ReadEnabled: true, ListEnabled: true));
        credential.RequestedScopes.ShouldBe([AzureApi.RegistryScope]);
        handler.Requests.Select(request => $"{request.Method} {request.PathAndQuery}").ShouldBe(
        [
            "POST /oauth2/exchange",
            "POST /oauth2/token",
            "GET /acr/v1/workorders/ui-server/_tags/2.5.120",
        ]);
        handler.Requests[0].Body.ShouldBe("grant_type=access_token&service=registry.example.test&access_token=%3Cstub-entra-token%3E&tenant=00000000-0000-0000-0000-000000000002");
        handler.Requests[1].Body.ShouldBe("grant_type=refresh_token&service=registry.example.test&scope=repository%3Aworkorders%2Fui-server%3Ametadata_read&refresh_token=%3Cstub-acr-refresh-token%3E");
        handler.Requests[2].Header("Authorization").ShouldBe("Bearer <stub-acr-access-token>");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetRegistryTagAsync_LoginServerIsPlaceholder_ThrowsInconclusive()
    {
        using var azure = new AzureApi(StubTokenCredential.Returning("<stub-entra-token>"), new AzureApiOptions { SubscriptionId = "00000000-0000-0000-0000-000000000001", RegistryLoginServer = "<acr-name>.azurecr.io" });

        var exception = await Should.ThrowAsync<PlatformPrerequisiteException>(() => azure.GetRegistryTagAsync("workorders/ui-server", "2.5.120"));

        exception.Message.ShouldContain("RegistryLoginServer");
    }
}
