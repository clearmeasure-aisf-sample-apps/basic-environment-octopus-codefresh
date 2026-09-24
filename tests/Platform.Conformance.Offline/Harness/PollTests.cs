using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves <see cref="Poll"/> with a stub clock: no test waits in real time.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class PollTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_ConditionHoldsOnThirdAttempt_ReturnsThatValueAfterTwoPauses()
    {
        var clock = new StubClock(Start);
        var attempts = 0;

        var value = await Poll.UntilAsync(
            _ => Task.FromResult(++attempts),
            attempt => attempt == 3,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            "the third attempt",
            clock);

        value.ShouldBe(3);
        clock.Delays.ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_ConditionNeverHolds_ThrowsTimeoutNamingDescriptionAttemptsAndLastValue()
    {
        var clock = new StubClock(Start);
        var attempts = 0;

        var exception = await Should.ThrowAsync<PollTimeoutException>(() => Poll.UntilAsync(
            _ => Task.FromResult($"power=Stopped (attempt {++attempts})"),
            state => state.StartsWith("power=Running", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            "cluster aks-platform-nonprod to run",
            clock));

        exception.Attempts.ShouldBe(4);
        exception.Elapsed.ShouldBe(TimeSpan.FromSeconds(30));
        exception.Message.ShouldBe("Timed out after 30.0 s waiting for cluster aks-platform-nonprod to run (4 attempts over 30.0 s). Last observed (attempt 4: power=Stopped (attempt 4)).");
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_IntervalOvershootsDeadline_ShortensTheLastPause()
    {
        var clock = new StubClock(Start);

        await Should.ThrowAsync<PollTimeoutException>(() => Poll.UntilAsync(
            _ => Task.FromResult(false),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(10),
            "a condition that never holds",
            clock));

        clock.Delays.ShouldBe([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)]);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_RetryableErrorThenSuccess_RetriesAndReturns()
    {
        var clock = new StubClock(Start);
        var attempts = 0;

        var value = await Poll.UntilAsync(
            _ => ++attempts < 3 ? throw new HttpRequestException("connection refused while the cluster wakes") : Task.FromResult("Running"),
            state => state == "Running",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            "the API server to answer",
            clock,
            retryWhen: exception => exception is HttpRequestException);

        value.ShouldBe("Running");
        attempts.ShouldBe(3);
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_RetryableErrorUntilDeadline_ReportsTheLastError()
    {
        var clock = new StubClock(Start);

        var exception = await Should.ThrowAsync<PollTimeoutException>(() => Poll.UntilAsync<string>(
            _ => throw new HttpRequestException("connection refused"),
            _ => true,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5),
            "the API server to answer",
            clock,
            retryWhen: error => error is HttpRequestException));

        exception.Message.ShouldEndWith("Last error: HttpRequestException: connection refused");
        exception.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_ErrorWithoutRetryPredicate_PropagatesAtOnce()
    {
        var clock = new StubClock(Start);

        await Should.ThrowAsync<InvalidOperationException>(() => Poll.UntilAsync(
            _ => throw new InvalidOperationException("unexpected 401"),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            "a call that fails",
            clock));

        clock.Delays.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_ProbeReportsMissingPrerequisite_NeverRetriesIt()
    {
        var clock = new StubClock(Start);

        await Should.ThrowAsync<PlatformPrerequisiteException>(() => Poll.UntilAsync(
            _ => throw new PlatformPrerequisiteException("no credential"),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            "a call without a credential",
            clock,
            retryWhen: _ => true));

        clock.Delays.ShouldBeEmpty();
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public void WhenUntilAsync_NonPositiveTimeout_ThrowsArgumentOutOfRange()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Poll.UntilAsync(_ => Task.FromResult(true), TimeSpan.Zero, TimeSpan.FromSeconds(1), "anything"));
    }

    [Test]
    [Capability("CAP-HARNESS-007")]
    public async Task WhenUntilAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Poll.UntilAsync(
            _ => Task.FromResult(false),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            "a cancelled wait",
            new StubClock(Start),
            retryWhen: _ => true,
            cancellationToken: cancellation.Token));
    }
}

/// <summary>Proves the duration format used by poll messages and reports.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class DurationFormatTests
{
    [TestCase(850, "850 ms")]
    [TestCase(12_400, "12.4 s")]
    [TestCase(185_000, "3 min 05 s")]
    [TestCase(3_720_000, "1 h 02 min")]
    [TestCase(-5, "0 ms")]
    [Capability("CAP-HARNESS-007")]
    public void WhenHuman_Duration_UsesTheLargestSensibleUnit(int milliseconds, string expected)
    {
        var text = DurationFormat.Human(TimeSpan.FromMilliseconds(milliseconds));

        text.ShouldBe(expected);
    }
}
