using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Tests.Azure;
using Platform.Conformance.Tests.GitOps;
using Platform.Conformance.Tests.Octopus;

namespace Platform.Conformance.Tests;

/// <summary>Names of the phases of a tier's sleep and wake cycle, in the order they run.</summary>
public static class SleepPhase
{
    /// <summary>Holds the tier (<see cref="TierLock"/>) so nothing else that runs in parallel starts its cluster during the cycle.</summary>
    public const string HoldTier = "hold tier";

    /// <summary>Wakes nonprod when needed, writes the canary row and reads Kyverno's ready policies and webhooks; in prod, records whether the cluster was already stopped.</summary>
    public const string AwakeBefore = "awake-before";

    /// <summary>Waits, bounded, until Octopus runs or queues no task in the tier's environments (env-sleep's busy rule).</summary>
    public const string Quiesce = "quiesce";

    /// <summary>env-sleep (forced in nonprod, by its own rules in prod), then the wait for Stopped and for the stop to settle.</summary>
    public const string Sleep = "sleep";

    /// <summary>Power state, apr-sleep-&lt;tier&gt; and, in nonprod, the wait guard of an app runbook while the tier sleeps.</summary>
    public const string Asleep = "asleep";

    /// <summary>A deployment wakes the tier (CAP-OCT-008): tdd in nonprod, a promotion in prod; env-wake directly when there is nothing to deploy or the tier did not sleep.</summary>
    public const string Wake = "wake";

    /// <summary>Power state, apr-sleep-&lt;tier&gt; disabled, and in nonprod the canary row read back and Kyverno's webhooks.</summary>
    public const string AwakeAfter = "awake-after";
}

/// <summary>What the wake phase saw of a deployment that woke a sleeping tier (CAP-OCT-008).</summary>
/// <param name="Environment">tdd or prod.</param>
/// <param name="Release">Version of the release deployed.</param>
/// <param name="Started">When the deployment was requested.</param>
/// <param name="Deployment">The deployment task as it completed.</param>
/// <param name="PlatformWake">The first platform-wake deployment to the environment since <paramref name="Started"/>, if any.</param>
/// <param name="EnvWake">The first env-wake run in the tier's infrastructure environment since <paramref name="Started"/>, if any.</param>
/// <param name="WakeTimeoutMinutes"><c>Wake.TimeoutMinutes</c> of the infrastructure environment (30 when unreadable).</param>
public sealed record WakeByDeployment(string Environment, string Release, DateTimeOffset Started, OctopusTask Deployment, OctopusTask? PlatformWake, OctopusTask? EnvWake, int WakeTimeoutMinutes);

/// <summary>A finished task and its log.</summary>
/// <param name="Task">The task.</param>
/// <param name="Log">Its raw log.</param>
public sealed record TaskWithLog(OctopusTask Task, string Log);

/// <summary>
/// The one sleep and wake cycle of a tier per run. Every test of CAP-AZ-004, CAP-AZ-005, CAP-OCT-008, CAP-OCT-010 (but the
/// prod history read), CAP-OCT-011 and CAP-GIT-011 asserts on what its phases observed, so a tier stops and starts once
/// instead of once per test. The phases (<see cref="SleepPhase"/>) run in the background on the first test's demand and
/// never again; a phase that does not pass ends the tests that need it with its name. Tests never force-sleep prod: the
/// prod cycle stops the tier only when env-sleep's own rules decide to (or finds it stopped already), else its sleep phase
/// is Inconclusive with env-sleep's reason. The cycle has its own clients and outlives the fixture that started it; every
/// fixture's teardown waits for it (<see cref="PhasedCycle.WaitForAllStartedAsync"/>), so the parallel fixtures that
/// share it never end while it runs and no non-parallel fixture overlaps it.
/// </summary>
public sealed class TierSleepCycle : OctopusCapabilityTestBase
{
    private static readonly ConcurrentDictionary<PlatformTier, Lazy<TierSleepCycle>> Cycles = new();
    private static readonly CustomResourceKind ValidatingWebhooks = new("admissionregistration.k8s.io", "v1", "validatingwebhookconfigurations");
    private readonly ConcurrentQueue<string> artifacts = new();
    private readonly PlatformTier tier;
    private IDisposable? hold;
    private bool slept;
    private CancellationToken phaseToken;

    private TierSleepCycle(PlatformTier tier)
    {
        this.tier = tier;
        Phases = new PhasedCycle(
            $"{tier.ToKey()} sleep and wake cycle",
            [
                new CyclePhase(SleepPhase.HoldTier, HoldTierAsync),
                new CyclePhase(SleepPhase.AwakeBefore, token => Guarded(token, AwakeBeforeAsync)),
                new CyclePhase(SleepPhase.Quiesce, token => Guarded(token, QuiesceAsync)),
                new CyclePhase(SleepPhase.Sleep, token => Guarded(token, SleepAsync)),
                new CyclePhase(SleepPhase.Asleep, token => Guarded(token, AsleepAsync), [SleepPhase.Sleep]),
                new CyclePhase(SleepPhase.Wake, token => Guarded(token, WakeTierAsync), [SleepPhase.HoldTier]),
                new CyclePhase(SleepPhase.AwakeAfter, token => Guarded(token, AwakeAfterAsync), [SleepPhase.Wake]),
            ],
            TimeSpan.FromHours(4),
            CycleProgress.Write,
            onEnd: EndAsync);
    }

    /// <summary>The phases.</summary>
    public PhasedCycle Phases { get; }

    /// <summary>The tier.</summary>
    public PlatformTier Tier => tier;

    /// <summary>The cluster as the cycle found it.</summary>
    public AksClusterState? InitialState { get; private set; }

    /// <summary>Prod only: the cluster was already stopped when the cycle started (the arm's forced stop), so no env-sleep ran.</summary>
    public bool StoppedBeforeCycle { get; private set; }

    /// <summary>Nonprod: the canary URL (<c>https://sandbox-tdd.&lt;apps-domain&gt;/data/canary</c>).</summary>
    public Uri? CanaryUrl { get; private set; }

    /// <summary>Nonprod: the canary value stored before the sleep.</summary>
    public Observation<string>? CanaryWrite { get; private set; }

    /// <summary>Nonprod: Kyverno policies that were ready before the stop.</summary>
    public Observation<string[]>? ReadyPoliciesBefore { get; private set; }

    /// <summary>Nonprod: Kyverno's validating webhook configurations before the stop; <c>null</c> value when RBAC hides them.</summary>
    public Observation<IReadOnlyList<string>?>? WebhooksBefore { get; private set; }

    /// <summary>The decision of the env-sleep run that stopped the tier; <c>null</c> when prod was stopped before the cycle.</summary>
    public SleepDecisionLine? Decision { get; private set; }

    /// <summary>The env-sleep task that stopped the tier.</summary>
    public OctopusTask? SleepTask { get; private set; }

    /// <summary>How the wait for a settled stop ended.</summary>
    public StopSettleResult? Settle { get; private set; }

    /// <summary>Power state while asleep.</summary>
    public Observation<AksClusterState>? AsleepState { get; private set; }

    /// <summary>apr-sleep-&lt;tier&gt; while asleep.</summary>
    public Observation<AlertProcessingRuleState>? AsleepRule { get; private set; }

    /// <summary>Nonprod: db-restore of sandbox in uat with <c>Wake.WaitMinutes=1</c> while the tier sleeps (CAP-OCT-011).</summary>
    public Observation<TaskWithLog>? WaitGuardRun { get; private set; }

    /// <summary>The deployment that woke the tier; Inconclusive when there was nothing to deploy.</summary>
    public Observation<WakeByDeployment>? WakeDeployment { get; private set; }

    /// <summary>The env-wake run that started the cluster (from the deployment's platform-wake, or run directly).</summary>
    public OctopusTask? EnvWakeTask { get; private set; }

    /// <summary>How the tier was woken, for messages.</summary>
    public string? WakeMethod { get; private set; }

    /// <summary>Power state after the wake.</summary>
    public Observation<AksClusterState>? AwakeState { get; private set; }

    /// <summary>apr-sleep-&lt;tier&gt; after the wake.</summary>
    public Observation<AlertProcessingRuleState>? AwakeRule { get; private set; }

    /// <summary>Nonprod: the canary value read back after the wake.</summary>
    public Observation<string?>? CanaryRead { get; private set; }

    /// <summary>Nonprod: Kyverno's validating webhook configurations after the wake.</summary>
    public Observation<IReadOnlyList<string>?>? WebhooksAfter { get; private set; }

    /// <inheritdoc />
    protected override CancellationToken Token => phaseToken;

    /// <summary>The cycle of a tier, created on first use (it starts on the first wait).</summary>
    /// <param name="tier">NonProd or Prod.</param>
    public static TierSleepCycle For(PlatformTier tier)
    {
        if (tier is not (PlatformTier.NonProd or PlatformTier.Prod))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only the app tiers sleep.");
        }

        return Cycles.GetOrAdd(tier, key => new Lazy<TierSleepCycle>(() => new TierSleepCycle(key))).Value;
    }

    /// <summary>
    /// Waits for the phases a test asserts on, attaches the cycle's task logs to the test and ends the test when a phase
    /// did not pass (see <see cref="PhasedCycle.RequireAsync"/>).
    /// </summary>
    /// <param name="phases">Phases the test needs.</param>
    public async Task RequireAsync(params string[] phases)
    {
        try
        {
            await Phases.RequireAsync(TestContext.CurrentContext.CancellationToken, phases);
        }
        finally
        {
            foreach (var path in artifacts.Distinct())
            {
                TestContext.AddTestAttachment(path);
            }
        }
    }

    /// <inheritdoc />
    protected override string AttachArtifact(string fileName, string content)
    {
        var path = Run.WriteArtifact($"sleep-cycle-{tier.ToKey()}-{fileName}", content);
        artifacts.Enqueue(path);
        return path;
    }

    /// <inheritdoc />
    protected override void Log(string line) => CycleProgress.Write($"{tier.ToKey()} sleep and wake cycle: {line}");

    private async Task Guarded(CancellationToken token, Func<Task> phase)
    {
        phaseToken = token;
        await phase();
    }

    private async Task HoldTierAsync(CancellationToken token)
    {
        LoadPlatformSettings();
        hold = await TierLock.AcquireAsync(tier, $"{tier.ToKey()} sleep and wake cycle", CycleProgress.Write, cancellationToken: token);
    }

    private async Task EndAsync()
    {
        hold?.Dispose();
        foreach (var failure in await DisposePlatformAsync())
        {
            Log($"cleanup '{failure.Description}' failed: {failure.Exception.Message}");
        }
    }

    private string ClusterName => AzurePlatform.ClusterName(tier);

    private string ClusterGroup => AzurePlatform.ClusterGroup(tier);

    private string InfraEnvironmentName => AzurePlatform.InfraEnvironment(tier);

    private static bool IsUp(AksClusterState state) =>
        state.IsRunning && string.Equals(state.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase);

    private Task<AksClusterState> StateAsync() => Azure.GetClusterStateAsync(ClusterGroup, ClusterName, Token);

    private async Task AwakeBeforeAsync()
    {
        RequireTier(tier, $"the {tier.ToKey()} sleep and wake cycle");
        Rest($"the {tier.ToKey()} sleep and wake cycle");
        InitialState = await StateAsync();
        Log($"found {InitialState}");
        if (tier == PlatformTier.Prod)
        {
            StoppedBeforeCycle = string.Equals(InitialState.PowerState, "Stopped", StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (!IsUp(InitialState))
        {
            await WakeAsync(tier);
            await WaitUpAsync();
        }

        var values = GitOpsRepository.TenantValues(GitOpsNames.Key(tier));
        if (values.AppsDomain is null)
        {
            CanaryWrite = Observation<string>.Failed("canary write", new InconclusiveException("the nonprod apps domain is still a placeholder in gitops/platform/tenant/values-nonprod.yaml"));
        }
        else
        {
            var url = new Uri($"https://{values.Host("sandbox-tdd")}/data/canary");
            CanaryUrl = url;
            var canary = $"conformance-{Guid.NewGuid():N}";
            CanaryWrite = await Observation<string>.CaptureAsync(
                $"canary write through PUT {url}",
                async token =>
                {
                    await Poll.UntilAsync(
                        async attempt => (await GitOpsRest.SendToHostAsync(HttpMethod.Put, url, new JsonObject { ["value"] = canary }.ToJsonString(), attempt)).StatusCode == HttpStatusCode.OK,
                        Settings.TimeLimits.WakeTimeout,
                        Settings.TimeLimits.PollInterval,
                        $"PUT {url} to store the canary",
                        retryWhen: ex => ex is HttpRequestException or TaskCanceledException,
                        cancellationToken: token);
                    return canary;
                },
                Token);
        }

        var cluster = await KubernetesAsync(tier, Token);
        ReadyPoliciesBefore = await Observation<string[]>.CaptureAsync(
            "ready Kyverno policies",
            async token => (await cluster.ListKyvernoPoliciesAsync(token)).Where(policy => policy.Ready == true).Select(policy => policy.Name).ToArray(),
            Token);
        WebhooksBefore = await Observation<IReadOnlyList<string>?>.CaptureAsync("Kyverno webhook configurations", token => KyvernoWebhooksAsync(cluster, token), Token);
        Log($"before the sleep: {CanaryWrite}; {ReadyPoliciesBefore}; {WebhooksBefore}");
    }

    private Task QuiesceAsync() => WaitForIdleTierAsync(tier, Settings.TimeLimits.RunbookTimeout);

    private TimeSpan QuickPoll => Settings.TimeLimits.PollInterval < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : Settings.TimeLimits.PollInterval > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : Settings.TimeLimits.PollInterval;

    private async Task SleepAsync()
    {
        if (StoppedBeforeCycle)
        {
            Log($"{ClusterName} was stopped before the cycle; no env-sleep run");
        }
        else
        {
            var force = tier == PlatformTier.NonProd;
            for (var attempt = 1; ; attempt++)
            {
                var (run, decision) = await RunEnvSleepAsync(tier, force);
                SleepTask = run.Task;
                Decision = decision;
                if (decision.Decision == "sleep")
                {
                    break;
                }

                if (!force)
                {
                    throw new InconclusiveException(
                        $"env-sleep kept {ClusterName} awake ({decision.Reason}). Tests never force-sleep prod, so the prod cycle stops the tier only when env-sleep's own "
                        + "rules do: outside the working window (Sleep.WorkDays, Sleep.WorkdayStart to Sleep.WorkdayEnd in America/Chicago) or when idle.");
                }

                if (attempt >= 3 || !decision.Reason.StartsWith("busy", StringComparison.Ordinal))
                {
                    throw new AssertionException($"env-sleep with Sleep.Force kept {ClusterName} awake after {attempt} run(s): {decision}");
                }

                Log($"env-sleep found the tier busy ({decision.Reason}); run {attempt + 1} of 3 in one minute");
                await Task.Delay(TimeSpan.FromMinutes(1), Token);
            }
        }

        var stopped = await Poll.UntilAsync(
            _ => StateAsync(),
            state => string.Equals(state.PowerState, "Stopped", StringComparison.OrdinalIgnoreCase) && !string.Equals(state.ProvisioningState, "Stopping", StringComparison.OrdinalIgnoreCase),
            Settings.TimeLimits.WakeTimeout,
            QuickPoll,
            $"{ClusterName} to stop",
            cancellationToken: Token);
        slept = true;
        Log($"stopped: {stopped}");
        Settle = await StopSettle.WaitAsync(ReadStopAsync, QuickPoll, StopSettle.Bound(), ConformanceProgress.Write, cancellationToken: Token);
        Log($"stop {Settle}");
    }

    private async Task<StopReading> ReadStopAsync(CancellationToken token)
    {
        var state = await Azure.GetClusterStateAsync(ClusterGroup, ClusterName, token);
        var pools = state.AgentPools
            .Where(pool => !string.Equals(pool.PowerState, "Stopped", StringComparison.OrdinalIgnoreCase) || !string.Equals(pool.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase))
            .Select(pool => $"{pool.Name}:{pool.PowerState}/{pool.ProvisioningState}")
            .ToArray();
        IReadOnlyList<string>? attached;
        try
        {
            attached = (await Azure.ListManagedDisksAsync($"rg-platform-{tier.ToKey()}-data", token))
                .Where(disk => string.Equals(disk.DiskState, "Attached", StringComparison.OrdinalIgnoreCase))
                .Select(disk => disk.Name)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Log($"data disks of rg-platform-{tier.ToKey()}-data are not readable ({ex.Message}); the upper bound decides");
            attached = null;
        }

        return new StopReading(state.PowerState, state.ProvisioningState, pools, attached);
    }

    private async Task AsleepAsync()
    {
        AsleepState = await Observation<AksClusterState>.CaptureAsync("power state while asleep", _ => StateAsync(), Token);
        AsleepRule = await Observation<AlertProcessingRuleState>.CaptureAsync(
            $"{AzurePlatform.SleepRule(tier)} while asleep",
            token => Azure.GetAlertProcessingRuleAsync(ClusterGroup, AzurePlatform.SleepRule(tier), token),
            Token);
        Log($"asleep: {AsleepState}; {AsleepRule}");
        if (tier == PlatformTier.NonProd)
        {
            WaitGuardRun = await Observation<TaskWithLog>.CaptureAsync(
                "db-restore of sandbox in uat with Wake.WaitMinutes=1",
                async token =>
                {
                    var run = await Octopus.RunRunbookAsync(
                        new OctopusRunbookRunRequest
                        {
                            Project = SandboxProject,
                            Runbook = "db-restore",
                            Environment = "uat",
                            PromptedVariables = new Dictionary<string, string> { ["Wake.WaitMinutes"] = "1" },
                            Comments = $"Conformance run {Run.RunId}: wait guard",
                        },
                        Settings.TimeLimits.RunbookTimeout,
                        token);
                    var log = await Octopus.GetTaskLogAsync(run.Task.Id, token);
                    AttachArtifact($"db-restore-wait-guard-{run.Task.Id}.log", log);
                    return new TaskWithLog(run.Task, log);
                },
                Token);
            Log($"asleep: {WaitGuardRun}");
        }
    }

    private async Task WakeTierAsync()
    {
        if (!slept)
        {
            WakeDeployment = Observation<WakeByDeployment>.Failed("deployment that wakes the tier", new InconclusiveException($"the {tier.ToKey()} tier did not sleep in this cycle"));
            await WakeDirectlyAsync("env-wake (the tier did not sleep)");
            return;
        }

        var environment = tier == PlatformTier.Prod ? "prod" : "tdd";
        var release = tier == PlatformTier.Prod ? await FindDeployedReleaseAsync("uat", "Default") : await LastDeployedReleaseAsync("tdd");
        if (release is null)
        {
            WakeDeployment = Observation<WakeByDeployment>.Failed(
                "deployment that wakes the tier",
                new InconclusiveException(tier == PlatformTier.Prod
                    ? "no sandbox release of channel Default has been deployed to uat, so there is nothing to promote to prod; the cycle does not deploy through nonprod"
                    : "no sandbox release has been deployed to tdd yet (P1-11)"));
            await WakeDirectlyAsync("env-wake (nothing to deploy)");
            return;
        }

        WakeDeployment = await Observation<WakeByDeployment>.CaptureAsync($"deployment of sandbox {release.Version} to {environment}", _ => DeployToWakeAsync(release, environment), Token);
        Log($"wake: {WakeDeployment}");
        EnvWakeTask = WakeDeployment.Error is null ? WakeDeployment.Require(SleepPhase.Wake).EnvWake : null;
        WakeMethod = $"deployment of sandbox {release.Version} to {environment} (platform-wake, then env-wake)";
        if (!IsUp(await StateAsync()))
        {
            await WakeDirectlyAsync($"env-wake after the deployment left {ClusterName} down");
        }
    }

    private async Task<WakeByDeployment> DeployToWakeAsync(SandboxRelease release, string environment)
    {
        var started = DateTimeOffset.UtcNow;
        var deployment = await DeployAsync(release, environment);
        CancelAtTeardown(deployment.TaskId, $"the waking {environment} deployment");
        var task = await CompleteAsync(deployment.TaskId, $"waking deployment to {environment}", requireSuccess: false);
        var child = (await Octopus.GetTasksAsync(new OctopusTaskQuery { Project = WakeProject, Environment = environment, Take = 10 }, Token))
            .Where(candidate => candidate.QueueTime >= started)
            .OrderBy(candidate => candidate.QueueTime)
            .FirstOrDefault();
        var wake = (await RunbookTasksAsync(InfraEnvironmentName, "env-wake"))
            .Where(candidate => candidate.QueueTime >= started)
            .OrderBy(candidate => candidate.QueueTime)
            .FirstOrDefault();
        var variables = await Octopus.GetProjectVariablesAsync(InfrastructureProject, OctopusRunbookRunRequest.MainBranch, Token);
        var timeout = int.TryParse(VariableValue(variables, "Wake.TimeoutMinutes", await EnvironmentIdAsync(InfraEnvironmentName)), out var minutes) ? minutes : 30;
        return new WakeByDeployment(environment, release.Version, started, task, child, wake, timeout);
    }

    private async Task WakeDirectlyAsync(string method)
    {
        WakeMethod = method;
        EnvWakeTask = (await WakeAsync(tier)).Task;
        await WaitUpAsync();
    }

    private Task<AksClusterState> WaitUpAsync() =>
        Poll.UntilAsync(_ => StateAsync(), IsUp, Settings.TimeLimits.WakeTimeout, QuickPoll, $"{ClusterName} to run", cancellationToken: Token);

    private async Task AwakeAfterAsync()
    {
        AwakeState = await Observation<AksClusterState>.CaptureAsync("power state after the wake", _ => WaitUpAsync(), Token);
        AwakeRule = await Observation<AlertProcessingRuleState>.CaptureAsync(
            $"{AzurePlatform.SleepRule(tier)} after the wake",
            token => Poll.UntilAsync(
                attempt => Azure.GetAlertProcessingRuleAsync(ClusterGroup, AzurePlatform.SleepRule(tier), attempt),
                rule => !rule.Enabled,
                TimeSpan.FromMinutes(2),
                QuickPoll,
                $"{AzurePlatform.SleepRule(tier)} to be disabled",
                cancellationToken: token),
            Token);
        Log($"after the wake: {AwakeState}; {AwakeRule}");
        if (tier == PlatformTier.Prod)
        {
            return;
        }

        if (CanaryUrl is { } url)
        {
            CanaryRead = await Observation<string?>.CaptureAsync(
                $"canary read through GET {url}",
                token => Poll.UntilAsync(
                    async attempt =>
                    {
                        var response = await GitOpsRest.SendToHostAsync(HttpMethod.Get, url, null, attempt);
                        return response.StatusCode == HttpStatusCode.OK ? ReadValue(response.Body) : null;
                    },
                    value => value is not null,
                    Settings.TimeLimits.WakeTimeout,
                    Settings.TimeLimits.PollInterval,
                    $"GET {url} to answer after the wake",
                    retryWhen: ex => ex is HttpRequestException or TaskCanceledException,
                    cancellationToken: token),
                Token);
        }

        var cluster = await KubernetesAsync(tier, Token);
        try
        {
            var workloads = await cluster.ListDeploymentsAsync("workorders-tdd", cancellationToken: Token);
            Log($"after the wake, workorders-tdd deployments: {string.Join(", ", workloads.Select(workload => $"{workload.Name} ready {workload.ReadyReplicas}/{workload.DesiredReplicas}"))}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"after the wake, workorders-tdd deployments are not readable: {ex.Message}");
        }

        WebhooksAfter = await Observation<IReadOnlyList<string>?>.CaptureAsync("Kyverno webhook configurations after the wake", token => KyvernoWebhooksAsync(cluster, token), Token);
        Log($"after the wake: {CanaryRead}; {WebhooksAfter}");
    }

    private async Task<IReadOnlyList<string>?> KyvernoWebhooksAsync(IKubernetesApi cluster, CancellationToken cancellationToken)
    {
        try
        {
            return (await cluster.ListCustomObjectsAsync(ValidatingWebhooks, null, cancellationToken))
                .Select(item => ArmReader.Text(item, "metadata", "name") ?? string.Empty)
                .Where(name => name.Contains("kyverno", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (PlatformApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
        {
            Log($"webhook configurations are not readable with AKS RBAC Reader; the ready policies stand in for them: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            Log($"webhook configurations could not be read: {ex.Message}");
            return null;
        }
    }

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
