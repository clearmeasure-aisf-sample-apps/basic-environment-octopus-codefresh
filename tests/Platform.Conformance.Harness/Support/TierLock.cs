using System.Collections.Concurrent;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// One holder at a time per tier, across fixtures that run in parallel: the shared sleep and wake cycle of a tier holds it
/// from its first phase to its last, and anything else that runs in parallel and may start the tier's cluster (env-plan
/// wakes it first) takes it too, so it never starts a cluster that the cycle is putting to sleep. The two tiers never
/// wait for each other.
/// </summary>
public static class TierLock
{
    private static readonly ConcurrentDictionary<PlatformTier, SemaphoreSlim> Locks = new();

    /// <summary>Waits for the tier and holds it until the returned handle is disposed.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="holder">Who asks, for progress lines.</param>
    /// <param name="progress">Receives a line when the wait starts and every <paramref name="reportEvery"/> after; ignored when omitted.</param>
    /// <param name="reportEvery">Interval of the waiting lines; one minute when omitted.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task<IDisposable> AcquireAsync(PlatformTier tier, string holder, Action<string>? progress = null, TimeSpan? reportEvery = null, CancellationToken cancellationToken = default)
    {
        var semaphore = Locks.GetOrAdd(tier, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            var started = DateTimeOffset.UtcNow;
            progress?.Invoke($"{holder}: waiting for the {tier.ToKey()} tier, held by {Holder(tier)}");
            while (!await semaphore.WaitAsync(reportEvery ?? TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false))
            {
                progress?.Invoke($"{holder}: still waiting for the {tier.ToKey()} tier after {DurationFormat.Human(DateTimeOffset.UtcNow - started)}, held by {Holder(tier)}");
            }
        }

        Holders[tier] = holder;
        return new Release(tier, semaphore);
    }

    private static readonly ConcurrentDictionary<PlatformTier, string> Holders = new();

    private static string Holder(PlatformTier tier) => Holders.TryGetValue(tier, out var holder) ? holder : "another fixture";

    private sealed class Release(PlatformTier tier, SemaphoreSlim semaphore) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Holders.TryRemove(tier, out _);
                semaphore.Release();
            }
        }
    }
}
