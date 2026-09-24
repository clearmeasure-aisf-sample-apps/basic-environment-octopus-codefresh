using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-009: a release build wakes nonprod early without failing. Observed as an <c>env-wake</c> task of
/// platform-infrastructure in infra-nonprod queued within five minutes of the start of the sandbox/release build of the
/// release commit (its step wake_nonprod runs right after prepare), and that build's success. conformance-arm
/// force-slept the cluster before it pushed the commit.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class EarlyWakeTests : CodefreshCapabilityTestBase
{
    private static readonly TimeSpan WakeWindow = TimeSpan.FromMinutes(5);

    /// <summary>env-wake is queued within five minutes of the release build's start, and the build succeeds.</summary>
    [Test]
    [Capability("CAP-CF-009")]
    [Category(Categories.NonProd)]
    [CancelAfter(20 * 60 * 1000)]
    public async Task Should_GetTasksAsync_ReleaseBuild_RequestsEnvWakeEarly()
    {
        var sha = RequireArmVariable(CodefreshPlatform.ReleaseShaVariable, "the early wake test");
        var codefresh = RequireCodefresh("the early wake test");
        var first = (await codefresh.ListBuildsAsync(CodefreshPlatform.SandboxRelease, 50, Token))
            .Where(build => string.Equals(build.Revision, sha, StringComparison.OrdinalIgnoreCase))
            .OrderBy(build => build.Created)
            .FirstOrDefault();
        first.ShouldNotBeNull($"no sandbox/release build ran for {sha}");
        var build = await FinishedAsync(first);
        var began = build.Began ?? throw new InvalidOperationException($"build {build} has no start time");

        var tasks = (await Octopus.GetTasksAsync(new OctopusTaskQuery { Project = CodefreshPlatform.InfrastructureProject, Environment = CodefreshPlatform.InfraNonProd, Take = 50 }, Token))
            .Where(task => task.Description?.Contains("env-wake", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        build.Status.ShouldBe("success", $"sandbox/release build {build}");
        tasks.ShouldContain(
            task => task.QueueTime >= began.AddMinutes(-1) && task.QueueTime <= began + WakeWindow,
            $"no env-wake task in {CodefreshPlatform.InfraNonProd} queued between {began:O} and {began + WakeWindow:O}; recent: {string.Join("; ", tasks.Take(5).Select(task => $"{task.Id} {task.QueueTime:O}"))}");
    }
}
