using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves reverse-order cleanup that survives failures, and run-scoped names and artifacts.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class CleanupRegistryTests
{
    [Test]
    [Capability("CAP-HARNESS-008")]
    public async Task WhenRunAllAsync_ThreeActions_RunsThemInReverseRegistrationOrder()
    {
        var registry = new CleanupRegistry();
        var order = new List<string>();
        registry.Register("delete branch", _ => Record(order, "branch"));
        registry.Register("close pull request", _ => Record(order, "pull request"));
        registry.Register("delete pod", _ => Record(order, "pod"));

        var failures = await registry.RunAllAsync();

        order.ShouldBe(["pod", "pull request", "branch"]);
        failures.ShouldBeEmpty();
        registry.Count.ShouldBe(0);
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public async Task WhenRunAllAsync_ActionThrows_RunsTheRestAndReportsTheFailure()
    {
        var registry = new CleanupRegistry();
        var order = new List<string>();
        registry.Register("delete branch", _ => Record(order, "branch"));
        registry.Register("delete pod", _ => throw new InvalidOperationException("pod is protected"));
        registry.Register("close pull request", _ => Record(order, "pull request"));

        var failures = await registry.RunAllAsync();

        order.ShouldBe(["pull request", "branch"]);
        var failure = failures.ShouldHaveSingleItem();
        failure.Description.ShouldBe("delete pod");
        failure.Exception.Message.ShouldBe("pod is protected");
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public async Task WhenRunAllAsync_CalledTwice_RunsEachActionOnce()
    {
        var registry = new CleanupRegistry();
        var runs = 0;
        registry.Register("count", _ =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await registry.RunAllAsync();
        await registry.RunAllAsync();

        runs.ShouldBe(1);
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public async Task WhenRunCleanupAsync_OnBaseFixtureWithFailingCleanup_RunsAllThenThrowsNamingTheFailure()
    {
        var fixture = new SampleFixture();
        var order = new List<string>();
        fixture.Registry.Register("delete release 0.0.1-conformance", _ => Record(order, "release"));
        fixture.Registry.Register("delete pod conf-run-probe in workorders-tdd", _ => throw new InvalidOperationException("forbidden"));

        var exception = await Should.ThrowAsync<CleanupFailedException>(fixture.RunCleanupAsync);

        order.ShouldBe(["release"]);
        exception.Message.ShouldBe("1 cleanup action failed; the resources may need manual removal: delete pod conf-run-probe in workorders-tdd (InvalidOperationException: forbidden)");
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenCreate_RunIdVariableSet_UsesItAsLowercaseDnsLabel()
    {
        var environment = new StubEnvironmentVariables((EnvironmentVariableNames.RunId, "Build_4711"), (EnvironmentVariableNames.ArtifactsDirectory, Path.GetTempPath()));

        var run = TestRunContext.Create(environment, new StubClock(DateTimeOffset.UnixEpoch), repositoryRoot: null);

        run.RunId.ShouldBe("build-4711");
        run.StartedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenCreate_NoVariables_GeneratesRunIdAndUsesTheRepositoryArtifactsFolder()
    {
        var run = TestRunContext.Create(new StubEnvironmentVariables(), new StubClock(new DateTimeOffset(2026, 9, 24, 6, 30, 5, TimeSpan.Zero)), "/repo");

        run.RunId.ShouldMatch("^20260924-063005-[0-9a-f]{4}$");
        run.ArtifactsDirectory.ShouldBe(Path.GetFullPath(Path.Combine("/repo", "tests", "TestResults", "artifacts", run.RunId)));
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenResourceName_LongPurpose_ReturnsLowercaseNameOfAtMost63Characters()
    {
        var run = new TestRunContext("20260924-063005-ab12", DateTimeOffset.UnixEpoch, Path.GetTempPath());

        var name = run.ResourceName("Policy_Probe.With A Very Long Purpose That Keeps Going And Going");

        name.Length.ShouldBeLessThanOrEqualTo(63);
        name.ShouldStartWith("conf-20260924-063005-ab12-policy-probe-with-a-very-long");
        name.ShouldMatch("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenWriteArtifact_PlainFileName_WritesItUnderTheArtifactsFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "platform-conformance-tests", Guid.NewGuid().ToString("N"));
        var run = new TestRunContext("run", DateTimeOffset.UnixEpoch, folder);

        try
        {
            var path = run.WriteArtifact("env-wake-task.log", "Task log");

            path.ShouldBe(Path.Combine(folder, "env-wake-task.log"));
            File.ReadAllText(path).ShouldBe("Task log");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    [Capability("CAP-HARNESS-008")]
    public void WhenWriteArtifact_FileNameWithFolders_ThrowsArgumentException()
    {
        var run = new TestRunContext("run", DateTimeOffset.UnixEpoch, Path.GetTempPath());

        Should.Throw<ArgumentException>(() => run.WriteArtifact("../escape.log", "x"));
    }

    private static Task Record(List<string> order, string item)
    {
        order.Add(item);
        return Task.CompletedTask;
    }

    private sealed class SampleFixture : PlatformTestBase
    {
        public ICleanupRegistry Registry => Cleanup;
    }
}
