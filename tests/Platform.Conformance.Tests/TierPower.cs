using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Tests.Azure;

namespace Platform.Conformance.Tests;

/// <summary>
/// Wakes an app-cluster tier for a test area whose fixtures need its Kubernetes API (ADR-IR33: the clusters sleep outside
/// the working window, and an hourly env-sleep can stop one between two fixtures of a long run). Runs runbook env-wake of
/// <c>platform-infrastructure</c> in the tier's infra environment, as the Azure area does, and waits until the cluster
/// runs; a running cluster costs one ARM read.
/// </summary>
public static class TierPower
{
    /// <summary>Starts the tier's cluster when it is not running and waits until it is.</summary>
    /// <param name="octopus">Octopus client (the automation user's key).</param>
    /// <param name="azure">Azure client that reads the power state.</param>
    /// <param name="settings">Harness settings (time limits).</param>
    /// <param name="runId">Run ID, for the task comment.</param>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    public static async Task EnsureAwakeAsync(IOctopusApi octopus, IAzureApi azure, PlatformSettings settings, string runId, PlatformTier tier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(octopus);
        ArgumentNullException.ThrowIfNull(azure);
        ArgumentNullException.ThrowIfNull(settings);
        if (IsUp(await azure.GetClusterStateAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken)))
        {
            return;
        }

        var run = await octopus.StartRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = AzurePlatform.InfrastructureProject,
                Runbook = "env-wake",
                Environment = AzurePlatform.InfraEnvironment(tier),
                PromptedVariables = new Dictionary<string, string>(),
                Comments = $"conformance:{runId} (wake for {TestContext.CurrentContext.Test.ClassName})",
            },
            cancellationToken);
        var task = await octopus.WaitForTaskAsync(run.TaskId, settings.TimeLimits.WakeTimeout, OctopusTaskWait.Completed, cancellationToken);
        task.FinishedSuccessfully.ShouldBeTrue($"env-wake in {AzurePlatform.InfraEnvironment(tier)} ({task})");

        var deadline = DateTimeOffset.UtcNow + settings.TimeLimits.WakeTimeout;
        AksClusterState state;
        while (!IsUp(state = await azure.GetClusterStateAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken)))
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, $"{state}: not running {settings.TimeLimits.WakeTimeout.TotalMinutes} minutes after env-wake {run.TaskId}");
            await Task.Delay(settings.TimeLimits.PollInterval, cancellationToken);
        }
    }

    private static bool IsUp(AksClusterState state) =>
        state.IsRunning && string.Equals(state.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase);
}
