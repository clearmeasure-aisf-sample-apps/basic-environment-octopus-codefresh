using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-011: data survives a cluster sleep. The test writes a canary row through the sandbox's
/// <c>PUT /data/canary</c> on <c>https://sandbox-tdd.&lt;apps-domain-nonprod&gt;</c>, force-sleeps nonprod (runbook
/// <c>env-sleep</c> with <c>Sleep.Force</c>), waits the stop grace (<c>CONFORMANCE_STOP_GRACE_MINUTES</c>, 15 by default,
/// E50), wakes it (<c>env-wake</c>) and reads the row back: the database's static volume
/// <c>disk-sandbox-tdd-db</c> kept it. The cluster is left awake; the pipeline's teardown sleeps what the run woke.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class SleepDataSurvivalTests : GitOpsTestBase
{
    private const string InfrastructureProject = "platform-infrastructure";
    private const string InfrastructureEnvironment = "infra-nonprod";

    [Test]
    [Capability("CAP-GIT-011")]
    [Category(Categories.NonProd)]
    [Category(Categories.Slow)]
    [CancelAfter(2 * 60 * 60 * 1000)]
    public async Task Should_Sleep_CanaryRowWrittenBeforeForceSleep_IsReadAfterWake()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var values = TenantValues(PlatformTier.NonProd);
        if (values.AppsDomain is null)
        {
            Unobservable("the nonprod apps domain is still a placeholder in gitops/platform/tenant/values-nonprod.yaml");
        }

        var tier = Settings.Tier(PlatformTier.NonProd);
        Settings.Check("the nonprod cluster power state")
            .Setting("Tiers.nonprod.ResourceGroup", tier.ResourceGroup)
            .Setting("Tiers.nonprod.ClusterName", tier.ClusterName)
            .ThrowIfMissing();
        var canaryUrl = new Uri($"https://{values.Host("sandbox-tdd")}/data/canary");
        await EnsureRunningAsync(tier.ResourceGroup!, tier.ClusterName!, cancellationToken);
        var canary = $"conformance-{Guid.NewGuid():N}";
        await Poll.UntilAsync(
            async token => (await GitOpsRest.SendToHostAsync(HttpMethod.Put, canaryUrl, new JsonObject { ["value"] = canary }.ToJsonString(), token)).StatusCode == HttpStatusCode.OK,
            Settings.TimeLimits.WakeTimeout,
            Settings.TimeLimits.PollInterval,
            $"PUT {canaryUrl} to store the canary",
            retryWhen: ex => ex is HttpRequestException or TaskCanceledException,
            cancellationToken: cancellationToken);

        await RunInfrastructureRunbookAsync("env-sleep", new Dictionary<string, string> { ["Sleep.Force"] = "True" }, cancellationToken);
        await WaitForPowerStateAsync(tier.ResourceGroup!, tier.ClusterName!, "Stopped", cancellationToken);
        await Task.Delay(StopGrace(), cancellationToken);
        await RunInfrastructureRunbookAsync("env-wake", new Dictionary<string, string>(), cancellationToken);
        await WaitForPowerStateAsync(tier.ResourceGroup!, tier.ClusterName!, "Running", cancellationToken);

        var stored = await Poll.UntilAsync(
            async token =>
            {
                var response = await GitOpsRest.SendToHostAsync(HttpMethod.Get, canaryUrl, null, token);
                return response.StatusCode == HttpStatusCode.OK ? ReadValue(response.Body) : null;
            },
            value => value is not null,
            Settings.TimeLimits.WakeTimeout,
            Settings.TimeLimits.PollInterval,
            $"GET {canaryUrl} to answer after the wake",
            retryWhen: ex => ex is HttpRequestException or TaskCanceledException,
            cancellationToken: cancellationToken);
        stored.ShouldBe(canary, "the canary row written before the sleep");
    }

    private async Task EnsureRunningAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken)
    {
        var state = await Azure.GetClusterStateAsync(resourceGroup, clusterName, cancellationToken);
        if (!string.Equals(state.PowerState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            await RunInfrastructureRunbookAsync("env-wake", new Dictionary<string, string>(), cancellationToken);
            await WaitForPowerStateAsync(resourceGroup, clusterName, "Running", cancellationToken);
        }
    }

    private async Task RunInfrastructureRunbookAsync(string runbook, IReadOnlyDictionary<string, string> prompted, CancellationToken cancellationToken)
    {
        var result = await Octopus.RunRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = InfrastructureProject,
                Runbook = runbook,
                Environment = InfrastructureEnvironment,
                PromptedVariables = prompted,
                Comments = $"Conformance run {Run.RunId} (CAP-GIT-011)",
            },
            Settings.TimeLimits.RunbookTimeout,
            cancellationToken);
        AttachArtifact($"{runbook}-{result.Task.Id}.log", await Octopus.GetTaskLogAsync(result.Task.Id, cancellationToken));
        result.Task.FinishedSuccessfully.ShouldBeTrue($"{runbook} in {InfrastructureEnvironment}: {result.Task.ErrorMessage}");
    }

    private Task WaitForPowerStateAsync(string resourceGroup, string clusterName, string powerState, CancellationToken cancellationToken) =>
        Poll.UntilAsync(
            async token => string.Equals((await Azure.GetClusterStateAsync(resourceGroup, clusterName, token)).PowerState, powerState, StringComparison.OrdinalIgnoreCase),
            Settings.TimeLimits.WakeTimeout,
            Settings.TimeLimits.PollInterval,
            $"cluster {clusterName} to be {powerState}",
            cancellationToken: cancellationToken);

    private static TimeSpan StopGrace() =>
        int.TryParse(Environment.GetEnvironmentVariable("CONFORMANCE_STOP_GRACE_MINUTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(15);

    private static string? ReadValue(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return GitOpsCluster.Text(document.RootElement, "value");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
