using System.Globalization;
using System.Text;

namespace Platform.Conformance.Harness.Support;

/// <summary>Counts of the tests a run has finished so far.</summary>
/// <param name="Done">Finished tests.</param>
/// <param name="Passed">Passed (or passed with a warning).</param>
/// <param name="Failed">Failed or errored.</param>
/// <param name="Skipped">Skipped, ignored or inconclusive.</param>
public readonly record struct ProgressCounts(int Done, int Passed, int Failed, int Skipped);

/// <summary>
/// The progress lines of a conformance run and the percent and ETA arithmetic behind them. Every line starts with
/// <c>progress:</c>, so a log search for that word shows the whole run at a glance:
/// <code>
/// progress: start 3/41 SleepDataSurvivalTests.Should_Sleep_CanaryRowWrittenBeforeForceSleep_IsReadAfterWake [CAP-GIT-011] elapsed 12:34
/// progress: waiting cluster aks-platform-nonprod to be Stopped state=Running elapsed 3:00/25:00
/// progress: done 3/41 Passed 35:02 | passed 2 failed 0 skipped 1 | 7% | eta 7:50:10
/// progress: stage 2/5 release start elapsed 14:10
/// </code>
/// </summary>
public static class ProgressFormat
{
    /// <summary>First word of every progress line.</summary>
    public const string Prefix = "progress:";

    /// <summary>Longest observed state a waiting line shows.</summary>
    public const int MaxStateLength = 120;

    /// <summary>A duration as <c>mm:ss</c> under an hour, else <c>h:mm:ss</c>: <c>05:07</c>, <c>1:02:10</c>.</summary>
    /// <param name="duration">The duration; negative values are formatted as zero, fractions of a second are dropped.</param>
    public static string Clock(TimeSpan duration) => Format(duration, padMinutes: true);

    /// <summary>A duration as <c>m:ss</c> under an hour, else <c>h:mm:ss</c>: <c>5:07</c>, <c>25:00</c>, <c>1:02:10</c>.</summary>
    /// <param name="duration">The duration; negative values are formatted as zero, fractions of a second are dropped.</param>
    public static string ShortClock(TimeSpan duration) => Format(duration, padMinutes: false);

    /// <summary>Whole percent of the selected tests that finished, rounded down and at most 100.</summary>
    /// <param name="done">Finished tests.</param>
    /// <param name="total">Selected tests; <c>null</c> when unknown.</param>
    /// <returns><c>null</c> when <paramref name="total"/> is unknown or not positive.</returns>
    public static int? Percent(int done, int? total)
    {
        if (total is not > 0)
        {
            return null;
        }

        var clamped = Math.Clamp(done, 0, total.Value);
        return (int)(clamped * 100L / total.Value);
    }

    /// <summary>
    /// Estimated time to the end of the run: the wall-clock time per finished test so far, times the tests left. Wall-clock
    /// time (not the sum of test durations) keeps the estimate right when tests run in parallel. The catalogue records no
    /// expected duration per test, so this simple average is the whole model.
    /// </summary>
    /// <param name="done">Finished tests.</param>
    /// <param name="total">Selected tests; <c>null</c> when unknown.</param>
    /// <param name="elapsed">Time since the first test started.</param>
    /// <returns><c>null</c> before the first test finishes or when <paramref name="total"/> is unknown; zero once every test finished.</returns>
    public static TimeSpan? Eta(int done, int? total, TimeSpan elapsed)
    {
        if (total is not > 0 || done <= 0)
        {
            return null;
        }

        if (done >= total.Value)
        {
            return TimeSpan.Zero;
        }

        var perTest = Math.Max(elapsed.Ticks, 0) / (double)done;
        return TimeSpan.FromTicks((long)Math.Round(perTest * (total.Value - done)));
    }

    /// <summary>The line written when a test starts.</summary>
    /// <param name="label">Suite label such as <c>offline</c>, shown as <c>[offline]</c> after the prefix; <c>null</c> for none.</param>
    /// <param name="number">Position of the test among those started (1-based).</param>
    /// <param name="total">Selected tests; <c>null</c> when unknown.</param>
    /// <param name="test">Display name, for example <c>SleepDataSurvivalTests.Should_Sleep_…</c>.</param>
    /// <param name="capabilities">Capability IDs of the test.</param>
    /// <param name="elapsed">Time since the first test started.</param>
    public static string Start(string? label, int number, int? total, string test, IReadOnlyCollection<string> capabilities, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var ids = capabilities.Count == 0 ? "-" : string.Join(' ', capabilities);
        return string.Create(CultureInfo.InvariantCulture, $"{Head(label)}start {number}/{Total(total)} {test} [{ids}] elapsed {Clock(elapsed)}");
    }

    /// <summary>The line written when a test ends.</summary>
    /// <param name="label">Suite label; <c>null</c> for none.</param>
    /// <param name="counts">Counts after this test.</param>
    /// <param name="total">Selected tests; <c>null</c> when unknown.</param>
    /// <param name="outcome">Outcome of this test, for example <c>Passed</c> or <c>Inconclusive</c>.</param>
    /// <param name="duration">Duration of this test.</param>
    /// <param name="elapsed">Time since the first test started, for the ETA.</param>
    public static string Done(string? label, ProgressCounts counts, int? total, string outcome, TimeSpan duration, TimeSpan elapsed)
    {
        var percent = Percent(counts.Done, total) is { } value ? value.ToString(CultureInfo.InvariantCulture) + "%" : "?%";
        var eta = Eta(counts.Done, total, elapsed) is { } left ? Clock(left) : "n/a";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Head(label)}done {counts.Done}/{Total(total)} {outcome} {Clock(duration)} | passed {counts.Passed} failed {counts.Failed} skipped {counts.Skipped} | {percent} | eta {eta}");
    }

    /// <summary>The line a long wait writes at least once a minute.</summary>
    /// <param name="what">What is awaited, as the wait describes it.</param>
    /// <param name="state">Last observed value or error; <c>null</c> when nothing was observed yet.</param>
    /// <param name="elapsed">Time spent waiting.</param>
    /// <param name="timeout">Longest wait.</param>
    public static string Waiting(string what, string? state, TimeSpan elapsed, TimeSpan timeout) =>
        $"{Prefix} waiting {OneLine(what, int.MaxValue)} state={(state is null ? "n/a" : OneLine(state, MaxStateLength))} elapsed {ShortClock(elapsed)}/{ShortClock(timeout)}";

    /// <summary>The line written when a stage of a multi-stage test starts.</summary>
    /// <param name="index">Stage number (1-based).</param>
    /// <param name="count">Number of stages.</param>
    /// <param name="name">Stage name, for example <c>release</c>.</param>
    /// <param name="elapsed">Time since the test started.</param>
    public static string StageStart(int index, int count, string name, TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix} stage {index}/{count} {name} start elapsed {Clock(elapsed)}");

    /// <summary>The line written when a stage of a multi-stage test ends.</summary>
    /// <param name="index">Stage number (1-based).</param>
    /// <param name="count">Number of stages.</param>
    /// <param name="name">Stage name.</param>
    /// <param name="duration">Duration of the stage.</param>
    /// <param name="elapsed">Time since the test started.</param>
    public static string StageDone(int index, int count, string name, TimeSpan duration, TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix} stage {index}/{count} {name} done {Clock(duration)} | {index * 100 / count}% of stages | elapsed {Clock(elapsed)}");

    /// <summary>Collapses whitespace to single spaces and shortens <paramref name="text"/> to <paramref name="maxLength"/> characters with an ellipsis.</summary>
    /// <param name="text">Any text.</param>
    /// <param name="maxLength">Longest result.</param>
    public static string OneLine(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, 256));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.Length <= maxLength ? builder.ToString() : string.Concat(builder.ToString(0, Math.Max(maxLength - 1, 0)), "…");
    }

    private static string Head(string? label) => string.IsNullOrWhiteSpace(label) ? $"{Prefix} " : $"{Prefix} [{label}] ";

    private static string Total(int? total) => total is > 0 ? total.Value.ToString(CultureInfo.InvariantCulture) : "?";

    private static string Format(TimeSpan duration, bool padMinutes)
    {
        var seconds = Math.Max((long)duration.TotalSeconds, 0);
        var hours = seconds / 3600;
        var minutes = seconds / 60 % 60;
        var rest = seconds % 60;
        if (hours > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}");
        }

        return padMinutes
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }
}
