using NUnit.Framework;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// Repeats a probe until a condition holds or a deadline passes. Failures name what was awaited,
/// how long and how often it was tried, and the last value or error observed. While it waits it writes
/// <c>progress: waiting &lt;what&gt; state=&lt;last observed&gt; elapsed &lt;m:ss&gt;/&lt;timeout m:ss&gt;</c> at least once a
/// minute (between probes; a pause is cut at each due report), so a long wait never leaves the log silent.
/// </summary>
public static class Poll
{
    private const int MaxObservationLength = 1000;

    /// <summary>Waits until <paramref name="condition"/> returns <c>true</c>.</summary>
    /// <param name="condition">Asynchronous check, called once per attempt.</param>
    /// <param name="timeout">Longest total wait; must be positive.</param>
    /// <param name="interval">Pause between attempts; must be positive. The last pause is shortened to meet the deadline.</param>
    /// <param name="description">What is awaited, completing "waiting for ..." (for example "cluster aks-platform-nonprod to run").</param>
    /// <param name="clock">Time source; <see cref="SystemClock.Instance"/> when omitted.</param>
    /// <param name="retryWhen">Exceptions for which the probe is retried instead of failing at once. Inconclusive results and cancellation are never retried.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="progress">Writes the <c>progress: waiting …</c> line at least once a minute; <see cref="ConformanceProgress.DefaultReporter"/> when omitted (the log for the real clock, nothing for a stub).</param>
    /// <exception cref="PollTimeoutException">The condition did not hold before the deadline.</exception>
    public static Task UntilAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan interval,
        string description,
        IClock? clock = null,
        Func<Exception, bool>? retryWhen = null,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return UntilAsync(condition, satisfied => satisfied, timeout, interval, description, clock, retryWhen, cancellationToken, progress);
    }

    /// <summary>Calls <paramref name="probe"/> until <paramref name="condition"/> accepts its value, and returns that value.</summary>
    /// <typeparam name="T">Type of the observed value.</typeparam>
    /// <param name="probe">Asynchronous observation, called once per attempt.</param>
    /// <param name="condition">Decides whether an observed value ends the wait.</param>
    /// <param name="timeout">Longest total wait; must be positive.</param>
    /// <param name="interval">Pause between attempts; must be positive. The last pause is shortened to meet the deadline.</param>
    /// <param name="description">What is awaited, completing "waiting for ...".</param>
    /// <param name="clock">Time source; <see cref="SystemClock.Instance"/> when omitted.</param>
    /// <param name="retryWhen">Exceptions for which the probe is retried instead of failing at once. Inconclusive results and cancellation are never retried.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="progress">Writes the <c>progress: waiting …</c> line, with the last observed value, at least once a minute; <see cref="ConformanceProgress.DefaultReporter"/> when omitted.</param>
    /// <returns>The first observed value that satisfies <paramref name="condition"/>.</returns>
    /// <exception cref="PollTimeoutException">No observed value satisfied the condition before the deadline.</exception>
    public static Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> condition,
        TimeSpan timeout,
        TimeSpan interval,
        string description,
        IClock? clock = null,
        Func<Exception, bool>? retryWhen = null,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive.");
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        return PollAsync(probe, condition, timeout, interval, description, clock ?? SystemClock.Instance, retryWhen, progress ?? ConformanceProgress.DefaultReporter(clock), cancellationToken);
    }

    /// <summary>
    /// Waits a fixed time, such as the stop grace, and writes <c>progress: waiting &lt;description&gt; state=waiting …</c> at
    /// least once a minute meanwhile.
    /// </summary>
    /// <param name="duration">How long to wait; nothing happens when it is not positive.</param>
    /// <param name="description">What the wait is for, for example "the stop grace of aks-platform-nonprod".</param>
    /// <param name="clock">Time source; <see cref="SystemClock.Instance"/> when omitted.</param>
    /// <param name="progress">Writes the lines; <see cref="ConformanceProgress.DefaultReporter"/> when omitted.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static Task DelayAsync(TimeSpan duration, string description, IClock? clock = null, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (duration <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var time = clock ?? SystemClock.Instance;
        var started = time.UtcNow;
        var wait = new WaitProgress(description, duration, started, progress ?? ConformanceProgress.DefaultReporter(clock)) { State = "waiting" };
        return wait.Pause(time, started + duration, cancellationToken);
    }

    private static async Task<T> PollAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> condition,
        TimeSpan timeout,
        TimeSpan interval,
        string description,
        IClock time,
        Func<Exception, bool>? retryWhen,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var started = time.UtcNow;
        var deadline = started + timeout;
        var wait = new WaitProgress(description, timeout, started, progress);
        var attempts = 0;
        string? lastObservation = null;
        Exception? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                var value = await probe(cancellationToken).ConfigureAwait(false);
                if (condition(value))
                {
                    return value;
                }

                var described = Describe(value);
                lastObservation = $"attempt {attempts}: {described}";
                wait.State = described;
            }
            catch (Exception ex) when (IsRetryable(ex, retryWhen, cancellationToken))
            {
                lastError = ex;
                wait.State = $"error {ex.GetType().Name}: {ex.Message}";
            }

            var now = time.UtcNow;
            if (now >= deadline)
            {
                throw new PollTimeoutException(description, timeout, now - started, attempts, lastObservation, lastError);
            }

            wait.Tick(now);
            var remaining = deadline - now;
            await wait.Pause(time, now + (remaining < interval ? remaining : interval), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(Exception exception, Func<Exception, bool>? retryWhen, CancellationToken cancellationToken)
    {
        if (retryWhen is null || exception is ResultStateException)
        {
            return false;
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return retryWhen(exception);
    }

    private static string Describe<T>(T value)
    {
        var text = value?.ToString() ?? "null";
        return text.Length <= MaxObservationLength ? text : string.Concat(text.AsSpan(0, MaxObservationLength), "…");
    }
}

/// <summary>Raised by <see cref="Poll"/> when the awaited condition did not hold before the deadline.</summary>
public sealed class PollTimeoutException : TimeoutException
{
    /// <summary>Creates the exception and its message from the details of the wait.</summary>
    /// <param name="description">What was awaited.</param>
    /// <param name="timeout">The configured timeout.</param>
    /// <param name="elapsed">Time actually spent.</param>
    /// <param name="attempts">Number of probes made.</param>
    /// <param name="lastObservation">Description of the last value that did not satisfy the condition, if any.</param>
    /// <param name="lastError">The last retried exception, if any.</param>
    public PollTimeoutException(string description, TimeSpan timeout, TimeSpan elapsed, int attempts, string? lastObservation, Exception? lastError)
        : base(BuildMessage(description, timeout, elapsed, attempts, lastObservation, lastError), lastError)
    {
        Description = description;
        Timeout = timeout;
        Elapsed = elapsed;
        Attempts = attempts;
        LastObservation = lastObservation;
    }

    /// <summary>What was awaited.</summary>
    public string Description { get; }

    /// <summary>The configured timeout.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Time actually spent waiting.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Number of probes made.</summary>
    public int Attempts { get; }

    /// <summary>The last observed value that did not satisfy the condition, with its attempt number.</summary>
    public string? LastObservation { get; }

    private static string BuildMessage(string description, TimeSpan timeout, TimeSpan elapsed, int attempts, string? lastObservation, Exception? lastError)
    {
        var message = $"Timed out after {DurationFormat.Human(timeout)} waiting for {description} ({attempts} attempt{(attempts == 1 ? "" : "s")} over {DurationFormat.Human(elapsed)}).";
        if (lastObservation is not null)
        {
            message += $" Last observed ({lastObservation}).";
        }

        if (lastError is not null)
        {
            message += $" Last error: {lastError.GetType().Name}: {lastError.Message}";
        }

        if (lastObservation is null && lastError is null)
        {
            message += " Nothing was observed.";
        }

        return message;
    }
}
