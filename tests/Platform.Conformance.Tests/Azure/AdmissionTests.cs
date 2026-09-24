using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// CAP-AZ-001: prod admits only images signed by the app's own release pipeline. Server-side dry runs of bare pods in
/// <c>sandbox-prod</c>: the signed sandbox image that runs there is admitted; the unsigned fixture
/// <c>apps/sandbox/unsigned:0.0.0-fixture</c> is rejected by the release-signature policies
/// (<c>verify-app-release-signatures</c>, <c>app-sandbox-release-signatures</c>).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SignedAdmissionTests : AzureConformanceTest
{
    private static readonly string Namespace = AzurePlatform.Namespace(AzurePlatform.Sandbox, "prod");

    [Test]
    [Capability("CAP-AZ-001")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(45 * 60 * 1000)]
    public async Task Should_CreatePodDryRun_SignedSandboxImage_BeAdmitted()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var prefix = $"{RegistryLoginServer}/apps/{AzurePlatform.Sandbox}/";
        await EnsureAwakeAsync(PlatformTier.Prod, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.Prod, cancellationToken);
        var image = (await cluster.ListDeploymentsAsync(Namespace, cancellationToken: cancellationToken))
            .SelectMany(deployment => deployment.Images)
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new PlatformPrerequisiteException($"No Deployment in {Namespace} runs an image from {prefix}: release the sandbox to prod first.");

        var result = await cluster.CreatePodAsync(DryRunPod(Namespace, "signed", image), dryRun: true, cancellationToken);

        RequireAdmissionDecision(result, Namespace);
        result.Created.ShouldBeTrue($"the signed image {image} was rejected in {Namespace}: {result.StatusCode} {result.Message}");
    }

    [Test]
    [Capability("CAP-AZ-001")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(45 * 60 * 1000)]
    public async Task Should_CreatePodDryRun_UnsignedFixtureImage_BeRejected()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var image = $"{RegistryLoginServer}/{AzurePlatform.UnsignedFixture}";
        await EnsureAwakeAsync(PlatformTier.Prod, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.Prod, cancellationToken);

        var result = await cluster.CreatePodAsync(DryRunPod(Namespace, "unsigned", image), dryRun: true, cancellationToken);

        RequireAdmissionDecision(result, Namespace);
        result.Created.ShouldBeFalse($"the unsigned fixture {image} was admitted in {Namespace}");
        (result.Message ?? string.Empty).ShouldContain("release-signatures", Case.Insensitive, $"the rejection must come from a release-signature policy: {result.Message}");
    }
}

/// <summary>
/// CAP-AZ-002: prod admits only images from the app's own registry path. A dry run in <c>sandbox-prod</c> of an image
/// that app #1 (<c>workorders</c>) runs in prod, signed by its own release pipeline, is rejected by
/// <c>restrict-app-image-paths</c>, so the path rule alone decides.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class RegistryPathAdmissionTests : AzureConformanceTest
{
    [Test]
    [Capability("CAP-AZ-002")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(45 * 60 * 1000)]
    public async Task Should_CreatePodDryRun_OtherAppImage_BeRejected()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var target = AzurePlatform.Namespace(AzurePlatform.Sandbox, "prod");
        var prefix = $"{RegistryLoginServer}/apps/{AzurePlatform.OtherApp}/";
        await EnsureAwakeAsync(PlatformTier.Prod, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.Prod, cancellationToken);
        string? image = null;
        foreach (var owned in await cluster.ListNamespacesAsync($"platform/app={AzurePlatform.OtherApp}", cancellationToken))
        {
            image ??= (await cluster.ListDeploymentsAsync(owned.Name, cancellationToken: cancellationToken))
                .SelectMany(deployment => deployment.Images)
                .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (image is null)
        {
            throw new PlatformPrerequisiteException($"No Deployment of {AzurePlatform.OtherApp} in prod runs an image from {prefix}; the test needs another app's signed image.");
        }

        var result = await cluster.CreatePodAsync(DryRunPod(target, "other-app", image), dryRun: true, cancellationToken);

        RequireAdmissionDecision(result, target);
        result.Created.ShouldBeFalse($"{image} of {AzurePlatform.OtherApp} was admitted in {target}");
        var message = result.Message ?? string.Empty;
        (message.Contains("restrict-app-image-paths", StringComparison.OrdinalIgnoreCase) || message.Contains("Images outside", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue($"the rejection must come from restrict-app-image-paths: {message}");
    }
}

/// <summary>
/// CAP-AZ-003: only SQL Server Express runs in app namespaces. Dry runs in <c>sandbox-prod</c> of the SQL Server image:
/// <c>MSSQL_PID=Express</c> is admitted (the control), and Developer, Enterprise and an unset edition are rejected by
/// <c>require-mssql-express</c>.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SqlEditionTests : AzureConformanceTest
{
    private const string FallbackImage = "mcr.microsoft.com/mssql/server:2022-latest";

    [Test]
    [Capability("CAP-AZ-003")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(45 * 60 * 1000)]
    public async Task Should_CreatePodDryRun_OtherSqlEditions_BeRejected()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var target = AzurePlatform.Namespace(AzurePlatform.Sandbox, "prod");
        await EnsureAwakeAsync(PlatformTier.Prod, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.Prod, cancellationToken);
        var image = (await cluster.ListStatefulSetsAsync(target, cancellationToken: cancellationToken))
            .SelectMany(statefulSet => statefulSet.Images)
            .FirstOrDefault(candidate => candidate.Contains("mssql/server", StringComparison.Ordinal))
            ?? FallbackImage;
        var editions = new Dictionary<string, string?> { ["developer"] = "Developer", ["enterprise"] = "Enterprise", ["unset"] = null };

        var control = await cluster.CreatePodAsync(DryRunPod(target, "sql-express", image, Edition("Express")), dryRun: true, cancellationToken);
        var outcomes = new List<(string Edition, bool Created, string Message)>();
        foreach (var (name, edition) in editions)
        {
            var result = await cluster.CreatePodAsync(DryRunPod(target, $"sql-{name}", image, Edition(edition)), dryRun: true, cancellationToken);
            RequireAdmissionDecision(result, target);
            outcomes.Add((name, result.Created, result.Message ?? string.Empty));
        }

        RequireAdmissionDecision(control, target);
        control.Created.ShouldBeTrue($"the Express control of {image} was rejected in {target}, so the other results prove nothing: {control.Message}");
        outcomes.Where(outcome => outcome.Created).Select(outcome => outcome.Edition).ShouldBeEmpty($"editions other than Express were admitted in {target} ({image})");
        outcomes.Where(outcome => !outcome.Message.Contains("MSSQL_PID", StringComparison.OrdinalIgnoreCase) && !outcome.Message.Contains("require-mssql-express", StringComparison.OrdinalIgnoreCase))
            .Select(outcome => $"{outcome.Edition}: {outcome.Message}")
            .ShouldBeEmpty("every rejection must come from require-mssql-express");
    }

    private static Dictionary<string, string?> Edition(string? edition) => new()
    {
        ["ACCEPT_EULA"] = "Y",
        ["MSSQL_PID"] = edition,
    };
}
