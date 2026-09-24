using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-012, live half: the build cluster holds no role assignment outside its node resource group. Observed on the
/// ARM role assignments of its two identities (the control plane's system-assigned identity and the kubelet identity)
/// that the conformance identity can read: subscription-wide when it may, else at, above and below every platform group
/// of the settings file (it holds Reader on those only). AKS itself grants inside the node group only. The offline half
/// reads terraform/build and the runner values.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class BuildIdentityTests : CodefreshCapabilityTestBase
{
    /// <summary>Every role assignment of the cluster's identities is scoped inside its node resource group.</summary>
    [Test]
    [Capability("CAP-CF-012")]
    [Category(Categories.Build)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_ListRoleAssignmentsAsync_BuildClusterIdentities_HoldNoneOutsideTheNodeGroup()
    {
        var arm = RequireArm("the build identity test");
        var cluster = await arm.GetClusterAsync(Token);
        cluster.ShouldNotBeNull($"cluster {CodefreshPlatform.BuildCluster} does not exist in {CodefreshPlatform.BuildGroup}");
        var properties = JsonRead.Path(cluster.Value, "properties");
        var nodeGroup = JsonRead.Text(properties, "nodeResourceGroup") ?? CodefreshPlatform.BuildNodeGroup;
        var principals = new[]
        {
            JsonRead.Text(JsonRead.Path(cluster.Value, "identity"), "principalId"),
            JsonRead.Text(JsonRead.Path(properties, "identityProfile", "kubeletidentity"), "objectId"),
        }.OfType<string>().ToArray();
        var allowed = $"/subscriptions/{Settings.AzureSubscriptionId}/resourceGroups/{nodeGroup}";
        IReadOnlyList<string> readableGroups =
        [
            nodeGroup,
            CodefreshPlatform.BuildGroup,
            .. new[] { PlatformTier.Build, PlatformTier.NonProd, PlatformTier.Prod }.SelectMany(tier => Settings.Tier(tier).ResourceGroups),
        ];

        var outside = new List<string>();
        foreach (var principal in principals)
        {
            foreach (var assignment in await arm.ListRoleAssignmentsAsync(principal, readableGroups, Token))
            {
                var scope = JsonRead.Text(JsonRead.Path(assignment, "properties"), "scope") ?? string.Empty;
                if (!scope.Equals(allowed, StringComparison.OrdinalIgnoreCase) && !scope.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase))
                {
                    outside.Add($"{principal}: {JsonRead.Text(JsonRead.Path(assignment, "properties"), "roleDefinitionId")} at {scope}");
                }
            }
        }

        principals.Length.ShouldBe(2, $"{CodefreshPlatform.BuildCluster} reports its system-assigned and kubelet identities");
        outside.ShouldBeEmpty($"role assignments outside {nodeGroup}: {string.Join("; ", outside)}");
    }
}
