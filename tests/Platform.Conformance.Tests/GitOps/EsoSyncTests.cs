using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-007: ESO syncs vault secrets into the namespace. The test writes a random canary to secret
/// <c>conformance-canary</c> of the sandbox tdd vault <c>kv-sandbox-t-&lt;hash4&gt;</c> (the conformance principal is Key
/// Vault Secrets Officer there, terraform/apps/grants), creates an ExternalSecret in <c>sandbox-tdd</c> through store
/// <c>sandbox-tdd</c>, then writes a second canary and forces a sync (annotation <c>force-sync</c>). Each time the synced
/// Secret must hold the canary; only SHA-256 hashes are compared and printed.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class EsoSyncTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string VaultSecret = "conformance-canary";
    private const string SecretKey = "canary";

    [Test]
    [Capability("CAP-GIT-007")]
    [Category(Categories.NonProd)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Sync_CanaryWrittenToSandboxTddVault_ReachesTheNamespaceAndForcedResync()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        Settings.Check("the sandbox tdd vault name")
            .Setting($"{nameof(PlatformSettings.AzureSubscriptionId)} (or {EnvironmentVariableNames.AzureSubscriptionId})", Settings.AzureSubscriptionId)
            .ThrowIfMissing();
        var vault = GitOpsNames.VaultName(Settings.AzureSubscriptionId!, GitOpsNames.FixtureApp, "tdd");
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        await cluster.RemoveLeftoversAsync(CustomResourceKind.ExternalSecret, Namespace, cancellationToken);
        var name = Run.ResourceName("eso-canary");
        var first = $"canary-{Guid.NewGuid():N}";
        var second = $"canary-{Guid.NewGuid():N}";
        await Rest.SetVaultSecretAsync(vault, VaultSecret, first, Run.RunId, cancellationToken);
        Cleanup.Register($"delete ExternalSecret {Namespace}/{name}", token => cluster.DeleteAsync(CustomResourceKind.ExternalSecret, Namespace, name, token));
        var body = new JsonObject
        {
            ["apiVersion"] = "external-secrets.io/v1",
            ["kind"] = "ExternalSecret",
            ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = FixtureLabelsJson() },
            ["spec"] = new JsonObject
            {
                ["refreshInterval"] = "1h",
                ["secretStoreRef"] = new JsonObject { ["kind"] = "ClusterSecretStore", ["name"] = Namespace },
                ["target"] = new JsonObject { ["name"] = name, ["creationPolicy"] = "Owner" },
                ["data"] = new JsonArray(new JsonObject { ["secretKey"] = SecretKey, ["remoteRef"] = new JsonObject { ["key"] = VaultSecret } }),
            },
        };

        var created = await cluster.CreateAsync(CustomResourceKind.ExternalSecret, Namespace, body, dryRun: false, cancellationToken);
        created.Admitted.ShouldBeTrue(created.Message);
        var firstSynced = await SyncedHashAsync(cluster, name, Hash(first), "the first canary", cancellationToken);
        await Rest.SetVaultSecretAsync(vault, VaultSecret, second, Run.RunId, cancellationToken);
        await cluster.MergePatchAsync(
            CustomResourceKind.ExternalSecret,
            Namespace,
            name,
            new JsonObject { ["metadata"] = new JsonObject { ["annotations"] = new JsonObject { ["force-sync"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
            cancellationToken);
        var secondSynced = await SyncedHashAsync(cluster, name, Hash(second), "the second canary after the forced sync", cancellationToken);

        firstSynced.ShouldBe(Hash(first));
        secondSynced.ShouldBe(Hash(second));
    }

    private static Task<string?> SyncedHashAsync(GitOpsCluster cluster, string name, string expected, string what, CancellationToken cancellationToken) =>
        Poll.UntilAsync(
            async token => await cluster.SecretAsync(Namespace, name, token) is { Data: { } data } && data.TryGetValue(SecretKey, out var bytes) ? Hash(bytes) : null,
            hash => hash == expected,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromSeconds(5),
            $"Secret {Namespace}/{name} to hold {what} (SHA-256 {expected[..12]}…)",
            cancellationToken: cancellationToken);

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
