using NUnit.Framework;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// The progress tracker of this test process (installed by <see cref="ConformanceProgressAttribute"/>) and the entry
/// point waits and stages write through. Lines go to <see cref="TestContext.Progress"/>, which NUnit flushes at once, so
/// they reach the build log while a test still runs.
/// </summary>
public static class ConformanceProgress
{
    /// <summary>Folder the progress files go to: one <c>&lt;assembly&gt;.json</c> per test assembly. Unset: no file.</summary>
    public const string DirectoryVariable = "CONFORMANCE_PROGRESS_DIR";

    private static readonly object Gate = new();
    private static ProgressTracker? tracker;

    /// <summary>The tracker of this process; <c>null</c> until the first test of an assembly with the attribute starts.</summary>
    public static ProgressTracker? Tracker
    {
        get
        {
            lock (Gate)
            {
                return tracker;
            }
        }
    }

    /// <summary>Writes a <c>progress:</c> line: through the tracker when there is one (it also updates the progress file), else straight to <see cref="TestContext.Progress"/>.</summary>
    /// <param name="line">The line.</param>
    public static void Write(string line)
    {
        if (Tracker is { } current)
        {
            current.Note(line);
        }
        else
        {
            WriteToLog(line);
        }
    }

    /// <summary>Writes one line to <see cref="TestContext.Progress"/>.</summary>
    /// <param name="line">The line.</param>
    public static void WriteToLog(string line) => TestContext.Progress.WriteLine(line);

    /// <summary>The reporter a wait uses when its caller passed none: <see cref="Write"/> for the real clock, nothing for a stub clock (its time is not the run's).</summary>
    /// <param name="clock">The wait's clock; <c>null</c> for the system clock.</param>
    public static Action<string>? DefaultReporter(IClock? clock) => clock is null or SystemClock ? Write : null;

    /// <summary>Returns the tracker of this process, creating it with <paramref name="create"/> on first use.</summary>
    /// <param name="create">Creates the tracker; called at most once per process.</param>
    internal static ProgressTracker GetOrCreate(Func<ProgressTracker> create)
    {
        lock (Gate)
        {
            if (tracker is null)
            {
                tracker = create();
                tracker.Plan();
            }

            return tracker;
        }
    }
}

/// <summary>
/// Reports a long wait at least once a minute: <c>progress: waiting &lt;what&gt; state=&lt;last observed&gt; elapsed
/// &lt;m:ss&gt;/&lt;timeout m:ss&gt;</c>. The wait calls <see cref="Tick"/> between probes and between slices of its pauses
/// (<see cref="Pause"/> cuts a pause at the next report).
/// </summary>
public sealed class WaitProgress
{
    /// <summary>Longest time between two reports of one wait.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    private readonly Action<string>? report;
    private readonly DateTimeOffset started;

    /// <summary>Starts reporting a wait.</summary>
    /// <param name="what">What is awaited.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="started">Start of the wait.</param>
    /// <param name="report">Writes a line; <c>null</c> reports nothing.</param>
    public WaitProgress(string what, TimeSpan timeout, DateTimeOffset started, Action<string>? report)
    {
        What = what;
        Timeout = timeout;
        this.started = started;
        this.report = report;
        NextReport = started + Every;
    }

    /// <summary>What is awaited.</summary>
    public string What { get; }

    /// <summary>Longest wait.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Last observed value or error; <c>null</c> until something is observed.</summary>
    public string? State { get; set; }

    /// <summary>When the next report is due.</summary>
    public DateTimeOffset NextReport { get; private set; }

    /// <summary>Reports when a report is due at <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The line written, or <c>null</c>.</returns>
    public string? Tick(DateTimeOffset now)
    {
        if (report is null || now < NextReport)
        {
            return null;
        }

        var line = ProgressFormat.Waiting(What, State, now - started, Timeout);
        report(line);
        NextReport = now + Every;
        return line;
    }

    /// <summary>Waits until <paramref name="until"/>, in slices that end at each due report, and reports between them.</summary>
    /// <param name="clock">Time source.</param>
    /// <param name="until">End of the pause.</param>
    /// <param name="cancellationToken">Cancels the pause.</param>
    public async Task Pause(IClock clock, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNow;
        while (now < until)
        {
            var sliceEnd = report is null || NextReport >= until ? until : NextReport;
            await clock.DelayAsync(sliceEnd - now, cancellationToken).ConfigureAwait(false);
            now = clock.UtcNow;
            Tick(now);
            if (report is null)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Numbered stages of one long test, as <c>progress: stage 2/5 release start …</c> and <c>… done …</c> lines, for example
/// the end-to-end pass ci, release, tdd, uat, prod.
/// </summary>
public sealed class StageProgress
{
    private readonly IReadOnlyList<string> names;
    private readonly Action<string> report;
    private readonly IClock clock;
    private readonly DateTimeOffset started;
    private int active = -1;
    private DateTimeOffset activeSince;

    /// <summary>Creates the stages; elapsed time counts from now.</summary>
    /// <param name="names">Stage names in order.</param>
    /// <param name="report">Writes a line; <see cref="ConformanceProgress.Write"/> when omitted.</param>
    /// <param name="clock">Time source; the system clock when omitted.</param>
    public StageProgress(IReadOnlyList<string> names, Action<string>? report = null, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfZero(names.Count);
        this.names = names;
        this.report = report ?? ConformanceProgress.Write;
        this.clock = clock ?? SystemClock.Instance;
        started = this.clock.UtcNow;
    }

    /// <summary>Ends the active stage, if any, and starts <paramref name="name"/>.</summary>
    /// <param name="name">One of the stage names.</param>
    public void Begin(string name)
    {
        var index = IndexOf(name);
        Complete();
        active = index;
        activeSince = clock.UtcNow;
        report(ProgressFormat.StageStart(index + 1, names.Count, name, activeSince - started));
    }

    /// <summary>Ends the active stage; does nothing when none is active.</summary>
    public void Complete()
    {
        if (active < 0)
        {
            return;
        }

        var now = clock.UtcNow;
        report(ProgressFormat.StageDone(active + 1, names.Count, names[active], now - activeSince, now - started));
        active = -1;
    }

    private int IndexOf(string name)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new ArgumentException($"'{name}' is not a stage ({string.Join(", ", names)}).", nameof(name));
    }
}
