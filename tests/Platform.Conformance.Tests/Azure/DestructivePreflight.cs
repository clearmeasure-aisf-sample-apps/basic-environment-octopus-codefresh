using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// The one preflight of a destructive run, shared by the destructive fixtures before the nonprod rebuild. It checks what
/// earlier runs found only mid-run: the conformance principal can read the nonprod cluster (a rebuild without a foundation
/// re-apply answers 403), it can read Kyverno's custom kinds, and the sandbox answers in tdd and uat. The first failure is
/// kept, so the later fixtures fail in seconds instead of each spending most of an hour on the same cause.
/// </summary>
public static class DestructivePreflight
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool passed;
    private static string? failure;

    /// <summary>Runs <paramref name="check"/> the first time; later callers get its result.</summary>
    /// <param name="check">The checks (<see cref="AzureConformanceTest.RunDestructivePreflightAsync"/>); it throws on a failure.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the first run.</param>
    public static async Task EnsureAsync(Func<CancellationToken, Task> check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (passed)
            {
                return;
            }

            if (failure is not null)
            {
                Assert.Fail($"The destructive preflight of this run failed earlier: {failure}");
            }

            try
            {
                await check(cancellationToken);
                passed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
