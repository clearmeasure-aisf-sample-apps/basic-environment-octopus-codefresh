using System.Net;
using k8s.Models;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// Shared steps of the CAP-AZ tests: runbook runs of <c>platform-infrastructure</c> with approvals answered as
/// <c>conformance:&lt;run-id&gt;</c>, waking and sleeping a tier, raw ARM reads, the fixture app's canary and server-side
/// dry-run pods. The cluster names are the §7.0 names; the settings file must name the same clusters, because the
/// harness connects to Kubernetes through it.
/// </summary>
public abstract class AzureConformanceTest : PlatformTestBase
{
    private ArmReader? arm;
    private OctopusExtras? octopusExtras;

    /// <summary>Notes recorded with every intervention the tests answer (<c>Platform.InterventionTestMode</c>).</summary>
    protected string ApprovalNote => $"conformance:{Run.RunId}";

    /// <summary>Raw ARM reads; Inconclusive without a subscription or a credential.</summary>
    protected ArmReader Arm
    {
        get
        {
            if (arm is null)
            {
                Settings.Check("Azure Resource Manager reads")
                    .Setting($"{nameof(Settings.AzureSubscriptionId)} (or {EnvironmentVariableNames.AzureSubscriptionId})", Settings.AzureSubscriptionId)
                    .ThrowIfMissing();
                arm = new ArmReader(AzureCredentialFactory.Create(Settings), Settings.AzureSubscriptionId!, Settings.TimeLimits.HttpTimeout);
            }

            return arm;
        }
    }

    /// <summary>Octopus calls beyond the harness client; Inconclusive without the Octopus settings and key.</summary>
    protected OctopusExtras OctopusExtras => octopusExtras ??= new OctopusExtras(Settings, Octopus);

    /// <summary>The registry login server, for example <c>&lt;acr-name&gt;.azurecr.io</c>; Inconclusive while it is a placeholder.</summary>
    protected string RegistryLoginServer
    {
        get
        {
            Settings.Check("the registry").Setting(nameof(Settings.RegistryLoginServer), Settings.RegistryLoginServer).ThrowIfMissing();
            return Settings.RegistryLoginServer!.Trim().TrimEnd('/');
        }
    }

    /// <summary>Disposes the helpers of this fixture.</summary>
    [OneTimeTearDown]
    public void DisposeAzureHelpers()
    {
        arm?.Dispose();
        octopusExtras?.Dispose();
    }

    /// <summary>
    /// The Kubernetes API of a tier's cluster, after checking that the settings file names the §7.0 cluster
    /// (Inconclusive otherwise, so a stale settings file never tests the wrong cluster).
    /// </summary>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    protected Task<IKubernetesApi> ClusterAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var configured = Settings.Tier(tier);
        if (!PlatformSettings.IsMissing(configured.ClusterName)
            && (!string.Equals(configured.ClusterName, AzurePlatform.ClusterName(tier), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(configured.ResourceGroup, AzurePlatform.ClusterGroup(tier), StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlatformPrerequisiteException(
                $"{Settings.SettingsSource} names cluster {configured.ResourceGroup}/{configured.ClusterName} for tier {tier.ToKey()}; the §7.0 cluster is "
                + $"{AzurePlatform.ClusterGroup(tier)}/{AzurePlatform.ClusterName(tier)}. Update Tiers.{tier.ToKey()} (terraform/foundation output conformance_settings).");
        }

        return KubernetesAsync(tier, cancellationToken);
    }

    /// <summary>The power and provisioning state of a tier's cluster.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected Task<AksClusterState> ClusterStateAsync(PlatformTier tier, CancellationToken cancellationToken) =>
        Azure.GetClusterStateAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken);

    /// <summary>
    /// Runs a config-as-code runbook at <c>refs/heads/main</c> and waits for it. Manual interventions are answered with
    /// <see cref="ApprovalNote"/> when <paramref name="approve"/> is set; any other interruption cancels the task and
    /// fails the test. The task log is attached to the result.
    /// </summary>
    /// <param name="project">Project slug.</param>
    /// <param name="runbook">Runbook name.</param>
    /// <param name="environment">Environment name.</param>
    /// <param name="promptedVariables">Prompted variables by name, or <c>null</c>.</param>
    /// <param name="timeout">Longest wait for the task.</param>
    /// <param name="approve"><c>true</c> to answer manual interventions.</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    /// <returns>The finished task and its log.</returns>
    protected async Task<RunbookOutcome> RunRunbookAsync(
        string project,
        string runbook,
        string environment,
        IReadOnlyDictionary<string, string>? promptedVariables,
        TimeSpan timeout,
        bool approve,
        CancellationToken cancellationToken)
    {
        var run = await Octopus.StartRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = project,
                Runbook = runbook,
                Environment = environment,
                PromptedVariables = promptedVariables ?? new Dictionary<string, string>(),
                Comments = $"{ApprovalNote} ({TestContext.CurrentContext.Test.MethodName})",
            },
            cancellationToken);
        Cleanup.Register($"cancel {runbook} task {run.TaskId} in {environment} if it still runs", token => OctopusExtras.CancelIfRunningAsync(run.TaskId, token));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        OctopusTask task;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            task = await Octopus.WaitForTaskAsync(run.TaskId, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1), OctopusTaskWait.CompletedOrPendingInterruption, cancellationToken);
            if (task.IsCompleted)
            {
                break;
            }

            var pending = (await Octopus.GetPendingInterruptionsAsync(run.TaskId, cancellationToken)).Where(item => item.IsPending && !item.IsAnsweredBySystem && !answered.Contains(item.Id)).ToArray();
            if (pending.Length == 0)
            {
                await Task.Delay(Settings.TimeLimits.PollInterval, cancellationToken);
                continue;
            }

            foreach (var interruption in pending)
            {
                await AnswerAsync(runbook, environment, run.TaskId, interruption, approve, cancellationToken);
                answered.Add(interruption.Id);
            }
        }

        var log = await Octopus.GetTaskLogAsync(run.TaskId, cancellationToken);
        AttachArtifact($"{runbook}-{environment}-{run.TaskId}.log", log);
        return new RunbookOutcome(runbook, environment, task, log);
    }

    /// <summary>
    /// Runs a <c>platform-infrastructure</c> runbook in the tier's infra environment and requires it to succeed. Every
    /// runbook but env-sleep may start the cluster (env-wake, or the wake step of env-plan, env-apply, env-destroy,
    /// apps-* and rotate-db-passwords), so they first wait out the stop grace after a stop of this run (E50).
    /// </summary>
    /// <param name="runbook">Runbook name.</param>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="promptedVariables">Prompted variables, or <c>null</c>.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="approve"><c>true</c> to answer manual interventions.</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    protected async Task<RunbookOutcome> RunInfrastructureRunbookAsync(
        string runbook,
        PlatformTier tier,
        IReadOnlyDictionary<string, string>? promptedVariables,
        TimeSpan timeout,
        bool approve,
        CancellationToken cancellationToken)
    {
        if (runbook != "env-sleep")
        {
            await ClusterStopGrace.WaitAsync(tier, cancellationToken);
        }

        var outcome = await RunRunbookAsync(AzurePlatform.InfrastructureProject, runbook, AzurePlatform.InfraEnvironment(tier), promptedVariables, timeout, approve, cancellationToken);
        outcome.Task.FinishedSuccessfully.ShouldBeTrue($"{outcome}: {outcome.Task.ErrorMessage}");
        return outcome;
    }

    /// <summary>Runs env-wake (idempotent: starts a stopped cluster, disables apr-sleep-&lt;tier&gt;, waits for the workers).</summary>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    protected async Task WakeAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        await RunInfrastructureRunbookAsync("env-wake", tier, null, Settings.TimeLimits.WakeTimeout, approve: false, cancellationToken);
        await WaitForPowerStateAsync(tier, running: true, cancellationToken);
    }

    /// <summary>Runs env-wake only when the tier's cluster is not running.</summary>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    protected async Task EnsureAwakeAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var state = await ClusterStateAsync(tier, cancellationToken);
        if (state.IsRunning && string.Equals(state.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await WakeAsync(tier, cancellationToken);
    }

    /// <summary>
    /// Runs env-sleep (forced or by its normal rules), requires it to succeed and returns its decision. When it decides to
    /// sleep, it waits for the cluster to stop and records the stop, so the next start waits out the grace (E50). Tests
    /// never force prod (<paramref name="force"/> is refused there).
    /// </summary>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="force"><c>Sleep.Force</c>: skip the working-window and idle rules (never the busy rule).</param>
    /// <param name="cancellationToken">Cancels the calls and waits.</param>
    protected async Task<SleepDecision> SleepAsync(PlatformTier tier, bool force, CancellationToken cancellationToken)
    {
        if (force && tier == PlatformTier.Prod)
        {
            throw new InvalidOperationException("Tests never force-sleep prod; env-sleep's own rules decide there.");
        }

        var outcome = await RunInfrastructureRunbookAsync(
            "env-sleep",
            tier,
            new Dictionary<string, string> { ["Sleep.Force"] = force ? "true" : "false", ["Sleep.DryRun"] = "false" },
            Settings.TimeLimits.RunbookTimeout,
            approve: false,
            cancellationToken);
        var decision = RunbookLogs.SleepDecision(outcome.Log);
        decision.ShouldNotBeNull($"{outcome} logged no Sleep.Decision line");
        if (decision.Sleeps && string.Equals((await WaitForPowerStateAsync(tier, running: false, cancellationToken)).PowerState, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            ClusterStopGrace.RecordStop(tier, DateTimeOffset.UtcNow);
        }

        return decision;
    }

    /// <summary>Waits until the tier's cluster runs (or is stopped) and returns its last observed state.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="running"><c>true</c> to wait for Running, <c>false</c> for Stopped.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    protected Task<AksClusterState> WaitForPowerStateAsync(PlatformTier tier, bool running, CancellationToken cancellationToken) =>
        ObserveAsync(
            token => ClusterStateAsync(tier, token),
            state => running
                ? state.IsRunning && string.Equals(state.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase)
                : string.Equals(state.PowerState, "Stopped", StringComparison.OrdinalIgnoreCase) && !string.Equals(state.ProvisioningState, "Stopping", StringComparison.OrdinalIgnoreCase),
            Settings.TimeLimits.WakeTimeout,
            cancellationToken);

    /// <summary>
    /// Polls <paramref name="probe"/> until <paramref name="condition"/> holds or <paramref name="timeout"/> passes and returns the
    /// last value either way, so the caller asserts on it with a readable message. Transient failures (by default those of
    /// the fixture app and of HTTP) are retried until the deadline.
    /// </summary>
    /// <typeparam name="T">Observed type.</typeparam>
    /// <param name="probe">One observation.</param>
    /// <param name="condition">Ends the wait.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="retryWhen">Failures to retry; <see cref="SandboxApp.IsTransient"/> when omitted.</param>
    protected async Task<T> ObserveAsync<T>(Func<CancellationToken, Task<T>> probe, Func<T, bool> condition, TimeSpan timeout, CancellationToken cancellationToken, Func<Exception, bool>? retryWhen = null)
    {
        retryWhen ??= SandboxApp.IsTransient;
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await probe(cancellationToken);
                if (condition(value) || DateTimeOffset.UtcNow >= deadline)
                {
                    return value;
                }
            }
            catch (Exception ex) when (retryWhen(ex) && ex is not ResultStateException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new PollTimeoutException("an observation that kept failing", timeout, timeout, 0, null, lastError);
                }
            }

            await Task.Delay(Settings.TimeLimits.PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until a backup CronJob has run its schedule's latest occurrence (a cluster that slept through it runs it at the
    /// wake) and no Job is active, then returns it. A CronJob created after that occurrence has nothing to catch up.
    /// </summary>
    /// <param name="cluster">The cluster of the CronJob.</param>
    /// <param name="name">CronJob name in platform-backup.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    protected async Task<BackupCronJob> WaitForBackupCatchUpAsync(IKubernetesApi cluster, string name, CancellationToken cancellationToken)
    {
        var first = await BackupSchedule.ReadAsync(cluster, name, cancellationToken);
        var due = first.LastDailyOccurrence(DateTimeOffset.UtcNow);
        var mustCatchUp = due is not null && first.Created < due && !first.Suspended;
        return await ObserveAsync(
            token => BackupSchedule.ReadAsync(cluster, name, token),
            observed => observed.ActiveJobs == 0 && (!mustCatchUp || observed.LastScheduleTime >= due),
            TimeSpan.FromMinutes(60),
            cancellationToken);
    }

    /// <summary>The apps domain of a tier from its static ingress IP.</summary>
    /// <param name="tier">An app-cluster tier.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    protected async Task<string> AppsDomainAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        var address = await Arm.GetPublicIpAddressAsync(AzurePlatform.SharedGroup(tier), AzurePlatform.IngressAddress(tier), cancellationToken);
        address.ShouldNotBeNull($"{AzurePlatform.IngressAddress(tier)} in {AzurePlatform.SharedGroup(tier)} has no address");
        return AzurePlatform.AppsDomain(address);
    }

    /// <summary>A client of the fixture app in an environment, disposed with the fixture.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="cancellationToken">Cancels the lookup of the apps domain.</param>
    protected async Task<SandboxApp> SandboxAsync(string environment, CancellationToken cancellationToken)
    {
        var sandbox = new SandboxApp(environment, await AppsDomainAsync(AzurePlatform.TierOf(environment), cancellationToken), Settings.TimeLimits.HttpTimeout);
        Cleanup.Register($"dispose the client of {sandbox.BaseUri}", _ =>
        {
            sandbox.Dispose();
            return Task.CompletedTask;
        });
        return sandbox;
    }

    /// <summary>Reads the canary until it is readable (the app and its database answer) and returns it.</summary>
    /// <param name="sandbox">The fixture app.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    protected Task<SandboxCanary?> ReadCanaryAsync(SandboxApp sandbox, TimeSpan timeout, CancellationToken cancellationToken) =>
        ObserveAsync(sandbox.GetCanaryAsync, _ => true, timeout, cancellationToken);

    /// <summary>
    /// A bare pod for a server-side dry run: restricted Pod Security, small requests and limits, the run label, no
    /// service account token. Admission control evaluates it; nothing is stored or scheduled.
    /// </summary>
    /// <param name="namespaceName">Target namespace, for example <c>sandbox-prod</c>.</param>
    /// <param name="purpose">Short purpose used in the name.</param>
    /// <param name="image">Container image.</param>
    /// <param name="environment">Container environment variables; a <c>null</c> value is left out.</param>
    protected V1Pod DryRunPod(string namespaceName, string purpose, string image, IReadOnlyDictionary<string, string?>? environment = null) =>
        new()
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = Run.ResourceName(purpose),
                NamespaceProperty = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["conformance-run"] = Run.RunId,
                    ["app.kubernetes.io/name"] = "conformance-probe",
                },
            },
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
                RestartPolicy = "Never",
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = 10001,
                    RunAsGroup = 10001,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
                },
                Containers =
                [
                    new V1Container
                    {
                        Name = "probe",
                        Image = image,
                        Env = environment?.Where(pair => pair.Value is not null).Select(pair => new V1EnvVar { Name = pair.Key, Value = pair.Value }).ToList(),
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") },
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") },
                        },
                        SecurityContext = new V1SecurityContext
                        {
                            AllowPrivilegeEscalation = false,
                            ReadOnlyRootFilesystem = true,
                            RunAsNonRoot = true,
                            Capabilities = new V1Capabilities { Drop = ["ALL"] },
                        },
                    },
                ],
            },
        };

    /// <summary>
    /// Makes a dry run's outcome an admission decision: an RBAC refusal or an exhausted quota says nothing about the
    /// policies, so it turns the test Inconclusive with what to grant or free.
    /// </summary>
    /// <param name="result">The dry run's result.</param>
    /// <param name="namespaceName">Target namespace.</param>
    /// <exception cref="PlatformPrerequisiteException">The API server refused for another reason than admission policy.</exception>
    protected static void RequireAdmissionDecision(PodCreationResult result, string namespaceName)
    {
        ArgumentNullException.ThrowIfNull(result);
        var message = result.Message ?? string.Empty;
        if (!result.Created && result.StatusCode == (int)HttpStatusCode.Forbidden && message.Contains("cannot create resource", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformPrerequisiteException(
                $"The conformance principal may not create pods in {namespaceName} (§7.0: AKS RBAC Writer on sandbox-<env>, or the interim cluster admin): {message}");
        }

        if (!result.Created && message.Contains("exceeded quota", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformPrerequisiteException($"The quota of {namespaceName} is exhausted, so the dry run says nothing about admission: {message}");
        }
    }

    /// <summary>
    /// Rebuilds the nonprod tier (docs/runbooks/conformance.md, CAP-AZ-007): env-destroy, env-apply with a fresh worker
    /// registration token, then apps-apply for the sandbox, each approved as <see cref="ApprovalNote"/>. Nothing is
    /// destroyed unless env-apply prompts <c>Octopus.WorkerRegistrationToken</c> and Octopus issues a token, because
    /// the workers of k8s-tdd and k8s-uat cannot register again without one. Called once per run through
    /// <see cref="NonProdRebuild"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the runs and waits.</param>
    protected internal async Task<RebuildOutcome> RebuildNonProdAsync(CancellationToken cancellationToken)
    {
        const PlatformTier tier = PlatformTier.NonProd;
        const string tokenVariable = "Octopus.WorkerRegistrationToken";
        var rebuildTimeout = Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.WakeTimeout;
        var prompted = await OctopusExtras.PromptedVariablesAsync(AzurePlatform.InfrastructureProject, "env-apply", AzurePlatform.InfraEnvironment(tier), cancellationToken);
        if (!prompted.Contains(tokenVariable, StringComparer.OrdinalIgnoreCase))
        {
            throw new PlatformPrerequisiteException(
                $"env-apply does not prompt {tokenVariable} (prompted: {string.Join(", ", prompted)}), so a rebuild could not register the "
                + "Kubernetes workers again; nothing was destroyed. Make it a prompted, sensitive, optional variable of env-apply.");
        }

        if (await OctopusExtras.CreateWorkerRegistrationTokenAsync(cancellationToken) is null)
        {
            throw new PlatformPrerequisiteException("Octopus issued no worker registration token (/api/users/access-token); nothing was destroyed.");
        }

        var issuerBefore = await Arm.GetClusterIssuerAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken);
        var ingressBefore = await Arm.GetPublicIpAddressAsync(AzurePlatform.SharedGroup(tier), AzurePlatform.IngressAddress(tier), cancellationToken);

        var destroy = await RunInfrastructureRunbookAsync("env-destroy", tier, null, rebuildTimeout, approve: true, cancellationToken);
        var issuerBetween = await Arm.GetClusterIssuerAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken);
        var token = await OctopusExtras.CreateWorkerRegistrationTokenAsync(cancellationToken)
            ?? throw new InvalidOperationException("Octopus issued no worker registration token after env-destroy; run env-apply by hand with a fresh token.");
        var apply = await RunInfrastructureRunbookAsync("env-apply", tier, new Dictionary<string, string> { [tokenVariable] = token }, rebuildTimeout, approve: true, cancellationToken);
        var appsApply = await RunInfrastructureRunbookAsync("apps-apply", tier, new Dictionary<string, string> { ["App.Name"] = AzurePlatform.Sandbox }, Settings.TimeLimits.RunbookTimeout + Settings.TimeLimits.WakeTimeout, approve: true, cancellationToken);
        var issuerAfter = await Arm.GetClusterIssuerAsync(AzurePlatform.ClusterGroup(tier), AzurePlatform.ClusterName(tier), cancellationToken);
        var ingressAfter = await Arm.GetPublicIpAddressAsync(AzurePlatform.SharedGroup(tier), AzurePlatform.IngressAddress(tier), cancellationToken);
        return new RebuildOutcome(destroy, apply, appsApply, issuerBefore, issuerBetween, issuerAfter, ingressBefore, ingressAfter);
    }

    private async Task AnswerAsync(string runbook, string environment, string taskId, OctopusInterruption interruption, bool approve, CancellationToken cancellationToken)
    {
        var manual = string.Equals(interruption.Type, "ManualIntervention", StringComparison.OrdinalIgnoreCase);
        if (!approve || !manual)
        {
            var log = await Octopus.GetTaskLogAsync(taskId, cancellationToken);
            AttachArtifact($"{runbook}-{environment}-{taskId}-interrupted.log", log);
            await OctopusExtras.CancelIfRunningAsync(taskId, cancellationToken);
            Assert.Fail($"{runbook} in {environment} ({taskId}) stopped at '{interruption.Title}' ({interruption.Type}); the task was cancelled. See the attached log.");
        }

        if (!interruption.CanTakeResponsibility && !interruption.HasResponsibility)
        {
            await OctopusExtras.CancelIfRunningAsync(taskId, cancellationToken);
            throw new PlatformPrerequisiteException(
                $"The automation user cannot answer '{interruption.Title}' of {runbook} in {environment} (responsible teams: {string.Join(", ", interruption.ResponsibleTeamIds)}); "
                + "the task was cancelled. Add AISF-Service-Account to a responsible team (octopus/terraform teams.tf).");
        }

        TestContext.Out.WriteLine($"{runbook} in {environment}: answering '{interruption.Title}' with {ApprovalNote}");
        await Octopus.ApproveInterruptionAsync(interruption.Id, ApprovalNote, cancellationToken);
    }
}

/// <summary>A finished runbook run.</summary>
/// <param name="Runbook">Runbook name.</param>
/// <param name="Environment">Environment name.</param>
/// <param name="Task">The task as last observed.</param>
/// <param name="Log">The raw task log.</param>
public sealed record RunbookOutcome(string Runbook, string Environment, OctopusTask Task, string Log)
{
    /// <summary>Compact summary for messages.</summary>
    public override string ToString() => $"{Runbook} in {Environment} ({Task})";
}
