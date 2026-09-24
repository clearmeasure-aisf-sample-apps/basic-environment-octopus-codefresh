using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Octopus;

/// <summary>A release of the sandbox app as the Octopus capability tests use it.</summary>
/// <param name="Id">Release ID.</param>
/// <param name="Version">Release version.</param>
/// <param name="Packages">Selected package versions by package reference (image) name.</param>
public sealed record SandboxRelease(string Id, string Version, IReadOnlyDictionary<string, string> Packages);

/// <summary>
/// Shared steps of the Octopus capability tests (CAP-OCT-001 to CAP-OCT-015). Every deployment targets the sandbox app,
/// the conformance fixture; interventions are answered by the automation user with the reason
/// <c>conformance:&lt;run-id&gt;</c>, which <c>platform-sod-guard</c> accepts while <c>Platform.InterventionTestMode</c> is
/// <c>true</c>. Conformance deployments run one at a time: a waking deployment holds 3 of the instance's 5 task slots.
/// </summary>
public abstract class OctopusCapabilityTestBase : PlatformTestBase
{
    /// <summary>The conformance fixture app and its Octopus project.</summary>
    protected const string SandboxProject = "sandbox";

    /// <summary>Platform runbooks project (env-wake, env-sleep).</summary>
    protected const string InfrastructureProject = "platform-infrastructure";

    /// <summary>Project deployed by the first step of every app deployment to wake its cluster.</summary>
    protected const string WakeProject = "platform-wake";

    /// <summary>The sandbox deployable whose pins the Argo CD step writes.</summary>
    protected const string SandboxDeployable = "app";

    private static int releaseSequence;
    private OctopusRest? rest;

    /// <summary>The intervention reason of this run.</summary>
    protected string Reason => $"conformance:{Run.RunId}";

    /// <summary>The cancellation token of the current test (<c>[CancelAfter]</c>).</summary>
    protected static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

    /// <summary>Infrastructure environment of a tier.</summary>
    /// <param name="tier">NonProd or Prod.</param>
    protected static string InfraEnvironment(PlatformTier tier) => tier == PlatformTier.Prod ? "infra-prod" : "infra-nonprod";

    /// <summary>Path of a sandbox pin file in the environment repository.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    protected static string PinPath(string environment) => $"gitops/apps/{SandboxProject}/envs/{environment}/{SandboxDeployable}/kustomization.yaml";

    /// <summary>Checks the Octopus prerequisites (and GitHub ones when asked) and returns the raw REST helper.</summary>
    /// <param name="purpose">What needs them, for the Inconclusive message.</param>
    /// <param name="gitHub">Also require <c>EnvRepo</c> and <c>GITHUB_TOKEN</c>.</param>
    protected OctopusRest Rest(string purpose, bool gitHub = false)
    {
        var check = Settings.Check(purpose)
            .Setting(nameof(Settings.OctopusUrl), Settings.OctopusUrl)
            .Setting(nameof(Settings.OctopusSpaceId), Settings.OctopusSpaceId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, Settings.Secrets.OctopusApiKey);
        if (gitHub)
        {
            check.Setting(nameof(Settings.EnvRepo), Settings.EnvRepo).Secret(EnvironmentVariableNames.GitHubToken, Settings.Secrets.GitHubToken);
        }

        check.ThrowIfMissing();
        if (rest is null)
        {
            rest = new OctopusRest(Settings);
            var created = rest;
            Cleanup.Register("dispose the Octopus REST helper", _ =>
            {
                created.Dispose();
                return Task.CompletedTask;
            });
        }

        return rest;
    }

    /// <summary>Checks and returns the cluster settings of a tier.</summary>
    /// <param name="tier">NonProd or Prod.</param>
    /// <param name="purpose">What needs them.</param>
    protected PlatformTierSettings RequireTier(PlatformTier tier, string purpose)
    {
        var settings = Settings.Tier(tier);
        Settings.Check(purpose)
            .Setting($"Tiers.{tier.ToKey()}.ResourceGroup", settings.ResourceGroup)
            .Setting($"Tiers.{tier.ToKey()}.ClusterName", settings.ClusterName)
            .Setting(nameof(Settings.AzureSubscriptionId), Settings.AzureSubscriptionId)
            .ThrowIfMissing();
        return settings;
    }

    /// <summary>ID of an Octopus environment, failing when it does not exist.</summary>
    /// <param name="name">Environment name.</param>
    protected async Task<string> EnvironmentIdAsync(string name) =>
        (await Octopus.FindEnvironmentByNameAsync(name, Token))?.Id ?? throw new InvalidOperationException($"Octopus environment {name} does not exist");

    /// <summary>Creates a sandbox release on a channel from <c>refs/heads/main</c> with the latest image versions.</summary>
    /// <param name="channel">Channel name: Default or Strict.</param>
    protected async Task<SandboxRelease> CreateSandboxReleaseAsync(string channel)
    {
        var sequence = Interlocked.Increment(ref releaseSequence);
        var created = await Octopus.CreateReleaseAsync(
            new OctopusReleaseRequest
            {
                ProjectName = SandboxProject,
                ReleaseVersion = $"0.0.0-conf.{Run.RunId}.{sequence}",
                ChannelName = channel,
                GitRef = OctopusRunbookRunRequest.MainBranch,
                ReleaseNotes = $"Conformance run {Run.RunId} ({Reason}); channel {channel}.",
            },
            Token);
        TestContext.Out.WriteLine($"created {SandboxProject} release {created.ReleaseVersion} ({created.ReleaseId}) on channel {channel}");
        return await ReadReleaseAsync(created.ReleaseId);
    }

    /// <summary>Reads a release and its selected packages.</summary>
    /// <param name="releaseId">Release ID.</param>
    protected async Task<SandboxRelease> ReadReleaseAsync(string releaseId)
    {
        var release = await Rest("reading a sandbox release").GetReleaseAsync(releaseId, Token);
        return new SandboxRelease(releaseId, release.GetProperty("Version").GetString()!, OctopusRest.SelectedPackages(release));
    }

    /// <summary>The task of the newest deployment of a release to an environment, or <c>null</c>.</summary>
    /// <param name="releaseId">Release ID.</param>
    /// <param name="environmentId">Environment ID.</param>
    protected async Task<string?> FindDeploymentTaskAsync(string releaseId, string environmentId) =>
        (await Rest("reading deployments").GetReleaseDeploymentsAsync(releaseId, Token))
            .Where(deployment => deployment.GetProperty("EnvironmentId").GetString() == environmentId)
            .Select(deployment => deployment.GetProperty("TaskId").GetString())
            .FirstOrDefault();

    /// <summary>
    /// The newest sandbox release (of <paramref name="channel"/>, when given) with a successful deployment to
    /// <paramref name="environment"/>, among the newest 30 releases; <c>null</c> when none.
    /// </summary>
    /// <param name="environment">Environment name.</param>
    /// <param name="channel">Channel name, or <c>null</c> for any.</param>
    /// <param name="except">Release IDs to skip.</param>
    protected async Task<SandboxRelease?> FindDeployedReleaseAsync(string environment, string? channel = null, IReadOnlyCollection<string>? except = null)
    {
        var project = await Octopus.GetProjectAsync(SandboxProject, Token);
        var environmentId = await EnvironmentIdAsync(environment);
        var channelId = channel is null ? null : await ChannelIdAsync(project.Id, channel);
        foreach (var release in await Rest("reading sandbox releases").GetReleasesAsync(project.Id, 30, Token))
        {
            var releaseId = release.GetProperty("Id").GetString()!;
            if (except?.Contains(releaseId) == true || (channelId is not null && release.GetProperty("ChannelId").GetString() != channelId))
            {
                continue;
            }

            foreach (var deployment in await Rest("reading deployments").GetReleaseDeploymentsAsync(releaseId, Token))
            {
                if (deployment.GetProperty("EnvironmentId").GetString() != environmentId)
                {
                    continue;
                }

                var task = await Octopus.GetTaskAsync(deployment.GetProperty("TaskId").GetString()!, Token);
                if (task.FinishedSuccessfully)
                {
                    return await ReadReleaseAsync(releaseId);
                }
            }
        }

        return null;
    }

    /// <summary>The release of the newest successful sandbox deployment to an environment; <c>null</c> when none.</summary>
    /// <param name="environment">Environment name.</param>
    protected async Task<SandboxRelease?> LastDeployedReleaseAsync(string environment)
    {
        var project = await Octopus.GetProjectAsync(SandboxProject, Token);
        var environmentId = await EnvironmentIdAsync(environment);
        foreach (var deployment in await Rest("reading deployments").GetDeploymentsAsync(project.Id, environmentId, 20, Token))
        {
            var task = await Octopus.GetTaskAsync(deployment.GetProperty("TaskId").GetString()!, Token);
            if (task.FinishedSuccessfully)
            {
                return await ReadReleaseAsync(deployment.GetProperty("ReleaseId").GetString()!);
            }
        }

        return null;
    }

    /// <summary>
    /// The release running in an environment and the newest earlier release with different images. Inconclusive when
    /// the environment has seen only one image version (the nightly sandbox release commit adds one per run).
    /// </summary>
    /// <param name="environment">Environment name.</param>
    protected async Task<(SandboxRelease Current, SandboxRelease Previous)> CurrentAndPreviousAsync(string environment)
    {
        var current = await LastDeployedReleaseAsync(environment);
        if (current is null)
        {
            Assert.Inconclusive($"no sandbox release has been deployed to {environment} yet (P1-11)");
        }

        var skipped = new List<string> { current!.Id };
        while (true)
        {
            var candidate = await FindDeployedReleaseAsync(environment, null, skipped);
            if (candidate is null)
            {
                Assert.Inconclusive($"{environment} has seen only one sandbox image version; a rollback needs two");
            }

            if (!candidate!.Packages.OrderBy(pair => pair.Key).SequenceEqual(current.Packages.OrderBy(pair => pair.Key)))
            {
                return (current, candidate);
            }

            skipped.Add(candidate.Id);
        }
    }

    /// <summary>
    /// A sandbox release of <paramref name="channel"/> that may deploy to prod: the newest one already deployed to uat, or
    /// a new release taken through tdd (automatic) and uat now.
    /// </summary>
    /// <param name="channel">Channel name.</param>
    protected async Task<SandboxRelease> ReleaseReadyForProdAsync(string channel)
    {
        var existing = await FindDeployedReleaseAsync("uat", channel);
        if (existing is not null)
        {
            return existing;
        }

        var release = await CreateSandboxReleaseAsync(channel);
        await CompleteAsync(await WaitForAutomaticDeploymentAsync(release, "tdd"), "tdd");
        await DeployAndCompleteAsync(release, "uat");
        return release;
    }

    /// <summary>Waits for the lifecycle to start the deployment of a new release to an environment; returns its task ID.</summary>
    /// <param name="release">The release.</param>
    /// <param name="environment">Environment of an automatic phase.</param>
    protected async Task<string> WaitForAutomaticDeploymentAsync(SandboxRelease release, string environment)
    {
        var environmentId = await EnvironmentIdAsync(environment);
        var taskId = await Poll.UntilAsync(
            _ => FindDeploymentTaskAsync(release.Id, environmentId),
            found => found is not null,
            TimeSpan.FromMinutes(5),
            Settings.TimeLimits.PollInterval,
            $"the automatic {environment} deployment of {SandboxProject} {release.Version}",
            cancellationToken: Token);
        return taskId!;
    }

    /// <summary>Deploys a release to an environment and completes it, answering interventions with <see cref="Reason"/>.</summary>
    /// <param name="release">The release.</param>
    /// <param name="environment">Environment name.</param>
    /// <returns>The final task.</returns>
    protected async Task<OctopusTask> DeployAndCompleteAsync(SandboxRelease release, string environment)
    {
        var deployment = await DeployAsync(release, environment);
        return await CompleteAsync(deployment.TaskId, environment);
    }

    /// <summary>Starts a deployment of a release to one environment.</summary>
    /// <param name="release">The release.</param>
    /// <param name="environment">Environment name.</param>
    protected async Task<OctopusDeploymentTask> DeployAsync(SandboxRelease release, string environment) =>
        (await Octopus.DeployReleaseAsync(
            new OctopusDeploymentRequest { ProjectName = SandboxProject, ReleaseVersion = release.Version, EnvironmentNames = [environment] },
            Token)).Single();

    /// <summary>
    /// Waits for a deployment task to finish, answering each pending intervention with <paramref name="answer"/>
    /// (default: approve with <see cref="Reason"/>). The task log is attached to the test result.
    /// </summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="label">Label for messages and the artifact name.</param>
    /// <param name="answer">Answers one interruption; returns the notes to approve with.</param>
    /// <param name="requireSuccess">Fail the test when the task does not succeed.</param>
    protected async Task<OctopusTask> CompleteAsync(string taskId, string label, Func<OctopusInterruption, string>? answer = null, bool requireSuccess = true)
    {
        while (true)
        {
            var task = await Octopus.WaitForTaskAsync(taskId, Settings.TimeLimits.DeploymentTimeout, OctopusTaskWait.CompletedOrPendingInterruption, Token);
            if (task.IsCompleted)
            {
                AttachArtifact($"{SafeName(label)}-{taskId}.log", await Octopus.GetTaskLogAsync(taskId, Token));
                if (requireSuccess)
                {
                    task.FinishedSuccessfully.ShouldBeTrue($"{label}: task {task} did not succeed");
                }

                return task;
            }

            foreach (var interruption in await Octopus.GetPendingInterruptionsAsync(taskId, Token))
            {
                var notes = answer?.Invoke(interruption) ?? Reason;
                TestContext.Out.WriteLine($"{label}: answering '{interruption.Title}' with '{notes}'");
                await Octopus.ApproveInterruptionAsync(interruption.Id, notes, Token);
            }
        }
    }

    /// <summary>Waits until a task pauses at an intervention with the given title and returns it.</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="title">Intervention title (the step name), for example <c>Prod go/no-go</c>.</param>
    protected async Task<OctopusInterruption> WaitForInterventionAsync(string taskId, string title)
    {
        var task = await Octopus.WaitForTaskAsync(taskId, Settings.TimeLimits.DeploymentTimeout, OctopusTaskWait.CompletedOrPendingInterruption, Token);
        task.IsCompleted.ShouldBeFalse($"task {task} ended instead of waiting for '{title}'");
        var pending = await Octopus.GetPendingInterruptionsAsync(taskId, Token);
        return pending.SingleOrDefault(interruption => string.Equals(interruption.Title, title, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"task {taskId} waits for [{string.Join(", ", pending.Select(item => item.Title))}], not '{title}'");
    }

    /// <summary>Cancels a task at teardown unless it has completed.</summary>
    /// <param name="taskId">Task ID.</param>
    /// <param name="label">What it is.</param>
    protected void CancelAtTeardown(string taskId, string label)
    {
        var helper = Rest("cancelling tasks");
        Cleanup.Register($"cancel {label} ({taskId})", async token =>
        {
            var task = await Octopus.GetTaskAsync(taskId, token);
            if (!task.IsCompleted)
            {
                await helper.CancelTaskAsync(taskId, token);
            }
        });
    }

    /// <summary>ID of a channel of a project by name.</summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="channel">Channel name.</param>
    protected async Task<string> ChannelIdAsync(string projectId, string channel)
    {
        var channels = await Rest("reading channels").GetAsync($"/api/{Settings.OctopusSpaceId}/projects/{projectId}/channels?take=50", Token);
        return channels.GetProperty("Items").EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("Name").GetString(), channel, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GetProperty("Id").GetString())
            .FirstOrDefault() ?? throw new InvalidOperationException($"project {projectId} has no channel {channel}");
    }

    /// <summary>Runs env-sleep with <c>Sleep.Force</c> in the tier's infrastructure environment and waits for the cluster to stop.</summary>
    /// <param name="tier">NonProd or Prod.</param>
    protected async Task<OctopusRunbookRunResult> ForceSleepAsync(PlatformTier tier)
    {
        var cluster = RequireTier(tier, "stopping a cluster");
        var run = await Octopus.RunRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = InfrastructureProject,
                Runbook = "env-sleep",
                Environment = InfraEnvironment(tier),
                PromptedVariables = new Dictionary<string, string> { ["Sleep.Force"] = "True" },
                Comments = $"Conformance run {Run.RunId}: force-sleep",
            },
            Settings.TimeLimits.RunbookTimeout,
            Token);
        AttachArtifact($"env-sleep-{tier.ToKey()}-{run.Task.Id}.log", await Octopus.GetTaskLogAsync(run.Task.Id, Token));
        run.Task.FinishedSuccessfully.ShouldBeTrue($"env-sleep in {InfraEnvironment(tier)}: {run.Task}");
        await Poll.UntilAsync(
            async token => (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, token)).PowerState == "Stopped",
            Settings.TimeLimits.WakeTimeout,
            TimeSpan.FromSeconds(30),
            $"{cluster.ClusterName} to stop",
            cancellationToken: Token);
        return run;
    }

    /// <summary>Runs env-wake in the tier's infrastructure environment and waits for the cluster to run.</summary>
    /// <param name="tier">NonProd or Prod.</param>
    protected async Task<OctopusRunbookRunResult> WakeAsync(PlatformTier tier)
    {
        var cluster = RequireTier(tier, "starting a cluster");
        var run = await Octopus.RunRunbookAsync(
            new OctopusRunbookRunRequest
            {
                Project = InfrastructureProject,
                Runbook = "env-wake",
                Environment = InfraEnvironment(tier),
                Comments = $"Conformance run {Run.RunId}: wake",
            },
            Settings.TimeLimits.WakeTimeout,
            Token);
        AttachArtifact($"env-wake-{tier.ToKey()}-{run.Task.Id}.log", await Octopus.GetTaskLogAsync(run.Task.Id, Token));
        run.Task.FinishedSuccessfully.ShouldBeTrue($"env-wake in {InfraEnvironment(tier)}: {run.Task}");
        (await Azure.GetClusterStateAsync(cluster.ResourceGroup!, cluster.ClusterName!, Token)).IsRunning.ShouldBeTrue($"{cluster.ClusterName} runs after env-wake");
        return run;
    }

    /// <summary>The <c>newTag</c> of each image in a Kustomize pin file, by image name under <c>apps/sandbox/</c>.</summary>
    /// <param name="content">Content of kustomization.yaml.</param>
    protected static IReadOnlyDictionary<string, string> PinnedTags(string content)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? image = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- name:", StringComparison.Ordinal) || line.StartsWith("name:", StringComparison.Ordinal))
            {
                var value = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Trim('"');
                image = value.Contains("/apps/", StringComparison.Ordinal) ? value[(value.LastIndexOf('/') + 1)..] : value;
            }
            else if (line.StartsWith("newTag:", StringComparison.Ordinal) && image is not null)
            {
                tags[image] = line["newTag:".Length..].Trim().Trim('"');
            }
        }

        return tags;
    }

    /// <summary>
    /// The value of a variable for one environment: the value scoped to that environment, else the unscoped one;
    /// <c>null</c> when neither exists.
    /// </summary>
    /// <param name="variables">Variable set.</param>
    /// <param name="name">Variable name.</param>
    /// <param name="environmentId">Environment ID.</param>
    protected static string? VariableValue(OctopusVariableSet variables, string name, string environmentId)
    {
        var candidates = variables.Variables.Where(variable => variable.Name == name).ToArray();
        return candidates.FirstOrDefault(variable => variable.Scope.TryGetValue("Environment", out var environments) && environments.Contains(environmentId))?.Value
            ?? candidates.FirstOrDefault(variable => variable.Scope.Count == 0)?.Value;
    }

    /// <summary>ID of a space team by exact name.</summary>
    /// <param name="name">Team name, for example <c>Prod Approvers</c>.</param>
    protected async Task<string> TeamIdAsync(string name)
    {
        var page = await Rest("reading teams").GetAsync($"/api/{Settings.OctopusSpaceId}/teams?partialName={Uri.EscapeDataString(name)}&take=50", Token);
        return page.GetProperty("Items").EnumerateArray()
            .Where(item => item.GetProperty("Name").GetString() == name)
            .Select(item => item.GetProperty("Id").GetString())
            .FirstOrDefault() ?? throw new InvalidOperationException($"team {name} does not exist");
    }

    /// <summary>
    /// Base URL of the sandbox in an environment: <c>https://sandbox-&lt;env&gt;.&lt;apps-domain-&lt;tier&gt;&gt;</c>, with the domain
    /// from <c>Platform.AppsDomain</c> of library variable set <c>Platform Environment</c>.
    /// </summary>
    /// <param name="environment">tdd, uat or prod.</param>
    protected async Task<Uri> SandboxBaseUrlAsync(string environment)
    {
        var set = await Rest("reading Platform Environment").FindLibraryVariableSetAsync("Platform Environment", Token)
            ?? throw new InvalidOperationException("library variable set Platform Environment does not exist");
        var variables = await Octopus.GetVariableSetAsync(set.GetProperty("VariableSetId").GetString()!, Token);
        var domain = VariableValue(variables, "Platform.AppsDomain", await EnvironmentIdAsync(environment));
        if (string.IsNullOrWhiteSpace(domain))
        {
            Assert.Inconclusive($"Platform.AppsDomain has no value for {environment} yet (octopus/terraform sets it after env-apply)");
        }

        return new Uri($"https://{SandboxProject}-{environment}.{domain}/");
    }

    /// <summary>Recent tasks of platform-infrastructure in an infrastructure environment whose description names a runbook.</summary>
    /// <param name="infraEnvironment">infra-nonprod or infra-prod.</param>
    /// <param name="runbook">Runbook slug, for example <c>env-wake</c>.</param>
    /// <param name="take">How many tasks to read.</param>
    protected async Task<IReadOnlyList<OctopusTask>> RunbookTasksAsync(string infraEnvironment, string runbook, int take = 30) =>
        (await Octopus.GetTasksAsync(new OctopusTaskQuery { Project = InfrastructureProject, Environment = infraEnvironment, Take = take }, Token))
            .Where(task => task.Description?.Contains(runbook, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

    /// <summary>Environment IDs of the scope of a variable in a raw variable set.</summary>
    /// <param name="variable">Raw variable.</param>
    protected static IReadOnlyList<string> ScopeEnvironments(JsonElement variable) =>
        variable.TryGetProperty("Scope", out var scope) && scope.TryGetProperty("Environment", out var environments)
            ? environments.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];

    private static string SafeName(string label) =>
        new(label.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray());
}
