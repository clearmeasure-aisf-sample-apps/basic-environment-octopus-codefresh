using System.Text.Json.Nodes;
using NUnit.Framework.Internal;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Support;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the progress lines of a run and the percent and ETA arithmetic behind them.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ProgressFormatTests
{
    [TestCase(0, "00:00")]
    [TestCase(307, "05:07")]
    [TestCase(3_599, "59:59")]
    [TestCase(3_730, "1:02:10")]
    [TestCase(-5, "00:00")]
    [Capability("CAP-HARNESS-013")]
    public void Should_Clock_Duration_ShowsMinutesAndSecondsUnderAnHourElseHours(int seconds, string expected)
    {
        var text = ProgressFormat.Clock(TimeSpan.FromSeconds(seconds));

        text.ShouldBe(expected);
    }

    [TestCase(0, "0:00")]
    [TestCase(307, "5:07")]
    [TestCase(1_500, "25:00")]
    [TestCase(3_730, "1:02:10")]
    [Capability("CAP-HARNESS-013")]
    public void Should_ShortClock_Duration_LeavesMinutesUnpadded(int seconds, string expected)
    {
        var text = ProgressFormat.ShortClock(TimeSpan.FromSeconds(seconds));

        text.ShouldBe(expected);
    }

    [TestCase(0, 41, 0)]
    [TestCase(3, 41, 7)]
    [TestCase(40, 41, 97)]
    [TestCase(41, 41, 100)]
    [TestCase(50, 41, 100)]
    [TestCase(3, 0, null)]
    [TestCase(3, null, null)]
    [Capability("CAP-HARNESS-013")]
    public void Should_Percent_DoneOfTotal_RoundsDownClampsAndIsUnknownWithoutTotal(int done, int? total, int? expected)
    {
        var percent = ProgressFormat.Percent(done, total);

        percent.ShouldBe(expected);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Eta_ThreeOfTwelveInThirtyMinutes_IsNinetyMinutes()
    {
        var eta = ProgressFormat.Eta(3, 12, TimeSpan.FromMinutes(30));

        eta.ShouldBe(TimeSpan.FromMinutes(90));
    }

    [TestCase(0, 12)]
    [TestCase(3, null)]
    [TestCase(3, 0)]
    [Capability("CAP-HARNESS-013")]
    public void Should_Eta_NothingDoneOrTotalUnknown_IsUnknown(int done, int? total)
    {
        var eta = ProgressFormat.Eta(done, total, TimeSpan.FromMinutes(30));

        eta.ShouldBeNull();
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Eta_EveryTestDone_IsZero()
    {
        var eta = ProgressFormat.Eta(12, 12, TimeSpan.FromMinutes(30));

        eta.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Start_TestWithCapability_ShowsPositionNameCapabilityAndElapsed()
    {
        var line = ProgressFormat.Start(null, 3, 41, "SleepDataSurvivalTests.Should_Sleep_CanaryRowWrittenBeforeForceSleep_IsReadAfterWake", ["CAP-GIT-011"], TimeSpan.FromSeconds(754));

        line.ShouldBe("progress: start 3/41 SleepDataSurvivalTests.Should_Sleep_CanaryRowWrittenBeforeForceSleep_IsReadAfterWake [CAP-GIT-011] elapsed 12:34");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Start_LabelUnknownTotalAndNoCapability_ShowsLabelQuestionMarkAndDash()
    {
        var line = ProgressFormat.Start("offline", 7, null, "PollTests.WhenUntilAsync_Cancelled_ThrowsOperationCanceled", [], TimeSpan.Zero);

        line.ShouldBe("progress: [offline] start 7/? PollTests.WhenUntilAsync_Cancelled_ThrowsOperationCanceled [-] elapsed 00:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Done_ThirdOfFortyOne_ShowsOutcomeDurationCountsPercentAndEta()
    {
        var line = ProgressFormat.Done(null, new ProgressCounts(3, 2, 0, 1), 41, "Passed", TimeSpan.FromSeconds(2_102), TimeSpan.FromMinutes(45));

        line.ShouldBe("progress: done 3/41 Passed 35:02 | passed 2 failed 0 skipped 1 | 7% | eta 9:30:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Done_TotalUnknown_ShowsQuestionMarksAndNoEta()
    {
        var line = ProgressFormat.Done(null, new ProgressCounts(1, 0, 1, 0), null, "Error", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(9));

        line.ShouldBe("progress: done 1/? Error 00:09 | passed 0 failed 1 skipped 0 | ?% | eta n/a");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Waiting_MultiLineLongState_CollapsesWhitespaceAndShortensIt()
    {
        var state = "error HttpRequestException:\n  connection refused " + new string('x', 200);

        var line = ProgressFormat.Waiting("cluster aks-platform-nonprod to be Running", state, TimeSpan.FromSeconds(180), TimeSpan.FromMinutes(25));

        line.ShouldStartWith("progress: waiting cluster aks-platform-nonprod to be Running state=error HttpRequestException: connection refused xxx");
        line.ShouldEndWith("x… elapsed 3:00/25:00");
        line.Length.ShouldBe("progress: waiting cluster aks-platform-nonprod to be Running state=".Length + ProgressFormat.MaxStateLength + " elapsed 3:00/25:00".Length);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Waiting_NothingObservedYet_ShowsStateNotAvailable()
    {
        var line = ProgressFormat.Waiting("Octopus task ServerTasks-1 to complete", null, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30));

        line.ShouldBe("progress: waiting Octopus task ServerTasks-1 to complete state=n/a elapsed 1:00/30:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_StageProgress_FiveStages_NumbersEachStartAndEnd()
    {
        var clock = new StubClock(new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero));
        var lines = new List<string>();
        var stages = new StageProgress(["ci", "release", "tdd", "uat", "prod"], lines.Add, clock);

        stages.Begin("ci");
        clock.Advance(TimeSpan.FromMinutes(8));
        stages.Begin("release");
        clock.Advance(TimeSpan.FromMinutes(6));
        stages.Complete();

        lines.ShouldBe(
        [
            "progress: stage 1/5 ci start elapsed 00:00",
            "progress: stage 1/5 ci done 08:00 | 20% of stages | elapsed 08:00",
            "progress: stage 2/5 release start elapsed 08:00",
            "progress: stage 2/5 release done 06:00 | 40% of stages | elapsed 14:00",
        ]);
    }
}

/// <summary>Proves the progress tracker: its lines, its step mode and its progress file.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class ProgressTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Tracker_TwoTestsOfFour_WritesTheLinesAndKeepsTheProgressFile()
    {
        var clock = new StubClock(Start);
        var lines = new List<string>();
        var file = Path.Combine(Path.GetTempPath(), $"progress-{Guid.NewGuid():N}", "Platform.Conformance.Tests.json");
        var tracker = new ProgressTracker("Platform.Conformance.Tests", 4, lines.Add, file, clock: clock);

        tracker.TestStarted("1", "PowerStateTests.Should_Sleep", ["CAP-AZ-004"]);
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.TestFinished("1", ProgressOutcome.Passed, "Passed");
        tracker.TestStarted("2", "SleepDataSurvivalTests.Should_Sleep", ["CAP-GIT-011"]);
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.TestFinished("2", ProgressOutcome.Skipped, "Inconclusive");
        tracker.TestStarted("3", "EndToEndTests.Should_ReachProd", ["CAP-KIT-009"]);
        tracker.Note("progress: stage 1/5 ci start elapsed 20:00");

        lines.ShouldBe(
        [
            "progress: start 1/4 PowerStateTests.Should_Sleep [CAP-AZ-004] elapsed 00:00",
            "progress: done 1/4 Passed 10:00 | passed 1 failed 0 skipped 0 | 25% | eta 30:00",
            "progress: start 2/4 SleepDataSurvivalTests.Should_Sleep [CAP-GIT-011] elapsed 10:00",
            "progress: done 2/4 Inconclusive 10:00 | passed 1 failed 0 skipped 1 | 50% | eta 20:00",
            "progress: start 3/4 EndToEndTests.Should_ReachProd [CAP-KIT-009] elapsed 20:00",
            "progress: stage 1/5 ci start elapsed 20:00",
        ]);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        (json["assembly"]!.GetValue<string>(), json["total"]!.GetValue<int>(), json["started"]!.GetValue<int>(), json["done"]!.GetValue<int>()).ShouldBe(("Platform.Conformance.Tests", 4, 3, 2));
        (json["passed"]!.GetValue<int>(), json["failed"]!.GetValue<int>(), json["skipped"]!.GetValue<int>(), json["pct"]!.GetValue<int>()).ShouldBe((1, 0, 1, 50));
        (json["eta"]!.GetValue<string>(), json["etaSeconds"]!.GetValue<long>(), json["elapsed"]!.GetValue<string>()).ShouldBe(("20:00", 1200L, "20:00"));
        json["current"]!.GetValue<string>().ShouldBe("EndToEndTests.Should_ReachProd [CAP-KIT-009]");
        json["line"]!.GetValue<string>().ShouldBe("progress: stage 1/5 ci start elapsed 20:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Tracker_EveryTenPercent_PrintsOnlyTheStepsAFailureAndTheLastTest()
    {
        var lines = new List<string>();
        var tracker = new ProgressTracker("Platform.Conformance.Offline", 25, lines.Add, label: "offline", everyPercent: 10, clock: new StubClock(Start));

        for (var index = 1; index <= 25; index++)
        {
            var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            tracker.TestStarted(id, $"T.Test{index}", []);
            tracker.TestFinished(id, index == 4 ? ProgressOutcome.Failed : ProgressOutcome.Passed, index == 4 ? "Failed" : "Passed");
        }

        lines.Select(line => line.Split(' ')[3]).ShouldBe(["3/25", "4/25", "5/25", "8/25", "10/25", "13/25", "15/25", "18/25", "20/25", "23/25", "25/25"]);
        lines.ShouldAllBe(line => line.StartsWith("progress: [offline] done ", StringComparison.Ordinal));
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Tracker_ParallelTests_CurrentIsTheLatestStillRunning()
    {
        var clock = new StubClock(Start);
        var tracker = new ProgressTracker("Platform.Conformance.Offline", 3, _ => { }, clock: clock);

        tracker.TestStarted("a", "A.One", []);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.TestStarted("b", "B.Two", []);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.TestFinished("b", ProgressOutcome.Passed, "Passed");

        tracker.Snapshot().Current.ShouldBe("A.One");
    }
}

/// <summary>Proves that long waits report at least once a minute with the last observed state.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class WaitProgressTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    [Test]
    [Capability("CAP-HARNESS-013")]
    public async Task Should_UntilAsync_FourMinuteWait_ReportsEachMinuteWithTheLastState()
    {
        var clock = new StubClock(Start);
        var lines = new List<string>();

        var state = await Poll.UntilAsync(
            _ => Task.FromResult(clock.UtcNow - Start >= TimeSpan.FromSeconds(250) ? "Running" : "Starting"),
            observed => observed == "Running",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(25),
            "cluster aks-platform-nonprod to be Running",
            clock,
            progress: lines.Add);

        state.ShouldBe("Running");
        lines.ShouldBe(
        [
            "progress: waiting cluster aks-platform-nonprod to be Running state=Starting elapsed 1:00/5:00",
            "progress: waiting cluster aks-platform-nonprod to be Running state=Starting elapsed 2:00/5:00",
            "progress: waiting cluster aks-platform-nonprod to be Running state=Starting elapsed 3:00/5:00",
            "progress: waiting cluster aks-platform-nonprod to be Running state=Starting elapsed 4:00/5:00",
        ]);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public async Task Should_UntilAsync_IntervalLongerThanAMinute_CutsThePausesToReportEveryMinute()
    {
        var clock = new StubClock(Start);
        var lines = new List<string>();

        await Should.ThrowAsync<PollTimeoutException>(() => Poll.UntilAsync(
            _ => Task.FromResult(false),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(150),
            "Codefresh build 1 to finish",
            clock,
            progress: lines.Add));

        clock.Delays.ShouldBe([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)]);
        lines.Select(line => line[line.LastIndexOf(' ')..]).ShouldBe([" 1:00/5:00", " 2:00/5:00", " 3:00/5:00", " 4:00/5:00", " 5:00/5:00"]);
        lines[0].ShouldBe("progress: waiting Codefresh build 1 to finish state=False elapsed 1:00/5:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public async Task Should_UntilAsync_RetriedError_ReportsTheErrorAsTheState()
    {
        var clock = new StubClock(Start);
        var lines = new List<string>();

        await Should.ThrowAsync<PollTimeoutException>(() => Poll.UntilAsync<string>(
            _ => throw new HttpRequestException("connection refused"),
            _ => true,
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(30),
            "the API server to answer",
            clock,
            retryWhen: error => error is HttpRequestException,
            progress: lines.Add));

        lines.ShouldBe(["progress: waiting the API server to answer state=error HttpRequestException: connection refused elapsed 1:00/1:30"]);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public async Task Should_DelayAsync_FifteenMinuteGrace_ReportsEveryMinuteToTheEnd()
    {
        var clock = new StubClock(Start);
        var lines = new List<string>();

        await Poll.DelayAsync(TimeSpan.FromMinutes(15), "the stop grace of aks-platform-nonprod", clock, lines.Add);

        clock.UtcNow.ShouldBe(Start + TimeSpan.FromMinutes(15));
        lines.Count.ShouldBe(15);
        lines[0].ShouldBe("progress: waiting the stop grace of aks-platform-nonprod state=waiting elapsed 1:00/15:00");
        lines[^1].ShouldBe("progress: waiting the stop grace of aks-platform-nonprod state=waiting elapsed 15:00/15:00");
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public async Task Should_DelayAsync_StubClockWithoutReporter_WaitsOnceAndWritesNothing()
    {
        var clock = new StubClock(Start);

        await Poll.DelayAsync(TimeSpan.FromMinutes(15), "a grace", clock);

        clock.Delays.ShouldBe([TimeSpan.FromMinutes(15)]);
    }
}

/// <summary>Proves that the run's total is the count of tests the filter selected, read inside NUnit.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class SelectedTestsTests
{
    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Count_InsideThisRun_ReadsTheRunFilterAndCountsThisTest()
    {
        var count = SelectedTests.Count(TestExecutionContext.CurrentContext.CurrentTest);

        count.ShouldNotBeNull("NUnit's dispatcher no longer exposes the run's filter where SelectedTests reads it");
        count.Value.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Count_EmptyFilterOnThisFixture_CountsEveryTestOfIt()
    {
        var fixture = TestExecutionContext.CurrentContext.CurrentTest.Parent!;

        var count = SelectedTests.Count(fixture, TestFilter.Empty);

        count.ShouldBe(fixture.TestCaseCount);
    }

    [Test]
    [Capability("CAP-HARNESS-013")]
    public void Should_Tracker_ThisAssembly_IsInstalledWithTheSelectedTotal()
    {
        var tracker = ConformanceProgress.Tracker;

        tracker.ShouldNotBeNull("[assembly: ConformanceProgress] is missing from Platform.Conformance.Offline");
        (tracker.Assembly, tracker.Label, tracker.EveryPercent).ShouldBe(("Platform.Conformance.Offline", "offline", 10));
        tracker.Total.ShouldBe(SelectedTests.Count(TestExecutionContext.CurrentContext.CurrentTest));
    }
}
