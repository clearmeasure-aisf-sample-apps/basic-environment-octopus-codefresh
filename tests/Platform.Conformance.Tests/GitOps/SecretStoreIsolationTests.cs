using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-004: an app cannot read another app's secrets. An ExternalSecret in <c>sandbox-tdd</c> that names
/// ClusterSecretStore <c>workorders-tdd</c> is refused by the store's namespace conditions (decision 6): its Ready
/// condition turns False with ESO's "not allowed" message and no Secret appears. The key it asks for,
/// <c>appinsights-connection-string</c>, exists in every app vault, so a missing fence would sync it.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SecretStoreIsolationTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string ForeignStore = "workorders-tdd";

    [Test]
    [Capability("CAP-GIT-004")]
    [Category(Categories.NonProd)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Sync_ExternalSecretNamingForeignStore_IsRefused()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var kubernetes = await KubernetesAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        await cluster.RemoveLeftoversAsync(CustomResourceKind.ExternalSecret, Namespace, cancellationToken);
        var name = Run.ResourceName("foreign-store");
        var body = new JsonObject
        {
            ["apiVersion"] = "external-secrets.io/v1",
            ["kind"] = "ExternalSecret",
            ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = FixtureLabelsJson() },
            ["spec"] = new JsonObject
            {
                ["refreshInterval"] = "1m",
                ["secretStoreRef"] = new JsonObject { ["kind"] = "ClusterSecretStore", ["name"] = ForeignStore },
                ["target"] = new JsonObject { ["name"] = name, ["creationPolicy"] = "Owner" },
                ["data"] = new JsonArray(new JsonObject { ["secretKey"] = "value", ["remoteRef"] = new JsonObject { ["key"] = "appinsights-connection-string" } }),
            },
        };
        Cleanup.Register($"delete ExternalSecret {Namespace}/{name}", token => cluster.DeleteAsync(CustomResourceKind.ExternalSecret, Namespace, name, token));

        var created = await cluster.CreateAsync(CustomResourceKind.ExternalSecret, Namespace, body, dryRun: false, cancellationToken);

        created.Admitted.ShouldBeTrue($"the ExternalSecret itself is an allowed kind in {Namespace}: {created.Message}");
        var status = await Poll.UntilAsync(
            async token => (await kubernetes.ListExternalSecretsAsync(Namespace, token)).FirstOrDefault(secret => secret.Name == name),
            secret => secret is { Ready: true } || !string.IsNullOrEmpty(secret?.Message),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromSeconds(5),
            $"ESO to decide on ExternalSecret {Namespace}/{name} (store {ForeignStore})",
            cancellationToken: cancellationToken);
        status!.Ready.ShouldBeFalse($"store {ForeignStore} served {Namespace}: {status.Message}");
        status.Message.ShouldNotBeNull();
        status.Message.ShouldContain("not allowed", Case.Insensitive, $"[VERIFY ESO wording] refusal: {status.Message}");
        (await cluster.SecretAsync(Namespace, name, cancellationToken)).ShouldBeNull($"Secret {Namespace}/{name} exists although store {ForeignStore} refused the namespace");
    }
}
