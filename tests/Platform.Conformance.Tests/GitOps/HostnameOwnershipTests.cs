using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-013: an app routes only its own host names. Server-side dry runs of HTTPRoutes in <c>sandbox-tdd</c> (admission
/// runs on a dry run, Q46): a route claiming <c>workorders-tdd.&lt;domain&gt;</c> and a wildcard route are refused by the
/// ValidatingPolicy <c>platform-app-hostnames</c> (gitops/platform/ingress, Deny in both tiers); a route naming its own
/// host is admitted. The policy compares first DNS labels only, so a stand-in domain serves until the tier's apps domain
/// is provisioned.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class HostnameOwnershipTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string StandInDomain = "apps.platform.invalid";
    private static readonly CustomResourceKind HttpRoutes = new("gateway.networking.k8s.io", "v1", "httproutes");

    [Test]
    [Capability("CAP-GIT-013")]
    [Category(Categories.NonProd)]
    [CancelAfter(5 * 60 * 1000)]
    public async Task Should_Admit_HttpRouteInSandboxTddClaimingForeignHost_IsRefused()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var domain = TenantValues(PlatformTier.NonProd).AppsDomain ?? StandInDomain;
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);

        var foreign = await cluster.CreateAsync(HttpRoutes, Namespace, Route("foreign-host", $"workorders-tdd.{domain}"), dryRun: true, cancellationToken);
        var wildcard = await cluster.CreateAsync(HttpRoutes, Namespace, Route("wildcard-host", $"*.{domain}"), dryRun: true, cancellationToken);
        var own = await cluster.CreateAsync(HttpRoutes, Namespace, Route("own-host", $"{Namespace}.{domain}"), dryRun: true, cancellationToken);

        Assert.Multiple(() =>
        {
            foreign.Admitted.ShouldBeFalse($"{Namespace} claimed workorders-tdd.{domain}");
            (foreign.Message ?? string.Empty).ShouldContain("first label", Case.Insensitive);
            wildcard.Admitted.ShouldBeFalse($"{Namespace} claimed *.{domain}");
            own.Admitted.ShouldBeTrue($"{Namespace} was refused its own host: {own.Message}");
        });
    }

    private JsonObject Route(string purpose, string host) => new()
    {
        ["apiVersion"] = "gateway.networking.k8s.io/v1",
        ["kind"] = "HTTPRoute",
        ["metadata"] = new JsonObject { ["name"] = Run.ResourceName(purpose), ["labels"] = FixtureLabelsJson() },
        ["spec"] = new JsonObject
        {
            ["parentRefs"] = new JsonArray(new JsonObject
            {
                ["group"] = "gateway.networking.k8s.io",
                ["kind"] = "ListenerSet",
                ["namespace"] = GitOpsNames.IngressNamespace,
                ["name"] = Namespace,
                ["sectionName"] = "https",
            }),
            ["hostnames"] = new JsonArray(host),
            ["rules"] = new JsonArray(new JsonObject
            {
                ["backendRefs"] = new JsonArray(new JsonObject { ["name"] = "web", ["port"] = 8080 }),
            }),
        },
    };
}
