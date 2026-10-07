using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Kit.ReleaseStall;

/// <summary>
/// CAP-KIT-012, the decision: <c>scripts/release/release-stall-check.ps1</c> run whole, in a real <c>pwsh</c>, over pin
/// lines as <c>git log --format='%H %cI %s'</c> prints them. The recorded lines are this repository's own history of
/// 2026-09-29 to 2026-10-07, in which eleven releases of one app never reached the next environment and nobody was told
/// (#86); the other cases (threshold, freeze, supersession, version order, rollback, a last environment, a new app) use
/// small synthetic logs.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ReleaseStallCheckTests
{
    private const string App = "workorders";
    private const string Tuesday = "2026-09-22";

    private ReleaseStallWorkspace workspace = null!;

    /// <summary>Creates the workspace.</summary>
    [SetUp]
    public void CreateWorkspace() => workspace = new ReleaseStallWorkspace();

    /// <summary>Deletes the workspace.</summary>
    [TearDown]
    public void DeleteWorkspace() => workspace.Dispose();

    /// <summary>
    /// The recorded history, judged at moments of that week: 2.5.759 reached prod; 2.5.761 to 2.5.765 and 2.5.767 to 2.5.770
    /// stopped after uat and 2.5.766 and 2.5.771 after tdd; 2.5.772 and 2.5.773 reached prod. Only the newest release of an
    /// environment is ever reported, and only after the threshold.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_RecordedHistory_NamesTheRealStallsAndNothingElse()
    {
        (string Now, string[] States)[] moments =
        [
            ("2026-09-30T12:00:00Z", [$"{App} 2.5.759 tdd>uat Promoted", $"{App} 2.5.759 uat>prod Promoted"]),
            ("2026-09-30T14:40:00Z", [$"{App} 2.5.762 tdd>uat Promoted", $"{App} 2.5.762 uat>prod Stalled"]),
            ("2026-10-05T10:50:00Z", [$"{App} 2.5.766 tdd>uat Waiting", $"{App} 2.5.765 uat>prod Waiting"]),
            ("2026-10-05T13:00:00Z", [$"{App} 2.5.767 tdd>uat Promoted", $"{App} 2.5.767 uat>prod Stalled"]),
            ("2026-10-06T12:00:00Z", [$"{App} 2.5.770 tdd>uat Promoted", $"{App} 2.5.770 uat>prod Stalled"]),
            ("2026-10-06T18:45:00Z", [$"{App} 2.5.771 tdd>uat Stalled", $"{App} 2.5.770 uat>prod Stalled"]),
            ("2026-10-06T21:20:00Z", [$"{App} 2.5.772 tdd>uat Promoted", $"{App} 2.5.772 uat>prod Waiting"]),
            ("2026-10-06T22:00:00Z", [$"{App} 2.5.772 tdd>uat Promoted", $"{App} 2.5.772 uat>prod Promoted"]),
            ("2026-10-07T04:00:00Z", [$"{App} 2.5.773 tdd>uat Promoted", $"{App} 2.5.773 uat>prod Promoted"]),
        ];

        foreach (var (now, states) in moments)
        {
            var result = workspace.Check(ReleaseStallWorkspace.RecordedLog, now, "-All");

            ReleaseStallWorkspace.States(result).ShouldBe(states, $"as of {now}: {result.Transcript}");
            var stalls = states.Count(state => state.EndsWith(" Stalled", StringComparison.Ordinal));
            result.ExitCode.ShouldBe(stalls > 0 ? 1 : 0, result.Transcript);
            Regex.Matches(result.Output, "(?m)^STALL ").Count.ShouldBe(stalls, result.Transcript);
        }
    }

    /// <summary>A stall is one line for people and one object for tools, each with app, version, both environments, the pin commit, the deployment ID and the age.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_OneStall_WritesTheLineAndTheObjectWithEveryField()
    {
        var result = workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-06T12:00:00Z");

        result.ExitCode.ShouldBe(1, result.Transcript);
        result.Output.ShouldContain($"STALL {App} 2.5.770: stuck in uat, not pinned in prod; pin c6af0d9 (Deployments-55149) at 2026-10-06T03:38:11Z, age 8h 21m");
        result.Output.ShouldContain("FAIL release-stall: 1 stalled release(s); 1 app(s) from");
        var row = ReleaseStallWorkspace.Rows(result).ShouldHaveSingleItem();
        ReleaseStallWorkspace.Text(row, "App").ShouldBe(App);
        ReleaseStallWorkspace.Text(row, "Version").ShouldBe("2.5.770");
        ReleaseStallWorkspace.Text(row, "Environment").ShouldBe("uat");
        ReleaseStallWorkspace.Text(row, "NextEnvironment").ShouldBe("prod");
        ReleaseStallWorkspace.Text(row, "State").ShouldBe("Stalled");
        ReleaseStallWorkspace.Text(row, "Commit").ShouldBe("c6af0d9ba44fcb307ee4714432dfa8e9b194066d");
        ReleaseStallWorkspace.Text(row, "DeploymentId").ShouldBe("Deployments-55149");
        ReleaseStallWorkspace.Text(row, "PinnedAt").ShouldBe("2026-10-06T03:38:11Z");
        ReleaseStallWorkspace.Number(row, "AgeMinutes").ShouldBe(501);
        ReleaseStallWorkspace.Number(row, "WaitingMinutes").ShouldBe(501);
        ReleaseStallWorkspace.Number(row, "ThresholdMinutes").ShouldBe(90);
        ReleaseStallWorkspace.Text(row, "Title").ShouldBe($"Release 2.5.770 of {App} has not left uat");

        var quiet = workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-07T04:00:00Z");
        quiet.ExitCode.ShouldBe(0, quiet.Transcript);
        quiet.Output.ShouldContain("PASS release-stall: no stalled release; 1 app(s) from");
        ReleaseStallWorkspace.Rows(quiet).ShouldBeEmpty();
    }

    /// <summary>A release is stalled only when it waited longer than the threshold; the default is 90 minutes and -ThresholdMinutes changes it.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_Threshold_IsNinetyMinutesByDefaultAndConfigurable()
    {
        workspace.App("shop", "tdd", "uat", "prod");
        var log = workspace.Log(ReleaseStallWorkspace.Pin($"{Tuesday}T10:00:00Z", "shop", "1.0.0", "tdd"));

        ReleaseStallWorkspace.States(workspace.Check(log, $"{Tuesday}T11:30:00Z", "-All")).ShouldBe(["shop 1.0.0 tdd>uat Waiting"]);
        ReleaseStallWorkspace.States(workspace.Check(log, $"{Tuesday}T11:31:00Z", "-All")).ShouldBe(["shop 1.0.0 tdd>uat Stalled"]);
        ReleaseStallWorkspace.States(workspace.Check(log, $"{Tuesday}T10:30:00Z", "-All", "-ThresholdMinutes", "30")).ShouldBe(["shop 1.0.0 tdd>uat Waiting"]);
        var short30 = workspace.Check(log, $"{Tuesday}T10:31:00Z", "-ThresholdMinutes", "30");
        ReleaseStallWorkspace.Number(ReleaseStallWorkspace.Rows(short30).ShouldHaveSingleItem(), "ThresholdMinutes").ShouldBe(30);
    }

    /// <summary>
    /// The prod weekend freeze (Saturday 00:00 to Monday 00:00 UTC, from its first window on) is no stall while it lasts,
    /// and its minutes do not count afterwards; it concerns promotions into prod only.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_ProdWeekendFreeze_IsNoStallWhileItLastsAndItsMinutesDoNotCount()
    {
        // Recorded: 2.5.762 waited in uat since Wednesday; 2.5.763 was pinned in uat on Saturday 2026-10-03 at 07:30:26.
        ReleaseStallWorkspace.States(workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-02T23:59:00Z", "-All")).ShouldContain($"{App} 2.5.762 uat>prod Stalled");
        ReleaseStallWorkspace.States(workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-03T06:00:00Z", "-All")).ShouldContain($"{App} 2.5.762 uat>prod Frozen");
        var saturday = workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-03T12:00:00Z", "-All");
        ReleaseStallWorkspace.States(saturday).ShouldBe([$"{App} 2.5.763 tdd>uat Promoted", $"{App} 2.5.763 uat>prod Frozen"]);
        saturday.ExitCode.ShouldBe(0, saturday.Transcript);
        saturday.Output.ShouldContain("promotions into prod are frozen until 2026-10-05T00:00:00Z");
        ReleaseStallWorkspace.States(workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-05T01:30:00Z", "-All")).ShouldContain($"{App} 2.5.763 uat>prod Waiting");
        var monday = workspace.Check(ReleaseStallWorkspace.RecordedLog, "2026-10-05T01:31:00Z");
        var row = ReleaseStallWorkspace.Rows(monday).ShouldHaveSingleItem();
        ReleaseStallWorkspace.Text(row, "Version").ShouldBe("2.5.763");
        ReleaseStallWorkspace.Number(row, "AgeMinutes").ShouldBe(2520);
        ReleaseStallWorkspace.Number(row, "WaitingMinutes").ShouldBe(91);
        monday.Output.ShouldContain("age 1d 18h 00m (1h 31m outside the prod freeze)");

        // Synthetic: a later weekend, a pin into tdd on a Saturday, and a Saturday before the first window.
        workspace.App("shop", "tdd", "uat", "prod").App("fresh", "tdd", "uat", "prod").App("early", "tdd", "uat", "prod");
        var log = workspace.Log(
            ReleaseStallWorkspace.Pin("2026-09-26T10:00:00Z", "early", "1.0.0", "tdd"),
            ReleaseStallWorkspace.Pin("2026-09-26T10:05:00Z", "early", "1.0.0", "uat"),
            ReleaseStallWorkspace.Pin("2026-10-16T22:00:00Z", "shop", "1.0.0", "tdd"),
            ReleaseStallWorkspace.Pin("2026-10-16T23:00:00Z", "shop", "1.0.0", "uat"),
            ReleaseStallWorkspace.Pin("2026-10-17T10:00:00Z", "fresh", "0.1.0", "tdd"));

        ReleaseStallWorkspace.States(workspace.Check(log, "2026-09-26T13:00:00Z", "-All")).ShouldBe(["early 1.0.0 tdd>uat Promoted", "early 1.0.0 uat>prod Stalled"]);
        var frozen = ReleaseStallWorkspace.States(workspace.Check(log, "2026-10-17T12:00:00Z", "-All"));
        frozen.ShouldContain("shop 1.0.0 uat>prod Frozen");
        frozen.ShouldContain("fresh 0.1.0 tdd>uat Stalled", "the freeze concerns promotions into prod only");
        frozen.ShouldContain("early 1.0.0 uat>prod Frozen");
        ReleaseStallWorkspace.States(workspace.Check(log, "2026-10-19T00:20:00Z", "-All")).ShouldContain("shop 1.0.0 uat>prod Waiting");
        var after = ReleaseStallWorkspace.Rows(workspace.Check(log, "2026-10-19T00:31:00Z")).Single(stall => ReleaseStallWorkspace.Text(stall, "App") == "shop");
        ReleaseStallWorkspace.Number(after, "AgeMinutes").ShouldBe(2971);
        ReleaseStallWorkspace.Number(after, "WaitingMinutes").ShouldBe(91);
        ReleaseStallWorkspace.States(workspace.Check(log, "2026-10-17T12:00:00Z", "-All", "-FreezeEnvironment", string.Empty)).ShouldContain("shop 1.0.0 uat>prod Stalled");
    }

    /// <summary>The script's freeze defaults are the first window of the Terraform variable prod_freeze_first_window: a Saturday 00:00 UTC and the Monday after it.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_FreezeDefaults_ScriptAndTerraform_NameTheSameFirstWindow()
    {
        var terraform = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, "octopus", "terraform", "variables.tf"));
        var script = File.ReadAllText(Path.Combine(KitToolbox.RepositoryRoot, ReleaseStallWorkspace.Script));

        var window = Regex.Match(terraform, "variable \"prod_freeze_first_window\"[\\s\\S]*?default\\s*=\\s*\\{\\s*start\\s*=\\s*\"([^\"]+)\"\\s*end\\s*=\\s*\"([^\"]+)\"");
        window.Success.ShouldBeTrue("octopus/terraform/variables.tf declares prod_freeze_first_window with a default");
        Regex.Match(script, @"\$FreezeFirstWindowStart = '([^']+)'").Groups[1].Value.ShouldBe(window.Groups[1].Value);
        Regex.Match(script, @"\$FreezeFirstWindowEnd = '([^']+)'").Groups[1].Value.ShouldBe(window.Groups[2].Value);
        Regex.Match(script, @"\$FreezeEnvironment = '([^']+)'").Groups[1].Value.ShouldBe("prod");
        var start = DateTimeOffset.Parse(window.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        var end = DateTimeOffset.Parse(window.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        start.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
        start.TimeOfDay.ShouldBe(TimeSpan.Zero);
        (end - start).ShouldBe(TimeSpan.FromHours(48));
    }

    /// <summary>A newer release in the same environment takes the place of an older stalled one: only the newest is reported, and nothing once it moved on.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_NewerReleaseInTheSameEnvironment_ReportsOnlyTheNewest()
    {
        workspace.App("shop", "tdd", "uat", "prod");
        var older = ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "shop", "1.0.9", "tdd", 1);
        var newer = ReleaseStallWorkspace.Pin($"{Tuesday}T10:00:00Z", "shop", "1.0.10", "tdd", 2);

        ReleaseStallWorkspace.States(workspace.Check(workspace.Log(older), $"{Tuesday}T09:45:00Z", "-All")).ShouldBe(["shop 1.0.9 tdd>uat Stalled"]);
        ReleaseStallWorkspace.States(workspace.Check(workspace.Log(older, newer), $"{Tuesday}T10:30:00Z", "-All")).ShouldBe(["shop 1.0.10 tdd>uat Waiting"]);
        var stalled = workspace.Check(workspace.Log(older, newer), $"{Tuesday}T12:00:00Z", "-All");
        ReleaseStallWorkspace.States(stalled).ShouldBe(["shop 1.0.10 tdd>uat Stalled"]);
        stalled.Output.ShouldNotContain("1.0.9");
        var promoted = workspace.Log(older, newer, ReleaseStallWorkspace.Pin($"{Tuesday}T10:10:00Z", "shop", "1.0.10", "uat", 3));
        ReleaseStallWorkspace.States(workspace.Check(promoted, $"{Tuesday}T12:00:00Z", "-All")).ShouldBe(["shop 1.0.10 tdd>uat Promoted", "shop 1.0.10 uat>prod Stalled"]);
    }

    /// <summary>Versions compare as versions: numbers as numbers, a release above its pre-releases, pre-release numbers as numbers; a name that is no version is ignored with a warning.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_Versions_CompareAsVersionsNotAsText()
    {
        foreach (var app in (string[])["numbers", "release", "hotfix", "four", "odd"])
        {
            workspace.App(app, "tdd", "uat");
        }

        var log = workspace.Log(
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "numbers", "2.5.99", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:01:00Z", "release", "1.2.3-hotfix.5", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:02:00Z", "hotfix", "1.2.3-hotfix.9", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:03:00Z", "four", "1.2.3.9", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:04:00Z", "odd", "sha-0a1b2c3", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:00:00Z", "numbers", "2.5.100", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:01:00Z", "release", "1.2.3", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:02:00Z", "hotfix", "1.2.3-hotfix.10", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:03:00Z", "four", "1.2.3.10", "tdd"));

        var result = workspace.Check(log, $"{Tuesday}T12:00:00Z", "-All");

        ReleaseStallWorkspace.States(result).ShouldBe(
        [
            "four 1.2.3.10 tdd>uat Stalled", "hotfix 1.2.3-hotfix.10 tdd>uat Stalled", "numbers 2.5.100 tdd>uat Stalled", "release 1.2.3 tdd>uat Stalled",
        ]);
        result.Output.ShouldContain("WARN release-stall: odd sha-0a1b2c3 is no version this check can order; its pins are ignored");
    }

    /// <summary>
    /// A rollback pin (an older version pinned again) never makes the older version the newest: the environment is reported
    /// as rolled back, not as a stall, until the newer release is pinned again; a rollback in the next environment does not
    /// undo a promotion.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_RollbackPin_IsNoStallAndDoesNotMakeTheOlderVersionTheNewest()
    {
        workspace.App("shop", "tdd", "uat", "prod").App("store", "tdd", "uat", "prod");
        string[] history =
        [
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "shop", "1.0.1", "tdd", 1),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:10:00Z", "shop", "1.0.1", "uat", 2),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:20:00Z", "shop", "1.0.1", "prod", 3),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:00:00Z", "shop", "1.0.2", "tdd", 4),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:30:00Z", "shop", "1.0.1", "tdd", 5),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "store", "2.0.1", "tdd", 6),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:10:00Z", "store", "2.0.1", "uat", 7),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:20:00Z", "store", "2.0.1", "prod", 8),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:40:00Z", "store", "2.0.0", "prod", 9),
        ];

        var rolledBack = workspace.Check(workspace.Log(history), $"{Tuesday}T12:00:00Z", "-All");

        rolledBack.ExitCode.ShouldBe(0, rolledBack.Transcript);
        ReleaseStallWorkspace.States(rolledBack).ShouldBe(
        [
            "shop 1.0.2 tdd>uat RolledBack", "shop 1.0.1 uat>prod Promoted", "store 2.0.1 tdd>uat Promoted", "store 2.0.1 uat>prod Promoted",
        ]);
        rolledBack.Output.ShouldContain($"tdd was rolled back to 1.0.1 by {ReleaseStallWorkspace.Short(history[4])} at {Tuesday}T09:30:00Z (Deployments-5)");

        var again = ReleaseStallWorkspace.Pin($"{Tuesday}T11:00:00Z", "shop", "1.0.2", "tdd", 10);
        var forward = workspace.Log([.. history, again]);
        ReleaseStallWorkspace.States(workspace.Check(forward, $"{Tuesday}T12:00:00Z", "-All")).ShouldContain("shop 1.0.2 tdd>uat Waiting");
        var stalled = ReleaseStallWorkspace.Rows(workspace.Check(forward, $"{Tuesday}T12:31:00Z")).ShouldHaveSingleItem();
        ReleaseStallWorkspace.Text(stalled, "Version").ShouldBe("1.0.2");
        ReleaseStallWorkspace.Text(stalled, "PinnedAt").ShouldBe($"{Tuesday}T11:00:00Z");
        ReleaseStallWorkspace.Text(stalled, "DeploymentId").ShouldBe("Deployments-10");
        ReleaseStallWorkspace.Text(stalled, "Commit").ShouldStartWith(ReleaseStallWorkspace.Short(again));
    }

    /// <summary>
    /// The last environment of an app has no next one: a version pinned in prod directly is no stall and stops an older
    /// release in uat from being one; an app without prod ends at uat; an app with one environment or without a folder is
    /// not checked; an app without uat goes from tdd to prod.
    /// </summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_LastEnvironmentAndDirectPins_AreNeverStalls()
    {
        workspace.App("shop", "tdd", "uat", "prod").App("kiosk", "tdd", "uat").App("lab", "tdd").App("skip", "tdd", "prod");
        var log = workspace.Log(
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "shop", "3.0.0", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:10:00Z", "shop", "3.0.0", "uat"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T09:00:00Z", "shop", "3.0.1", "prod"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "kiosk", "1.0.0", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:10:00Z", "kiosk", "1.0.0", "uat"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:20:00Z", "kiosk", "0.9.0", "prod"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "lab", "1.0.0", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "gone", "1.0.0", "tdd"),
            ReleaseStallWorkspace.Pin($"{Tuesday}T08:00:00Z", "skip", "1.0.0", "tdd"));

        var result = workspace.Check(log, $"{Tuesday}T12:00:00Z", "-All");

        ReleaseStallWorkspace.States(result).ShouldBe(
        [
            "kiosk 1.0.0 tdd>uat Promoted", "shop 3.0.0 tdd>uat Promoted", "shop 3.0.0 uat>prod Bypassed", "skip 1.0.0 tdd>prod Stalled",
        ]);
        result.Output.ShouldContain("prod already holds the newer version 3.0.1");
        result.Output.ShouldContain("not checked: gone (no gone/envs/<environment> under -AppsRoot)");
    }

    /// <summary>The first pin of a new app, with no other pin anywhere, waits for the threshold and is then a stall like any other.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_FirstPinOfANewApp_WaitsThenStalls()
    {
        workspace.App("fresh", "tdd", "uat", "prod");
        var log = workspace.Log(ReleaseStallWorkspace.Pin($"{Tuesday}T10:00:00Z", "fresh", "0.1.0", "tdd", 7));

        var waiting = workspace.Check(log, $"{Tuesday}T10:30:00Z", "-All");
        waiting.ExitCode.ShouldBe(0, waiting.Transcript);
        ReleaseStallWorkspace.States(waiting).ShouldBe(["fresh 0.1.0 tdd>uat Waiting"]);
        var stalled = workspace.Check(log, $"{Tuesday}T12:00:00Z");
        stalled.ExitCode.ShouldBe(1, stalled.Transcript);
        stalled.Error.ShouldBeEmpty();
        ReleaseStallWorkspace.Text(ReleaseStallWorkspace.Rows(stalled).ShouldHaveSingleItem(), "Title").ShouldBe("Release 0.1.0 of fresh has not left tdd");

        var nothing = workspace.Check(workspace.Log("# no pin commit yet"), $"{Tuesday}T12:00:00Z", "-All");
        nothing.ExitCode.ShouldBe(0, nothing.Transcript);
        ReleaseStallWorkspace.Rows(nothing).ShouldBeEmpty();
    }

    /// <summary>The conformance fixture, whose releases stay in tdd on purpose, is not checked unless -ExcludeApp says otherwise.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_ConformanceFixture_IsExcludedByDefault()
    {
        workspace.App("sandbox", "tdd", "uat", "prod");
        var log = workspace.Log(ReleaseStallWorkspace.Pin($"{Tuesday}T10:00:00Z", "sandbox", "0.1.32", "tdd"));

        var standard = workspace.Check(log, $"{Tuesday}T12:00:00Z", "-All");
        standard.ExitCode.ShouldBe(0, standard.Transcript);
        standard.Output.ShouldContain("not checked: sandbox (excluded)");
        ReleaseStallWorkspace.Rows(standard).ShouldBeEmpty();
        ReleaseStallWorkspace.States(workspace.Check(log, $"{Tuesday}T12:00:00Z", "-All", "-ExcludeApp", string.Empty)).ShouldBe(["sandbox 0.1.32 tdd>uat Stalled"]);
    }

    /// <summary>The pins come from git log of a checked-out repository by default, from the pipeline or from a file, and every source gives the same answer; other commits are ignored.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReadPins_GitLogPipelineAndFile_GiveTheSameAnswer()
    {
        var git = KitToolbox.Find("git") ?? KitToolbox.Require("git");
        var repository = Path.Combine(workspace.Root, "repository");
        Directory.CreateDirectory(Path.Combine(repository, "gitops", "apps", "shop", "envs", "tdd"));
        Directory.CreateDirectory(Path.Combine(repository, "gitops", "apps", "shop", "envs", "uat"));
        Git(git, repository, null, "init", "--quiet", "--initial-branch=main");
        (string When, string Subject)[] commits =
        [
            ($"{Tuesday}T07:00:00Z", "Pin writer mints a per-run token (#74)"),
            ($"{Tuesday}T08:00:00Z", "Pin shop 1.0.0 in tdd (Deployments-11)"),
            ($"{Tuesday}T08:10:00Z", "Pin shop 1.0.0 in uat (Deployments-12)"),
            ($"{Tuesday}T09:00:00Z", "Merge pull request #1 from example/branch"),
            ($"{Tuesday}T10:00:00Z", "Pin shop 1.0.1 in tdd (Deployments-13)"),
        ];
        foreach (var (when, subject) in commits)
        {
            Git(git, repository, when, "-c", "user.name=Octopus", "-c", "user.email=octopus@example.invalid", "commit", "--quiet", "--allow-empty", "-m", subject);
        }

        string[] common = ["-Now", $"{Tuesday}T12:00:00Z", "-Json"];
        var fromGit = ReleaseStallWorkspace.Run(null, [], ["-Root", repository, .. common]);
        var lines = KitToolbox.Run(git, ["log", "--format=%H %cI %s"], repository).Output;
        var fromPipeline = ReleaseStallWorkspace.Run(lines, [], ["-AppsRoot", Path.Combine(repository, "gitops", "apps"), .. common]);
        var file = Path.Combine(workspace.Root, "git.log");
        File.WriteAllText(file, lines);
        var fromFile = ReleaseStallWorkspace.Run(null, [], ["-PinLog", file, "-AppsRoot", Path.Combine(repository, "gitops", "apps"), .. common]);

        fromGit.ExitCode.ShouldBe(1, fromGit.Transcript);
        fromGit.Output.ShouldContain("from git log HEAD");
        fromPipeline.Output.ShouldContain("from the pipeline");
        var expected = ReleaseStallWorkspace.Rows(fromGit).ToJsonString();
        ReleaseStallWorkspace.Text(ReleaseStallWorkspace.Rows(fromGit).ShouldHaveSingleItem(), "Title").ShouldBe("Release 1.0.1 of shop has not left tdd");
        ReleaseStallWorkspace.Rows(fromPipeline).ToJsonString().ShouldBe(expected);
        ReleaseStallWorkspace.Rows(fromFile).ToJsonString().ShouldBe(expected);
    }

    /// <summary>Input that cannot be read is a usage error (exit 2) with a message, never a silent pass.</summary>
    [Test]
    [Capability("CAP-KIT-012")]
    public void Should_ReportStalls_UnreadableInput_ExitsTwo()
    {
        var badTime = workspace.Check(ReleaseStallWorkspace.RecordedLog, "yesterday");
        badTime.ExitCode.ShouldBe(2, badTime.Transcript);
        badTime.Output.ShouldContain("FAIL release-stall: -Now 'yesterday' is not an ISO 8601 time");

        var noLog = workspace.Check(Path.Combine(workspace.Root, "missing.log"), $"{Tuesday}T12:00:00Z");
        noLog.ExitCode.ShouldBe(2, noLog.Transcript);
        noLog.Output.ShouldContain("(-PinLog)");

        var noHistory = ReleaseStallWorkspace.Run(null, [], "-Root", workspace.Root, "-Ref", "no-such-ref", "-AppsRoot", workspace.AppsRoot);
        noHistory.ExitCode.ShouldBe(2, noHistory.Transcript);
        noHistory.Output.ShouldContain("FAIL release-stall: git log no-such-ref failed");
    }

    private static void Git(string git, string repository, string? when, params string[] arguments)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_SYSTEM"] = "/dev/null" };
        if (when is not null)
        {
            environment["GIT_AUTHOR_DATE"] = when;
            environment["GIT_COMMITTER_DATE"] = when;
        }

        var result = KitToolbox.Run(git, arguments, repository, TimeSpan.FromMinutes(1), environment);
        result.ExitCode.ShouldBe(0, result.Transcript);
    }
}
