using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>
/// Proves <see cref="StopSettle"/> and <see cref="TierLock"/> with a stub clock: the wait after a stop ends on the
/// condition it protects (two consecutive stopped readings, no data disk attached), CONFORMANCE_STOP_GRACE_MINUTES is only
/// its upper bound, and a tier has one holder at a time. No test waits in real time.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class StopSettleTests
{
    private static readonly DateTimeOffset Stopped = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly StopReading Settled = new("Stopped", "Succeeded", [], []);

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void Should_Bound_VariableValues_UseWholeMinutesElseFifteenMinutes()
    {
        StopSettle.Bound(new StubEnvironmentVariables()).ShouldBe(TimeSpan.FromMinutes(15));
        StopSettle.Bound(new StubEnvironmentVariables((StopSettle.VariableName, "25"))).ShouldBe(TimeSpan.FromMinutes(25));
        StopSettle.Bound(new StubEnvironmentVariables((StopSettle.VariableName, "0"))).ShouldBe(TimeSpan.Zero);
        StopSettle.Bound(new StubEnvironmentVariables((StopSettle.VariableName, "-5"))).ShouldBe(TimeSpan.FromMinutes(15));
        StopSettle.Bound(new StubEnvironmentVariables((StopSettle.VariableName, "soon"))).ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitAsync_StoppedWithDisksReleased_EndsOnTheSecondReading()
    {
        var clock = new StubClock(Stopped);

        var result = await StopSettle.WaitAsync(_ => Task.FromResult(Settled), Interval, TimeSpan.FromMinutes(15), clock: clock);

        result.Settled.ShouldBeTrue();
        result.Readings.ShouldBe(2);
        clock.Delays.ShouldBe([Interval]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitAsync_PoolStillStoppingThenDiskAttached_WaitsUntilBothSettle()
    {
        var clock = new StubClock(Stopped);
        var readings = new Queue<StopReading>(
        [
            new("Stopped", "Succeeded", ["nodes:Running/Stopping"], ["disk-sandbox-tdd-db"]),
            new("Stopped", "Succeeded", [], ["disk-sandbox-tdd-db"]),
            new("Stopped", "Succeeded", [], ["disk-sandbox-tdd-db"]),
            Settled,
        ]);

        var result = await StopSettle.WaitAsync(_ => Task.FromResult(readings.Dequeue()), Interval, TimeSpan.FromMinutes(15), clock: clock);

        result.Settled.ShouldBeTrue();
        result.Readings.ShouldBe(4);
        result.Waited.ShouldBe(Interval * 3);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitAsync_ReadingFailsBetweenStoppedReadings_StartsCountingAgain()
    {
        var clock = new StubClock(Stopped);
        var attempt = 0;

        var result = await StopSettle.WaitAsync(
            _ => ++attempt == 2 ? throw new HttpRequestException("reset by the proxy") : Task.FromResult(Settled),
            Interval,
            TimeSpan.FromMinutes(15),
            clock: clock);

        result.Settled.ShouldBeTrue();
        result.Readings.ShouldBe(4);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitAsync_DisksNeverReadable_EndsAtTheUpperBoundUnsettled()
    {
        var clock = new StubClock(Stopped);
        var lines = new List<string>();

        var result = await StopSettle.WaitAsync(_ => Task.FromResult(Settled with { AttachedDisks = null }), Interval, TimeSpan.FromMinutes(2), lines.Add, clock);

        result.Settled.ShouldBeFalse();
        result.Waited.ShouldBe(TimeSpan.FromMinutes(2));
        result.Readings.ShouldBe(9);
        lines.Count.ShouldBe(2, "one waiting line a minute");
        lines[0].ShouldContain("disks=unreadable; stopped 4/2 elapsed 1:00/2:00");
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_WaitAsync_ZeroBound_ReadsOnceAndReturns()
    {
        var clock = new StubClock(Stopped);

        var result = await StopSettle.WaitAsync(_ => Task.FromResult(Settled), Interval, TimeSpan.Zero, clock: clock);

        result.Settled.ShouldBeFalse();
        result.Readings.ShouldBe(1);
        clock.Delays.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task Should_AcquireAsync_TierHeld_WaitsForTheReleaseAndNeverBlocksTheOtherTier()
    {
        var first = await TierLock.AcquireAsync(PlatformTier.Build, "first");
        var lines = new List<string>();

        var second = TierLock.AcquireAsync(PlatformTier.Build, "second", line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        });
        using var other = await TierLock.AcquireAsync(PlatformTier.Prod, "other tier").WaitAsync(TimeSpan.FromSeconds(5));
        var waited = !second.IsCompleted;
        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));

        waited.ShouldBeTrue();
        lines.ShouldContain("second: waiting for the build tier, held by first");
    }
}
