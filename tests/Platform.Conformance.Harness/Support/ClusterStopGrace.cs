using System.Collections.Concurrent;
using System.Globalization;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// Remembers when this run stopped a cluster, so that whatever starts it next waits the grace Microsoft advises between a
/// stop and a start (15–30 minutes, design evidence E50): <c>CONFORMANCE_STOP_GRACE_MINUTES</c>, 15 by default, as
/// platform-env/conformance-arm waits after its own stop. Stops made outside this process are not known and cost no wait.
/// </summary>
public static class ClusterStopGrace
{
    /// <summary>Variable with the grace in whole minutes.</summary>
    public const string VariableName = "CONFORMANCE_STOP_GRACE_MINUTES";

    /// <summary>The grace when the variable is not set.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<PlatformTier, DateTimeOffset> Stops = new();

    /// <summary>The configured grace: <c>CONFORMANCE_STOP_GRACE_MINUTES</c> when it is a whole number of minutes, else 15 minutes.</summary>
    /// <param name="environment">Environment variables; the process environment when omitted.</param>
    public static TimeSpan Grace(IEnvironmentVariables? environment = null) =>
        int.TryParse((environment ?? ProcessEnvironmentVariables.Instance).Get(VariableName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultGrace;

    /// <summary>Records that this run saw the cluster of <paramref name="tier"/> reach Stopped at <paramref name="stoppedAt"/>.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="stoppedAt">When the cluster was seen stopped.</param>
    public static void RecordStop(PlatformTier tier, DateTimeOffset stoppedAt) => Stops[tier] = stoppedAt;

    /// <summary>How much of the grace is left for <paramref name="tier"/> at <paramref name="now"/>; zero when this run stopped nothing.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="now">The current time.</param>
    /// <param name="grace">The grace.</param>
    public static TimeSpan Remaining(PlatformTier tier, DateTimeOffset now, TimeSpan grace) =>
        Stops.TryGetValue(tier, out var stopped) && stopped + grace > now ? stopped + grace - now : TimeSpan.Zero;

    /// <summary>Waits out what is left of the grace before the cluster of <paramref name="tier"/> is started.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="clock">Time source; the system clock when omitted.</param>
    /// <param name="environment">Environment variables; the process environment when omitted.</param>
    /// <returns>How long it waited.</returns>
    public static async Task<TimeSpan> WaitAsync(PlatformTier tier, CancellationToken cancellationToken, IClock? clock = null, IEnvironmentVariables? environment = null)
    {
        var time = clock ?? SystemClock.Instance;
        var remaining = Remaining(tier, time.UtcNow, Grace(environment));
        if (remaining > TimeSpan.Zero)
        {
            await time.DelayAsync(remaining, cancellationToken).ConfigureAwait(false);
        }

        return remaining;
    }
}
