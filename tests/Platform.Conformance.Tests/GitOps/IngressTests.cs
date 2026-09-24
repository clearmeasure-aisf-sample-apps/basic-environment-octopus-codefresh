using System.Net;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-012: ingress serves valid HTTPS, redirects HTTP and hides diagnostics in uat and prod. For every app host of a
/// tier (<c>&lt;namespace&gt;.&lt;apps-domain-&lt;tier&gt;&gt;</c>, decision 21): the TLS handshake validates against the system
/// trust store and the certificate outlives the next week (cert-manager renews 30 days ahead); <c>http://</c> answers
/// 301 to <c>https://</c>; in uat and prod the diagnostic paths of app #1 and the fixture answer 302 to <c>/</c> at the
/// Gateway, so they never reach the app. Redirects are never followed.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class IngressTests : GitOpsTestBase
{
    [Test]
    [Capability("CAP-GIT-012")]
    [Category(Categories.NonProd)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Serve_EveryAppHostOnNonprod_WithValidTlsRedirectAndHiddenDiagnostics()
    {
        var problems = await IngressProblemsAsync(PlatformTier.NonProd, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    [Capability("CAP-GIT-012")]
    [Category(Categories.Prod)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Serve_EveryAppHostOnProd_WithValidTlsRedirectAndHiddenDiagnostics()
    {
        var problems = await IngressProblemsAsync(PlatformTier.Prod, TestContext.CurrentContext.CancellationToken);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static async Task<List<string>> IngressProblemsAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var tierKey = GitOpsNames.Key(tier);
        var values = TenantValues(tier);
        if (values.AppsDomain is null)
        {
            Unobservable($"the {tierKey} apps domain is still a placeholder in gitops/platform/tenant/values-{tierKey}.yaml");
        }

        var problems = new List<string>();
        foreach (var app in Apps())
        {
            foreach (var environment in app.EnvironmentsOf(tierKey))
            {
                foreach (var namespaceName in app.Namespaces(environment))
                {
                    var host = values.Host(namespaceName);
                    await CheckHostAsync(host, problems, cancellationToken);
                    if (environment != "tdd" && namespaceName == app.Namespace(environment) && GitOpsNames.HiddenDiagnosticPaths.TryGetValue(app.Name, out var paths))
                    {
                        await CheckHiddenAsync(host, paths, problems, cancellationToken);
                    }
                }
            }
        }

        return problems;
    }

    private static async Task CheckHostAsync(string host, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            var secure = await GitOpsRest.SendToHostAsync(HttpMethod.Get, new Uri($"https://{host}/"), null, cancellationToken);
            if (secure.Certificate is null)
            {
                problems.Add($"https://{host}/: no server certificate was presented");
            }
            else if (secure.Certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(7))
            {
                problems.Add($"https://{host}/: the certificate expires at {secure.Certificate.NotAfter:u} (renewal is 30 days ahead)");
            }
        }
        catch (HttpRequestException ex)
        {
            problems.Add($"https://{host}/: {ex.Message} (TLS validation or connection failed)");
        }

        try
        {
            var plain = await GitOpsRest.SendToHostAsync(HttpMethod.Get, new Uri($"http://{host}/"), null, cancellationToken);
            if (plain.StatusCode != HttpStatusCode.MovedPermanently || plain.Location is null || plain.Location.Scheme != Uri.UriSchemeHttps || plain.Location.Host != host)
            {
                problems.Add($"http://{host}/ answered {(int)plain.StatusCode} {plain.Location}, expected 301 to https://{host}/");
            }
        }
        catch (HttpRequestException ex)
        {
            problems.Add($"http://{host}/: {ex.Message}");
        }
    }

    private static async Task CheckHiddenAsync(string host, IEnumerable<string> paths, List<string> problems, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            try
            {
                var response = await GitOpsRest.SendToHostAsync(HttpMethod.Get, new Uri($"https://{host}{path}"), null, cancellationToken);
                if (response.StatusCode != HttpStatusCode.Found || !IsRoot(response.Location, host))
                {
                    problems.Add($"https://{host}{path} answered {(int)response.StatusCode} {response.Location}, expected 302 to /");
                }
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"https://{host}{path}: {ex.Message}");
            }
        }
    }

    private static bool IsRoot(Uri? location, string host) =>
        location is not null && (location.IsAbsoluteUri ? location.Host == host && location.AbsolutePath == "/" : location.OriginalString == "/");
}
