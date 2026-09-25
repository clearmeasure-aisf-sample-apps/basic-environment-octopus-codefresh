using System.Globalization;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Support;

/// <summary>One reading of a stopped cluster and its data disks.</summary>
/// <param name="PowerState">Cluster power state, for example <c>Stopped</c>.</param>
/// <param name="ProvisioningState">Cluster provisioning state; <c>Stopping</c> while Azure still deallocates.</param>
/// <param name="PoolsNotStopped">Node pools that do not yet report Stopped with provisioning Succeeded, as <c>name:power/provisioning</c>.</param>
/// <param name="AttachedDisks">Disks of the tier's data group still attached to a running VM (disk state <c>Attached</c>); <c>null</c> when they could not be read.</param>
public sealed record StopReading(string? PowerState, string? ProvisioningState, IReadOnlyList<string> PoolsNotStopped, IReadOnlyList<string>? AttachedDisks)
{
    /// <summary>Cluster and every node pool read Stopped with provisioning Succeeded.</summary>
    public bool ClusterStopped =>
        string.Equals(PowerState, "Stopped", StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase)
        && PoolsNotStopped.Count == 0;

    /// <summary>The disks were read and none is attached to a running VM.</summary>
    public bool DisksReleased => AttachedDisks is { Count: 0 };

    /// <summary>Compact text for progress lines and messages.</summary>
    public override string ToString() =>
        $"power={PowerState} provisioning={ProvisioningState}"
        + (PoolsNotStopped.Count == 0 ? string.Empty : $" pools-not-stopped=[{string.Join(", ", PoolsNotStopped)}]")
        + (AttachedDisks is null ? " disks=unreadable" : AttachedDisks.Count == 0 ? " disks=released" : $" disks-attached=[{string.Join(", ", AttachedDisks)}]");
}

/// <summary>How a wait for a settled stop ended.</summary>
/// <param name="Settled"><c>true</c> when the condition held; <c>false</c> when the upper bound ended the wait.</param>
/// <param name="Waited">How long it waited.</param>
/// <param name="Readings">How many readings it took.</param>
/// <param name="Last">The last reading.</param>
public sealed record StopSettleResult(bool Settled, TimeSpan Waited, int Readings, StopReading? Last)
{
    /// <summary>Compact text for progress lines.</summary>
    public override string ToString() =>
        $"{(Settled ? "settled" : "upper bound reached")} after {DurationFormat.Human(Waited)} ({Readings} reading{(Readings == 1 ? string.Empty : "s")}; last: {Last?.ToString() ?? "none"})";
}

/// <summary>
/// Waits until a cluster that this run stopped can be started again, instead of a fixed grace. Microsoft advises 15–30
/// minutes between a stop and a start (design evidence E50) because a start that follows too closely can meet a stop that
/// Azure has not finished: the managed cluster reports Stopped while a node pool still deallocates, or the static data
/// disks (<c>disk-&lt;app&gt;-&lt;env&gt;-db</c>, ADR-IR34 decision 28) are still attached to a running node, so the next node
/// cannot attach them. The wait ends as soon as the cluster and every node pool read Stopped with provisioning Succeeded
/// on two consecutive readings and no disk of the tier's data group reads <c>Attached</c>. <c>CONFORMANCE_STOP_GRACE_MINUTES</c>
/// (default 15) is only the upper bound: when it passes the wait ends anyway, as the fixed grace did.
/// </summary>
public static class StopSettle
{
    /// <summary>Variable with the upper bound in whole minutes.</summary>
    public const string VariableName = "CONFORMANCE_STOP_GRACE_MINUTES";

    /// <summary>The upper bound when the variable is not set.</summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromMinutes(15);

    /// <summary>Consecutive readings the cluster must read stopped.</summary>
    public const int ConsecutiveReadings = 2;

    /// <summary>The upper bound: <c>CONFORMANCE_STOP_GRACE_MINUTES</c> when it is a whole number of minutes, else 15 minutes.</summary>
    /// <param name="environment">Environment variables; the process environment when omitted.</param>
    public static TimeSpan Bound(IEnvironmentVariables? environment = null) =>
        int.TryParse((environment ?? ProcessEnvironmentVariables.Instance).Get(VariableName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultBound;

    /// <summary>
    /// Reads until the stop has settled or <paramref name="bound"/> has passed. A reading that throws counts as not
    /// settled and breaks the run of consecutive stopped readings.
    /// </summary>
    /// <param name="read">One reading.</param>
    /// <param name="interval">Pause between readings (10–15 seconds in live runs).</param>
    /// <param name="bound">Upper bound; zero reads once and returns.</param>
    /// <param name="progress">Writes the <c>progress: waiting …</c> line with the last reading at least once a minute; <see cref="ConformanceProgress.DefaultReporter"/> when omitted.</param>
    /// <param name="clock">Time source; the system clock when omitted.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task<StopSettleResult> WaitAsync(
        Func<CancellationToken, Task<StopReading>> read,
        TimeSpan interval,
        TimeSpan bound,
        Action<string>? progress = null,
        IClock? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        var time = clock ?? SystemClock.Instance;
        var started = time.UtcNow;
        var deadline = started + bound;
        var wait = new WaitProgress("the stop to settle (stopped twice in a row, no data disk attached)", bound, started, progress ?? ConformanceProgress.DefaultReporter(clock));
        var consecutive = 0;
        var readings = 0;
        StopReading? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readings++;
            try
            {
                last = await read(cancellationToken).ConfigureAwait(false);
                consecutive = last.ClusterStopped ? consecutive + 1 : 0;
                wait.State = $"{last}; stopped {consecutive}/{ConsecutiveReadings}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                consecutive = 0;
                wait.State = $"reading {readings} failed: {ex.GetType().Name}: {ex.Message}";
            }

            if (consecutive >= ConsecutiveReadings && last!.DisksReleased)
            {
                return new StopSettleResult(true, time.UtcNow - started, readings, last);
            }

            var now = time.UtcNow;
            if (now >= deadline)
            {
                return new StopSettleResult(false, now - started, readings, last);
            }

            wait.Tick(now);
            var remaining = deadline - now;
            await wait.Pause(time, now + (remaining < interval ? remaining : interval), cancellationToken).ConfigureAwait(false);
        }
    }
}
