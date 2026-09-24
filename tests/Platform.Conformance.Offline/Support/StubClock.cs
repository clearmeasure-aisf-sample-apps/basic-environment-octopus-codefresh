using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Offline.Support;

/// <summary>A clock that advances only when something waits on it, and records every wait.</summary>
internal sealed class StubClock : IClock
{
    public StubClock(DateTimeOffset start)
    {
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan by) => UtcNow += by;
}
