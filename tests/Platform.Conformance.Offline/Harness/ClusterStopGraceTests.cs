using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves <see cref="ClusterStopGrace"/> with a stub clock and stub environment variables: no test waits in real time.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ClusterStopGraceTests
{
    private static readonly DateTimeOffset Stopped = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void WhenGrace_VariableValues_UsesWholeMinutesElseFifteenMinutes()
    {
        ClusterStopGrace.Grace(new StubEnvironmentVariables()).ShouldBe(TimeSpan.FromMinutes(15));
        ClusterStopGrace.Grace(new StubEnvironmentVariables((ClusterStopGrace.VariableName, "25"))).ShouldBe(TimeSpan.FromMinutes(25));
        ClusterStopGrace.Grace(new StubEnvironmentVariables((ClusterStopGrace.VariableName, "0"))).ShouldBe(TimeSpan.Zero);
        ClusterStopGrace.Grace(new StubEnvironmentVariables((ClusterStopGrace.VariableName, "-5"))).ShouldBe(TimeSpan.FromMinutes(15));
        ClusterStopGrace.Grace(new StubEnvironmentVariables((ClusterStopGrace.VariableName, "soon"))).ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenWaitAsync_StopRecordedFiveMinutesAgo_WaitsTheRestOfTheGraceOnce()
    {
        var clock = new StubClock(Stopped.AddMinutes(5));
        var environment = new StubEnvironmentVariables();
        ClusterStopGrace.RecordStop(PlatformTier.NonProd, Stopped);

        var first = await ClusterStopGrace.WaitAsync(PlatformTier.NonProd, CancellationToken.None, clock, environment);
        var second = await ClusterStopGrace.WaitAsync(PlatformTier.NonProd, CancellationToken.None, clock, environment);

        first.ShouldBe(TimeSpan.FromMinutes(10));
        second.ShouldBe(TimeSpan.Zero);
        clock.Delays.ShouldBe([TimeSpan.FromMinutes(10)]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenWaitAsync_StopOlderThanTheGraceOrNoStopOfTheTier_DoesNotWait()
    {
        var clock = new StubClock(Stopped.AddMinutes(20));
        var environment = new StubEnvironmentVariables();
        ClusterStopGrace.RecordStop(PlatformTier.Prod, Stopped);

        var prod = await ClusterStopGrace.WaitAsync(PlatformTier.Prod, CancellationToken.None, clock, environment);
        var build = await ClusterStopGrace.WaitAsync(PlatformTier.Build, CancellationToken.None, clock, environment);

        prod.ShouldBe(TimeSpan.Zero);
        build.ShouldBe(TimeSpan.Zero);
        clock.Delays.ShouldBeEmpty();
    }
}
