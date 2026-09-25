using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// One observation a shared phase made for a test that asserts on it later: the value, or the error that kept the phase
/// from observing it. An error is kept instead of failing the phase, so one broken probe (a canary write) fails only the
/// tests that read it, and the phase goes on with the others.
/// </summary>
/// <typeparam name="T">Observed type.</typeparam>
public sealed class Observation<T>
{
    private readonly T? value;

    private Observation(string what, T? value, Exception? error)
    {
        What = what;
        this.value = value;
        Error = error;
    }

    /// <summary>What was observed, for messages, for example <c>canary write through PUT /data/canary</c>.</summary>
    public string What { get; }

    /// <summary>The error, or <c>null</c> when the value was observed.</summary>
    public Exception? Error { get; }

    /// <summary>Observes once; an exception other than cancellation is kept as <see cref="Error"/>.</summary>
    /// <param name="what">What is observed.</param>
    /// <param name="probe">The observation.</param>
    /// <param name="cancellationToken">Cancellation of the phase; it propagates.</param>
    public static async Task<Observation<T>> CaptureAsync(string what, Func<CancellationToken, Task<T>> probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            return new Observation<T>(what, await probe(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Observation<T>(what, default, ex);
        }
    }

    /// <summary>An observation with a value.</summary>
    /// <param name="what">What was observed.</param>
    /// <param name="value">The value.</param>
    public static Observation<T> Of(string what, T value) => new(what, value, null);

    /// <summary>An observation that could not be made (for example a prerequisite that is missing).</summary>
    /// <param name="what">What was to be observed.</param>
    /// <param name="error">Why not.</param>
    public static Observation<T> Failed(string what, Exception error) => new(what, default, error);

    /// <summary>
    /// The value; when the observation failed, fails the test naming the phase and the error, or makes it Inconclusive
    /// when the error was Inconclusive.
    /// </summary>
    /// <param name="phase">Phase that observed it, for the message.</param>
    public T Require(string phase)
    {
        if (Error is null)
        {
            return value!;
        }

        var message = $"phase '{phase}' could not observe the {What}: {Error.Message}";
        if (Error is ResultStateException { ResultState.Status: TestStatus.Inconclusive or TestStatus.Skipped })
        {
            throw new InconclusiveException(message, Error);
        }

        throw new AssertionException(message, Error);
    }

    /// <summary>The value or the error, for progress lines.</summary>
    public override string ToString() => Error is null ? $"{What}: {value}" : $"{What}: {Error.GetType().Name}: {Error.Message}";
}
