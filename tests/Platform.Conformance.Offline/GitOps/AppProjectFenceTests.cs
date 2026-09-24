using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Tests.GitOps;

namespace Platform.Conformance.Offline.GitOps;

/// <summary>
/// CAP-GIT-005, offline part: the tenant chart fences each app. The rendered AppProject <c>app-&lt;app&gt;</c> of every
/// committed descriptor, evaluated like Argo CD evaluates it (<see cref="AppProjectFence"/>), refuses cluster-scoped
/// kinds, the platform-owned namespaced kinds and every namespace but the app's own, and still admits everyday kinds.
/// The chart itself refuses descriptors that would widen the fence: a reserved or malformed name, an unknown environment,
/// another app's Octopus project, a chart outside the app's folder, a deployable named <c>db</c> and an unknown database
/// engine. The live part (Platform.Conformance.Tests.GitOps.AppProjectFenceTests) reads the AppProjects from the clusters.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class AppProjectFenceTests
{
    private const string ValidDescriptor = """
        schema: 1
        name: ledger
        status: active
        environments: [tdd, uat, prod]
        octopus:
          projects:
            - name: ledger
        deployables:
          - name: app
            octopusProject: ledger
            packaging: kustomize
            images: [web]
        """;

    private static readonly (string Case, string From, string To, string Refusal)[] Wideners =
    [
        ("a reserved name", "name: ledger", "name: argocd", "is reserved"),
        ("a malformed name", "name: ledger", "name: Ledger-1", "must match"),
        ("an unknown environment", "environments: [tdd, uat, prod]", "environments: [tdd, staging]", "must be tdd, uat or prod"),
        ("another app's Octopus project", "octopusProject: ledger", "octopusProject: workorders", "must be ledger or ledger-<part>"),
        ("a deployable named db", "  - name: app", "  - name: db", "not be db"),
        ("a Helm chart outside the app folder", "packaging: kustomize\n    images: [web]", "packaging: helm\n    images: [web]\n    helm:\n      chart: ../workorders/charts/app\n      imageReplacePaths: [\"{{ .Values.image.repository }}:{{ .Values.image.tag }}\"]", "must be a path inside"),
        ("an unknown database engine", "    images: [web]", "    images: [web]\ndatabase:\n  engine: mssql-2022-enterprise", "is not a platform component"),
    ];

    [Test]
    [Capability("CAP-GIT-005")]
    public void Should_Render_AppProjects_DenyForbiddenKindsAndForeignDestinations()
    {
        var descriptors = TenantChart.Descriptors;
        var others = descriptors.Select(descriptor => descriptor.App.Name).Append("ledger").ToArray();

        TenantChart.EnsureHelmAvailable();

        Assert.Multiple(() =>
        {
            foreach (var (app, path) in descriptors)
            {
                foreach (var tier in TenantChart.Tiers)
                {
                    var values = GitOpsRepository.TenantValues(tier);
                    var render = TenantChart.Render(path, tier);
                    render.ExitCode.ShouldBe(0, render.Error);
                    var appProject = render.Objects.Single(item => GitOpsCluster.Text(item, "kind") == "AppProject" && GitOpsCluster.Text(item, "metadata", "name") == app.AppProject);
                    var envRepoUrl = PlatformSettings.IsMissing(values.EnvRepoUrl) ? null : values.EnvRepoUrl;
                    AppProjectFence.Read(appProject).Violations(app, tier, envRepoUrl, others).ShouldBeEmpty($"{app.AppProject} on {tier}");
                    GitOpsCluster.Child(appProject, "spec", "sourceRepos")[0].GetString().ShouldBe(values.EnvRepoUrl, $"{app.AppProject} on {tier}: the only source is the environment repository");
                }
            }
        });
    }

    [Test]
    [Capability("CAP-GIT-005")]
    public void Should_Render_ValidDescriptor_IsAcceptedAsTheBaseline()
    {
        var render = TenantChart.RenderText(ValidDescriptor, "nonprod");

        render.ExitCode.ShouldBe(0, render.Error);
        render.Objects.ShouldContain(item => GitOpsCluster.Text(item, "kind") == "AppProject" && GitOpsCluster.Text(item, "metadata", "name") == "app-ledger");
    }

    [TestCaseSource(nameof(WidenerCases))]
    [Capability("CAP-GIT-005")]
    public void Should_Render_DescriptorWideningTheFence_IsRefused(string widening, string from, string to, string refusal)
    {
        ValidDescriptor.ShouldContain(from, Case.Sensitive, $"the widening '{widening}' no longer matches the baseline descriptor");
        var descriptor = ValidDescriptor.Replace(from, to, StringComparison.Ordinal);

        var render = TenantChart.RenderText(descriptor, "nonprod");

        render.ExitCode.ShouldNotBe(0, $"the chart rendered a descriptor with {widening}");
        render.Error.ShouldContain(refusal, Case.Sensitive, $"refusal of {widening}");
    }

    private static IEnumerable<TestCaseData> WidenerCases() =>
        Wideners.Select(widener => new TestCaseData(widener.Case, widener.From, widener.To, widener.Refusal).SetArgDisplayNames(widener.Case));
}
