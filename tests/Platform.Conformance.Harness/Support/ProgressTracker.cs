using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Conformance.Harness.Support;

/// <summary>How a finished test counts in the progress totals.</summary>
public enum ProgressOutcome
{
    /// <summary>Passed, or passed with a warning.</summary>
    Passed,

    /// <summary>Failed, errored or cancelled.</summary>
    Failed,

    /// <summary>Skipped, ignored or inconclusive.</summary>
    Skipped,
}

/// <summary>What <see cref="ProgressTracker"/> writes to its progress file: the state of the run after the latest line.</summary>
public sealed record ProgressSnapshot
{
    /// <summary>Test assembly, for example <c>Platform.Conformance.Tests</c>.</summary>
    public required string Assembly { get; init; }

    /// <summary>Suite label, for example <c>offline</c>; <c>null</c> for none.</summary>
    public string? Label { get; init; }

    /// <summary>Tests selected by the run's filter; <c>null</c> when unknown.</summary>
    public int? Total { get; init; }

    /// <summary>Tests started.</summary>
    public int Started { get; init; }

    /// <summary>Tests finished.</summary>
    public int Done { get; init; }

    /// <summary>Tests passed.</summary>
    public int Passed { get; init; }

    /// <summary>Tests failed.</summary>
    public int Failed { get; init; }

    /// <summary>Tests skipped or inconclusive.</summary>
    public int Skipped { get; init; }

    /// <summary>Whole percent finished; <c>null</c> when the total is unknown.</summary>
    public int? Pct { get; init; }

    /// <summary>Estimated time left as <see cref="ProgressFormat.Clock"/>; <c>null</c> before the first test ends.</summary>
    public string? Eta { get; init; }

    /// <summary>Estimated seconds left; <c>null</c> before the first test ends.</summary>
    public long? EtaSeconds { get; init; }

    /// <summary>Time since the first test started, as <see cref="ProgressFormat.Clock"/>.</summary>
    public required string Elapsed { get; init; }

    /// <summary>The latest test still running (display name and capabilities); <c>null</c> between tests.</summary>
    public string? Current { get; init; }

    /// <summary>The latest progress line, printed or not.</summary>
    public string? Line { get; init; }

    /// <summary>When the snapshot was taken (UTC).</summary>
    public required DateTimeOffset Updated { get; init; }
}

/// <summary>
/// Counts the tests of one test assembly as they start and end, writes the <c>progress:</c> lines (<see cref="ProgressFormat"/>)
/// and keeps a JSON progress file (<see cref="ProgressSnapshot"/>) for the pipeline's heartbeat. Thread-safe: parallel
/// fixtures report concurrently. Writing the file never fails a test.
/// </summary>
public sealed class ProgressTracker
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object gate = new();
    private readonly Dictionary<string, (DateTimeOffset Started, string Current)> running = new(StringComparer.Ordinal);
    private readonly IClock clock;
    private readonly Action<string> write;
    private readonly DateTimeOffset startedAt;
    private int started;
    private ProgressCounts counts;
    private string? lastLine;
    private string? current;

    /// <summary>Creates a tracker; the run's elapsed time counts from now.</summary>
    /// <param name="assembly">Test assembly name, for the file.</param>
    /// <param name="total">Tests selected by the run's filter; <c>null</c> when unknown.</param>
    /// <param name="write">Writes one line to the log, unbuffered.</param>
    /// <param name="progressFile">JSON progress file to keep current; <c>null</c> for none.</param>
    /// <param name="label">Suite label shown in each line; <c>null</c> for none.</param>
    /// <param name="everyPercent">0 prints every start and end; above 0 prints no start line and an end line only at each
    /// <paramref name="everyPercent"/> percent step, for a failure and for the last test (a suite of many quick tests).</param>
    /// <param name="clock">Time source; the system clock when omitted.</param>
    public ProgressTracker(string assembly, int? total, Action<string> write, string? progressFile = null, string? label = null, int everyPercent = 0, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assembly);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(everyPercent);
        Assembly = assembly;
        Total = total is > 0 ? total : null;
        this.write = write;
        ProgressFile = progressFile;
        Label = label;
        EveryPercent = everyPercent;
        this.clock = clock ?? SystemClock.Instance;
        startedAt = this.clock.UtcNow;
    }

    /// <summary>Test assembly name.</summary>
    public string Assembly { get; }

    /// <summary>Tests selected by the run's filter; <c>null</c> when unknown.</summary>
    public int? Total { get; }

    /// <summary>The JSON progress file; <c>null</c> for none.</summary>
    public string? ProgressFile { get; }

    /// <summary>Suite label; <c>null</c> for none.</summary>
    public string? Label { get; }

    /// <summary>Printing step in percent; 0 prints every line.</summary>
    public int EveryPercent { get; }

    /// <summary>Writes the plan line (<c>progress: plan &lt;N&gt; tests selected</c>) and the first progress file.</summary>
    public void Plan()
    {
        var total = Total is { } value ? value.ToString(CultureInfo.InvariantCulture) : "?";
        Emit($"{ProgressFormat.Prefix} {(Label is null ? "" : $"[{Label}] ")}plan {total} tests selected", print: true);
    }

    /// <summary>Records the start of a test and writes its start line unless <see cref="EveryPercent"/> is set.</summary>
    /// <param name="id">Unique test ID.</param>
    /// <param name="name">Display name.</param>
    /// <param name="capabilities">Capability IDs of the test.</param>
    /// <returns>The start line.</returns>
    public string TestStarted(string id, string name, IReadOnlyCollection<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (gate)
        {
            var now = clock.UtcNow;
            started++;
            current = capabilities.Count == 0 ? name : $"{name} [{string.Join(' ', capabilities)}]";
            running[id] = (now, current);
            var line = ProgressFormat.Start(Label, started, Total, name, capabilities, now - startedAt);
            Emit(line, EveryPercent == 0);
            return line;
        }
    }

    /// <summary>Records the end of a test and writes its end line (always for a failure).</summary>
    /// <param name="id">Unique test ID given to <see cref="TestStarted"/>.</param>
    /// <param name="outcome">How the result counts.</param>
    /// <param name="outcomeText">Outcome shown in the line, for example <c>Passed</c> or <c>Inconclusive</c>.</param>
    /// <returns>The end line.</returns>
    public string TestFinished(string id, ProgressOutcome outcome, string outcomeText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeText);
        lock (gate)
        {
            var now = clock.UtcNow;
            var duration = running.Remove(id, out var entry) ? now - entry.Started : TimeSpan.Zero;
            current = running.Count == 0 ? null : running.Values.MaxBy(value => value.Started).Current;
            var before = ProgressFormat.Percent(counts.Done, Total);
            counts = outcome switch
            {
                ProgressOutcome.Passed => counts with { Done = counts.Done + 1, Passed = counts.Passed + 1 },
                ProgressOutcome.Failed => counts with { Done = counts.Done + 1, Failed = counts.Failed + 1 },
                _ => counts with { Done = counts.Done + 1, Skipped = counts.Skipped + 1 },
            };
            var line = ProgressFormat.Done(Label, counts, Total, outcomeText, duration, now - startedAt);
            Emit(line, ShouldPrintDone(outcome, before, ProgressFormat.Percent(counts.Done, Total)));
            return line;
        }
    }

    /// <summary>Writes a line of a wait or a stage and records it as the latest line.</summary>
    /// <param name="line">A <c>progress:</c> line.</param>
    public void Note(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        lock (gate)
        {
            Emit(line, print: true);
        }
    }

    /// <summary>The current state, as the progress file holds it.</summary>
    public ProgressSnapshot Snapshot()
    {
        lock (gate)
        {
            return SnapshotLocked(clock.UtcNow);
        }
    }

    private bool ShouldPrintDone(ProgressOutcome outcome, int? before, int? after)
    {
        if (EveryPercent == 0 || outcome == ProgressOutcome.Failed || (Total is { } total && counts.Done >= total))
        {
            return true;
        }

        return before is { } from && after is { } to && to / EveryPercent > from / EveryPercent;
    }

    private void Emit(string line, bool print)
    {
        lastLine = line;
        if (print)
        {
            write(line);
        }

        WriteFile();
    }

    private ProgressSnapshot SnapshotLocked(DateTimeOffset now)
    {
        var elapsed = now - startedAt;
        var eta = ProgressFormat.Eta(counts.Done, Total, elapsed);
        return new ProgressSnapshot
        {
            Assembly = Assembly,
            Label = Label,
            Total = Total,
            Started = started,
            Done = counts.Done,
            Passed = counts.Passed,
            Failed = counts.Failed,
            Skipped = counts.Skipped,
            Pct = ProgressFormat.Percent(counts.Done, Total),
            Eta = eta is { } left ? ProgressFormat.Clock(left) : null,
            EtaSeconds = eta is { } seconds ? (long)seconds.TotalSeconds : null,
            Elapsed = ProgressFormat.Clock(elapsed),
            Current = current,
            Line = lastLine,
            Updated = now,
        };
    }

    private void WriteFile()
    {
        if (ProgressFile is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(ProgressFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = $"{ProgressFile}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(SnapshotLocked(clock.UtcNow), FileJson) + "\n");
            File.Move(temporary, ProgressFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Telemetry only: the log lines still carry the progress.
        }
    }
}
