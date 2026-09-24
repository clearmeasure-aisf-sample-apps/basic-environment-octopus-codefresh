using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Tests.Azure;

/// <summary>Reads the parsable lines the platform runbooks write into their task logs.</summary>
public static partial class RunbookLogs
{
    /// <summary>
    /// The Terraform plan summary of a task log: "No changes." or the "Plan: a to add, c to change, d to destroy" line
    /// (with the optional "i to import"). <c>null</c> when the log holds neither.
    /// </summary>
    /// <param name="log">Raw task log.</param>
    public static TerraformPlanSummary? PlanSummary(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var counts = PlanLine().Matches(log).LastOrDefault();
        if (counts is not null)
        {
            return new TerraformPlanSummary(
                Count(counts.Groups["import"]),
                Count(counts.Groups["add"]),
                Count(counts.Groups["change"]),
                Count(counts.Groups["destroy"]),
                counts.Value.Trim());
        }

        return NoChangesLine().IsMatch(log) ? new TerraformPlanSummary(0, 0, 0, 0, "No changes.") : null;
    }

    /// <summary>
    /// The decision of runbook env-sleep: its highlight
    /// "Sleep.Decision=&lt;sleep|stay&gt; Sleep.DryRun=&lt;bool&gt; Environment=&lt;env&gt; Reason=&lt;text&gt;". <c>null</c> when absent.
    /// </summary>
    /// <param name="log">Raw task log.</param>
    public static SleepDecision? SleepDecision(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var match = SleepDecisionLine().Matches(log).LastOrDefault();
        return match is null
            ? null
            : new SleepDecision(match.Groups["decision"].Value, string.Equals(match.Groups["dryRun"].Value, "true", StringComparison.OrdinalIgnoreCase), match.Groups["reason"].Value.Trim());
    }

    private static int Count(Group group) => group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"Plan: (?:(?<import>\d+) to import, )?(?<add>\d+) to add, (?<change>\d+) to change, (?<destroy>\d+) to destroy")]
    private static partial Regex PlanLine();

    [GeneratedRegex(@"No changes\. Your infrastructure matches the configuration")]
    private static partial Regex NoChangesLine();

    [GeneratedRegex(@"Sleep\.Decision=(?<decision>sleep|stay) Sleep\.DryRun=(?<dryRun>\S+) Environment=\S+ Reason=(?<reason>[^\r\n]*)")]
    private static partial Regex SleepDecisionLine();
}

/// <summary>A Terraform plan summary.</summary>
/// <param name="Import">Resources to import.</param>
/// <param name="Add">Resources to add.</param>
/// <param name="Change">Resources to change.</param>
/// <param name="Destroy">Resources to destroy.</param>
/// <param name="Line">The summary as logged.</param>
public sealed record TerraformPlanSummary(int Import, int Add, int Change, int Destroy, string Line)
{
    /// <summary><c>true</c> when the plan changes no infrastructure.</summary>
    public bool NoChanges => Import == 0 && Add == 0 && Change == 0 && Destroy == 0;
}

/// <summary>A decision of runbook env-sleep.</summary>
/// <param name="Decision"><c>sleep</c> or <c>stay</c>.</param>
/// <param name="DryRun"><c>true</c> for a dry run, which never stops the cluster.</param>
/// <param name="Reason">Why, as logged.</param>
public sealed record SleepDecision(string Decision, bool DryRun, string Reason)
{
    /// <summary><c>true</c> when the runbook decided to stop the cluster for real.</summary>
    public bool Sleeps => Decision == "sleep" && !DryRun;
}

/// <summary>
/// The state of a backup CronJob (<c>db-backup-&lt;app&gt;-&lt;env&gt;</c>) as the Kubernetes API reports it, read through the
/// harness's generic object reads.
/// </summary>
/// <param name="Name">CronJob name.</param>
/// <param name="Suspended"><c>true</c> when <c>spec.suspend</c> is set (a frozen app).</param>
/// <param name="Schedule">Cron schedule.</param>
/// <param name="TimeZone">IANA time zone of the schedule, or <c>null</c> for the controller's zone (UTC on AKS).</param>
/// <param name="Created">Creation time of the CronJob.</param>
/// <param name="LastScheduleTime">When the controller last created a Job.</param>
/// <param name="LastSuccessfulTime">When a Job last completed successfully.</param>
/// <param name="ActiveJobs">Jobs running now.</param>
/// <param name="Environment">Environment variables of the Job template's first container.</param>
public sealed record BackupCronJob(
    string Name,
    bool Suspended,
    string? Schedule,
    string? TimeZone,
    DateTimeOffset? Created,
    DateTimeOffset? LastScheduleTime,
    DateTimeOffset? LastSuccessfulTime,
    int ActiveJobs,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Reads a <c>batch/v1</c> CronJob object.</summary>
    /// <param name="cronJob">The object as JSON.</param>
    public static BackupCronJob From(JsonElement cronJob)
    {
        var container = Path(cronJob, "spec", "jobTemplate", "spec", "template", "spec", "containers") is { ValueKind: JsonValueKind.Array } containers && containers.GetArrayLength() > 0
            ? containers[0]
            : default;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array)
        {
            foreach (var variable in env.EnumerateArray())
            {
                if (variable.TryGetProperty("name", out var name) && variable.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    environment[name.GetString() ?? string.Empty] = value.GetString() ?? string.Empty;
                }
            }
        }

        return new BackupCronJob(
            ArmReader.Text(cronJob, "metadata", "name") ?? string.Empty,
            ArmReader.Text(cronJob, "spec", "suspend") == "true",
            ArmReader.Text(cronJob, "spec", "schedule"),
            ArmReader.Text(cronJob, "spec", "timeZone"),
            Time(ArmReader.Text(cronJob, "metadata", "creationTimestamp")),
            Time(ArmReader.Text(cronJob, "status", "lastScheduleTime")),
            Time(ArmReader.Text(cronJob, "status", "lastSuccessfulTime")),
            Path(cronJob, "status", "active") is { ValueKind: JsonValueKind.Array } active ? active.GetArrayLength() : 0,
            environment);
    }

    /// <summary>
    /// The latest time at or before <paramref name="now"/> at which a daily schedule (<c>M H * * *</c>) fires in its time zone;
    /// <c>null</c> for any other schedule shape or an unknown time zone.
    /// </summary>
    /// <param name="now">The current time.</param>
    public DateTimeOffset? LastDailyOccurrence(DateTimeOffset now) => DailyOccurrence(now, next: false);

    /// <summary>
    /// The first time after <paramref name="now"/> at which a daily schedule (<c>M H * * *</c>) fires in its time zone;
    /// <c>null</c> for any other schedule shape or an unknown time zone.
    /// </summary>
    /// <param name="now">The current time.</param>
    public DateTimeOffset? NextDailyOccurrence(DateTimeOffset now) => DailyOccurrence(now, next: true);

    /// <summary>
    /// When a CronJob that has never run is first due, if no occurrence of its daily schedule lies between its creation and
    /// <paramref name="now"/> (a new app's first nightly backup is still ahead); otherwise <c>null</c>: it has run, or it
    /// is due, or its schedule is not daily.
    /// </summary>
    /// <param name="now">The current time.</param>
    public DateTimeOffset? FirstRunAhead(DateTimeOffset now)
    {
        if (LastScheduleTime is not null || Created is not { } created)
        {
            return null;
        }

        var latest = LastDailyOccurrence(now);
        return latest is not null && latest < created ? NextDailyOccurrence(now) : null;
    }

    private DateTimeOffset? DailyOccurrence(DateTimeOffset now, bool next)
    {
        var fields = (Schedule ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields[2] != "*" || fields[3] != "*" || fields[4] != "*"
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) || minute > 59
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour > 23)
        {
            return null;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(TimeZone) ? "UTC" : TimeZone);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var candidate = local.Date.AddHours(hour).AddMinutes(minute);
            if (next && candidate <= local.DateTime)
            {
                candidate = candidate.AddDays(1);
            }
            else if (!next && candidate > local.DateTime)
            {
                candidate = candidate.AddDays(-1);
            }

            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Compact summary for messages.</summary>
    public override string ToString() =>
        $"{Name} suspend={Suspended} schedule='{Schedule}' {TimeZone} lastSchedule={LastScheduleTime:u} lastSuccess={LastSuccessfulTime:u} active={ActiveJobs}";

    private static JsonElement? Path(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
