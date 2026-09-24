using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Tests.Azure;

namespace Platform.Conformance.Offline.Azure;

/// <summary>
/// CAP-AZ-009 (offline half): the live backup test reads a new CronJob's first nightly run as not yet due. The case is the
/// sandbox fixture, onboarded at 21:10Z, two hours before its first 18:30 America/Chicago run (23:30Z in daylight time).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class BackupScheduleTests
{
    private static readonly DateTimeOffset Onboarded = new(2026, 9, 24, 21, 10, 0, TimeSpan.Zero);

    [Test]
    [Capability("CAP-AZ-009")]
    public void Should_ReadFirstRunAhead_CronJobCreatedAfterItsLatestRun_BeTheNextNightlyRun()
    {
        var cronJob = Nightly(Onboarded, lastScheduleTime: null);

        cronJob.FirstRunAhead(Onboarded.AddMinutes(80)).ShouldBe(new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.Zero));
        cronJob.FirstRunAhead(new DateTimeOffset(2026, 9, 24, 23, 31, 0, TimeSpan.Zero)).ShouldBeNull("the first run is due once 23:30Z has passed");
    }

    [Test]
    [Capability("CAP-AZ-009")]
    public void Should_ReadFirstRunAhead_CronJobOlderThanItsLatestRunOrScheduled_BeNull()
    {
        var now = new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

        Nightly(Onboarded, lastScheduleTime: null).FirstRunAhead(now).ShouldBeNull("a missed run is due and the wake catches it up");
        Nightly(Onboarded.AddHours(-1), lastScheduleTime: Onboarded.AddMinutes(-5)).FirstRunAhead(Onboarded.AddMinutes(30)).ShouldBeNull("a CronJob that ran is never ahead of its first run");
        (Nightly(Onboarded, lastScheduleTime: null) with { Schedule = "*/15 * * * *" }).FirstRunAhead(Onboarded.AddMinutes(1)).ShouldBeNull("only daily schedules are read");
    }

    [Test]
    [Capability("CAP-AZ-009")]
    public void Should_ReadDailyOccurrences_ChicagoAcrossTheDstChange_FollowTheLocalTime()
    {
        var summer = Nightly(Onboarded, lastScheduleTime: null);
        var winterNow = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        summer.LastDailyOccurrence(Onboarded).ShouldBe(new DateTimeOffset(2026, 9, 23, 23, 30, 0, TimeSpan.Zero));
        summer.NextDailyOccurrence(Onboarded).ShouldBe(new DateTimeOffset(2026, 9, 24, 23, 30, 0, TimeSpan.Zero));
        summer.NextDailyOccurrence(winterNow).ShouldBe(new DateTimeOffset(2026, 11, 3, 0, 30, 0, TimeSpan.Zero));
    }

    private static BackupCronJob Nightly(DateTimeOffset created, DateTimeOffset? lastScheduleTime) =>
        new("db-backup-sandbox-uat", false, "30 18 * * *", "America/Chicago", created, lastScheduleTime, null, 0, new Dictionary<string, string>());
}
