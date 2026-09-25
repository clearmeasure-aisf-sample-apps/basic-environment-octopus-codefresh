using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Kit;

/// <summary>
/// CAP-KIT-009: a change to app #1 reaches prod through every stage. Operator-run only (<c>[Explicit]</c>): it opens a
/// pull request against the default branch of the app's repository in clearmeasure-aisf-sample-apps (never the upstream
/// ClearMeasureLabs repository), waits for <c>codefresh/ci</c>, merges, waits for <c>codefresh/release</c>, then follows
/// the Octopus release through tdd (automatic, with acceptance tests), uat and prod, answering the interventions with
/// the reason <c>e2e:&lt;run-id&gt;</c> (Platform.InterventionTestMode). Each stage writes <c>progress: stage n/5 …</c> lines
/// (ci, release, tdd, uat, prod) at its start and end.
/// </summary>
/// <remarks>
/// Settings: <c>PLATFORM_E2E_APP</c> (default <c>workorders</c>), <c>PLATFORM_E2E_REPO</c> (default: the descriptor's first
/// repository), <c>PLATFORM_E2E_FILE</c> (the harmless file it writes; default <c>src/UI/Server/e2e-marker.txt</c>).
/// The merge needs the branch protection to let the operator's token merge its own pull request [VERIFY].
/// </remarks>
[TestFixture]
[Category(Categories.Live)]
public class EndToEndTests : PlatformTestBase
{
    // The progress stages of the pass: progress: stage 1/5 ci … 5/5 prod.
    private static readonly string[] Stages = ["ci", "release", "tdd", "uat", "prod"];

    [Test]
    [Explicit("Operator-run: changes app #1 and deploys it to prod (CAP-KIT-009, design P1-12).")]
    [Capability("CAP-KIT-009")]
    [Category(Categories.Prod)]
    [Category(Categories.Slow)]
    [CancelAfter(4 * 60 * 60 * 1000)]
    public async Task Should_ChangeToAppOne_ReachesProdThroughEveryStage()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var appName = Environment.GetEnvironmentVariable("PLATFORM_E2E_APP") is { Length: > 0 } configuredApp ? configuredApp : "workorders";
        var app = KitDescriptors.Load(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance)).Single(descriptor => descriptor.Name == appName);
        var repository = Environment.GetEnvironmentVariable("PLATFORM_E2E_REPO") is { Length: > 0 } configuredRepo ? configuredRepo : app.Repositories[0].Name;
        var baseBranch = app.Repositories.FirstOrDefault(entry => entry.Name == repository).DefaultBranch ?? app.Repositories[0].DefaultBranch;
        EndToEndGuard.AssertAllowedRepository(repository);
        Settings.Check("the end-to-end pass")
            .Setting(nameof(Settings.OctopusUrl), Settings.OctopusUrl)
            .Setting(nameof(Settings.OctopusSpaceId), Settings.OctopusSpaceId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, Settings.Secrets.OctopusApiKey)
            .Secret(EnvironmentVariableNames.GitHubToken, Settings.Secrets.GitHubToken)
            .ThrowIfMissing();
        using var rest = new KitRest(Settings);
        var limits = Settings.TimeLimits;
        var note = $"e2e:{Run.RunId}";
        var branch = $"e2e/{Run.RunId}";
        var file = Environment.GetEnvironmentVariable("PLATFORM_E2E_FILE") is { Length: > 0 } configuredFile ? configuredFile : "src/UI/Server/e2e-marker.txt";
        var stages = new StageProgress(Stages);

        stages.Begin("ci");

        // One push event: a branch created first and committed to after starts two ci builds, and the later one terminates
        // the build of the head commit (Codefresh branch termination policy).
        var head = await GitHub.CreateBranchWithFileAsync(repository, branch, await GitHub.GetBranchHeadAsync(repository, baseBranch, cancellationToken), file, $"End-to-end marker {Run.RunId} {Run.StartedAt:O}; harmless (CAP-KIT-009).\n", $"e2e: harmless change {Run.RunId} (CAP-KIT-009)", cancellationToken);
        Cleanup.Register($"delete branch {branch} of {repository}", token => GitHub.DeleteBranchAsync(repository, branch, token));
        var pullRequest = await GitHub.OpenPullRequestAsync(repository, branch, baseBranch, $"e2e: {Run.RunId}", $"Harmless change of the continuous end-to-end pass (CAP-KIT-009), run {Run.RunId}.", cancellationToken);
        var merged = false;
        Cleanup.Register($"close pull request #{pullRequest.Number} unless merged", token => merged ? Task.CompletedTask : GitHub.ClosePullRequestAsync(repository, pullRequest.Number, token));
        await WaitForStatusAsync(rest, repository, head, "codefresh/ci", limits.BuildTimeout, cancellationToken);
        stages.Begin("release");
        var mergeSha = await rest.MergePullRequestAsync(repository, pullRequest.Number, $"e2e: {Run.RunId} (#{pullRequest.Number})", cancellationToken);
        merged = true;
        await WaitForStatusAsync(rest, repository, mergeSha, "codefresh/release", limits.BuildTimeout, cancellationToken);
        var project = await Octopus.GetProjectAsync(app.OctopusProjects[0], cancellationToken);
        var release = (await Poll.UntilAsync(
            token => rest.FindReleaseByCommitAsync(project.Id, mergeSha, token),
            found => found is not null,
            TimeSpan.FromMinutes(15),
            limits.PollInterval,
            $"the Octopus release of {project.Name} for commit {mergeSha}",
            cancellationToken: cancellationToken))!.Value;
        stages.Begin("tdd");
        var tdd = await Octopus.FindEnvironmentByNameAsync("tdd", cancellationToken) ?? throw new InvalidOperationException("Octopus environment tdd does not exist");
        var tddTask = await Poll.UntilAsync(
            token => rest.FindDeploymentTaskAsync(release.Id, tdd.Id, token),
            task => task is not null,
            TimeSpan.FromMinutes(10),
            limits.PollInterval,
            $"the automatic tdd deployment of {project.Name} {release.Version}",
            cancellationToken: cancellationToken);
        await CompleteAsync(tddTask!, note, limits.DeploymentTimeout, "tdd", cancellationToken);
        foreach (var environment in new[] { "uat", "prod" })
        {
            stages.Begin(environment);
            var deployment = (await Octopus.DeployReleaseAsync(new OctopusDeploymentRequest { ProjectName = project.Name, ReleaseVersion = release.Version, EnvironmentNames = [environment] }, cancellationToken)).Single();
            await CompleteAsync(deployment.TaskId, note, limits.DeploymentTimeout, environment, cancellationToken);
        }

        stages.Complete();
        TestContext.Out.WriteLine($"{project.Name} {release.Version} (commit {mergeSha}) reached prod through tdd and uat");
    }

    // The probe returns the commit status itself (pending, or none yet), so the progress lines of the wait show it.
    private static Task WaitForStatusAsync(KitRest rest, string repository, string sha, string context, TimeSpan timeout, CancellationToken cancellationToken) =>
        Poll.UntilAsync(
            async token => (await rest.CommitStatusAsync(repository, sha, context, token)) switch
            {
                "failure" or "error" => throw new InvalidOperationException($"{context} failed on {repository}@{sha}"),
                var status => status ?? "none yet",
            },
            status => status == "success",
            timeout,
            TimeSpan.FromSeconds(30),
            $"{context} to succeed on {repository}@{sha}",
            cancellationToken: cancellationToken);

    private async Task CompleteAsync(string taskId, string note, TimeSpan timeout, string environment, CancellationToken cancellationToken)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var task = await Octopus.WaitForTaskAsync(taskId, timeout, OctopusTaskWait.CompletedOrPendingInterruption, cancellationToken);
            if (task.IsCompleted)
            {
                AttachArtifact($"e2e-{environment}-task.log", await Octopus.GetTaskLogAsync(taskId, cancellationToken));
                task.FinishedSuccessfully.ShouldBeTrue($"the {environment} deployment {task} failed: {task.ErrorMessage}");
                return;
            }

            // Octopus answers its own waits (Argo CD sync and the like) and refuses a user answer. A submitted interruption can stay pending for a moment: never answer it twice, and never spin without a pause.
            var pending = (await Octopus.GetPendingInterruptionsAsync(taskId, cancellationToken)).Where(interruption => !interruption.IsAnsweredBySystem && !answered.Contains(interruption.Id)).ToArray();
            if (pending.Length == 0)
            {
                await Task.Delay(Settings.TimeLimits.PollInterval, cancellationToken);
                continue;
            }

            foreach (var interruption in pending)
            {
                TestContext.Out.WriteLine($"{environment}: answering '{interruption.Title}' with {note}");
                await Octopus.ApproveInterruptionAsync(interruption.Id, note, cancellationToken);
                answered.Add(interruption.Id);
            }
        }
    }
}
