using System.Reflection;
using NUnit.Framework.Interfaces;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Tests;

namespace Platform.Conformance.Offline.Harness;

/// <summary>
/// Proves <see cref="PhasedCycle"/>, the ordering logic of the shared sleep and wake cycle and the sandbox tdd rollout:
/// phases run once, in order, on first demand; a phase that does not pass skips the phases that need it and ends the tests
/// that need them with its name; a phase with no needs runs anyway.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PhasedCycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_RequireAsync_ManyWaiters_RunEachPhaseOnceInOrder()
    {
        var ran = new List<string>();
        var cycle = new PhasedCycle("test cycle", [Phase("a", ran), Phase("b", ran), Phase("c", ran)], Budget);

        await Task.WhenAll(cycle.RequireAsync(CancellationToken.None, "c"), cycle.RequireAsync(CancellationToken.None, "a"), cycle.RequireAsync(CancellationToken.None, "b", "a"));
        await cycle.RequireAsync(CancellationToken.None, "a", "b", "c");

        ran.ShouldBe(["a", "b", "c"]);
        cycle.Outcomes.Select(outcome => outcome.Status).ShouldBe([PhaseStatus.Passed, PhaseStatus.Passed, PhaseStatus.Passed]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void Should_Constructor_NothingAwaited_StartsNothing()
    {
        var ran = new List<string>();

        var cycle = new PhasedCycle("test cycle", [Phase("a", ran)], Budget);

        cycle.Started.ShouldBeFalse();
        ran.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_RequireAsync_PhaseFailed_FailsDependentTestsNamingThePhaseAndRunsPhasesWithoutNeeds()
    {
        var ran = new List<string>();
        var cycle = new PhasedCycle(
            "nonprod sleep and wake cycle",
            [
                Phase("awake-before", ran),
                Phase("sleep", ran, () => throw new InvalidOperationException("env-sleep failed")),
                Phase("asleep", ran, needs: ["sleep"]),
                Phase("wake", ran, needs: []),
                Phase("awake-after", ran, needs: ["wake"]),
            ],
            Budget);

        var asleep = await Should.ThrowAsync<AssertionException>(() => cycle.RequireAsync(CancellationToken.None, "asleep", "sleep"));
        await cycle.RequireAsync(CancellationToken.None, "awake-after", "awake-before");

        asleep.Message.ShouldBe("phase 'sleep' of the nonprod sleep and wake cycle failed: InvalidOperationException: env-sleep failed");
        ran.ShouldBe(["awake-before", "sleep", "wake", "awake-after"]);
        cycle.Outcomes.Single(outcome => outcome.Name == "asleep").Status.ShouldBe(PhaseStatus.Skipped);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_RequireAsync_OnlySkippedPhaseNeeded_FailsNamingThePhaseThatFailed()
    {
        var cycle = new PhasedCycle("test cycle", [Phase("a", [], () => throw new InvalidOperationException("boom")), Phase("b", [])], Budget);

        var failure = await Should.ThrowAsync<AssertionException>(() => cycle.RequireAsync(CancellationToken.None, "b"));

        failure.Message.ShouldBe("phase 'a' of the test cycle failed: InvalidOperationException: boom (so phase 'b' did not run)");
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_RequireAsync_PhaseInconclusive_MakesDependentTestsInconclusiveWithTheReason()
    {
        var cycle = new PhasedCycle(
            "prod sleep and wake cycle",
            [Phase("sleep", [], () => throw new InconclusiveException("env-sleep kept aks-platform-prod awake (inside the working window)")), Phase("asleep", [], needs: ["sleep"]), Phase("wake", [], needs: [])],
            Budget);

        var asleep = await Should.ThrowAsync<InconclusiveException>(() => cycle.RequireAsync(CancellationToken.None, "asleep"));
        await cycle.RequireAsync(CancellationToken.None, "wake");

        asleep.Message.ShouldBe("phase 'sleep' of the prod sleep and wake cycle was inconclusive: env-sleep kept aks-platform-prod awake (inside the working window) (so phase 'asleep' did not run)");
        asleep.ResultState.Status.ShouldBe(TestStatus.Inconclusive);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_RequireAsync_WaiterCancelled_LeavesTheCycleRunning()
    {
        var release = new TaskCompletionSource();
        var ran = new List<string>();
        var cycle = new PhasedCycle("test cycle", [new CyclePhase("slow", async _ => await release.Task), Phase("after", ran)], Budget);
        using var cancelled = new CancellationTokenSource();

        var waiting = cycle.RequireAsync(cancelled.Token, "after");
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await cycle.RequireAsync(CancellationToken.None, "after");

        ran.ShouldBe(["after"]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void Should_Constructor_PhaseNeedsLaterPhase_Throws()
    {
        var phases = new[] { new CyclePhase("a", _ => Task.CompletedTask, ["b"]), new CyclePhase("b", _ => Task.CompletedTask) };

        Should.Throw<ArgumentException>(() => new PhasedCycle("test cycle", phases, Budget)).Message.ShouldStartWith("Phase 'a' needs 'b', which is not an earlier phase.");
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitForAllStartedAsync_StartedCycle_WaitsForItsLastPhaseAndEnd()
    {
        var release = new TaskCompletionSource();
        var ended = false;
        var cycle = new PhasedCycle("test cycle", [new CyclePhase("slow", async _ => await release.Task)], Budget, onEnd: () =>
        {
            ended = true;
            return Task.CompletedTask;
        });
        _ = cycle.Start();

        var all = PhasedCycle.WaitForAllStartedAsync(CancellationToken.None);
        var early = all.IsCompleted;
        release.SetResult();
        await all;

        early.ShouldBeFalse();
        ended.ShouldBeTrue();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void Should_TierSleepCycle_EitherAppTier_RunsHoldAwakeBeforeQuiesceSleepAsleepWakeAwakeAfterInOrder()
    {
        var expected = new[] { SleepPhase.HoldTier, SleepPhase.AwakeBefore, SleepPhase.Quiesce, SleepPhase.Sleep, SleepPhase.Asleep, SleepPhase.Wake, SleepPhase.AwakeAfter };

        var nonprod = TierSleepCycle.For(PlatformTier.NonProd);
        var prod = TierSleepCycle.For(PlatformTier.Prod);

        nonprod.Phases.PhaseNames.ShouldBe(expected);
        prod.Phases.PhaseNames.ShouldBe(expected);
        nonprod.Phases.Started.ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => TierSleepCycle.For(PlatformTier.Build));
    }

    /// <summary>
    /// The partition that keeps the cycles alone: only the fixtures that share a sleep and wake cycle, and env-plan (which
    /// takes the tier lock), run in NUnit's parallel shift; every other live fixture, those that deploy sandbox releases
    /// included, stays non-parallel, and NUnit never runs the two shifts at once.
    /// </summary>
    [Test]
    [Capability("CAP-HARNESS-007")]
    public void Should_LiveFixtures_ParallelOnes_AreExactlyTheCycleFixturesAndEnvPlan()
    {
        var expected = new[]
        {
            "Platform.Conformance.Tests.Azure.SleepAlertTests",
            "Platform.Conformance.Tests.Azure.StopWithAdmissionTests",
            "Platform.Conformance.Tests.Azure.TierIdempotenceTests",
            "Platform.Conformance.Tests.GitOps.SleepDataSurvivalTests",
            "Platform.Conformance.Tests.Octopus.ForceSleepWakeTests",
            "Platform.Conformance.Tests.Octopus.RunbookWaitGuardTests",
            "Platform.Conformance.Tests.Octopus.WakeOnDeploymentTests",
        };
        var fixtures = typeof(TierSleepCycle).Assembly.GetTypes().Where(type => type.GetCustomAttributes<TestFixtureAttribute>().Any()).ToArray();

        var parallel = fixtures.Where(type => type.GetCustomAttribute<ParallelizableAttribute>() is not null).Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();

        parallel.ShouldBe(expected);
        fixtures.Where(type => type.GetCustomAttribute<ParallelizableAttribute>() is { } attribute && attribute.Scope != ParallelScope.All).ShouldBeEmpty();
        typeof(TierSleepCycle).Assembly.GetCustomAttribute<LevelOfParallelismAttribute>().ShouldNotBeNull("the parallel shift needs a worker for every waiting test of both cycles");
    }

    private static CyclePhase Phase(string name, List<string> ran, Action? body = null, IReadOnlyList<string>? needs = null) =>
        new(name, _ =>
        {
            lock (ran)
            {
                ran.Add(name);
            }

            body?.Invoke();
            return Task.CompletedTask;
        }, needs);
}
