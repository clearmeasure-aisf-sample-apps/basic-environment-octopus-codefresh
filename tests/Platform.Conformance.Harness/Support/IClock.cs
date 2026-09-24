namespace Platform.Conformance.Harness.Support;

/// <summary>Time source for polling and timestamps, replaceable by a stub in unit tests.</summary>
public interface IClock
{
    /// <summary>The current time in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for <paramref name="delay"/>.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The real clock: <see cref="DateTimeOffset.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
