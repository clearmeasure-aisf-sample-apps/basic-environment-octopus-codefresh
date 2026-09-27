using System.Globalization;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus.Runbooks;

/// <summary>
/// CAP-OCT-009 (offline half): runbook sleep-hold under the stub Octopus runtime. It sets the hold that env-sleep's step
/// Decide sleep honours (tags platform-sleep-hold-until and platform-sleep-hold-by on rg-platform-&lt;tier&gt;-aks, through
/// az tag update, which touches no other tag), releases it with 0 minutes, validates its prompts before any Azure call
/// and fails the step when Azure refuses.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
[Parallelizable(ParallelScope.All)]
public class SleepHoldScriptTests
{
    private const string Runbook = ".octopus/platform-infrastructure/runbooks/sleep-hold.ocl";
    private const string Holder = "conformance:r1-test";

    /// <summary>A hold of some minutes merges both tags, ending that many minutes from now, and outputs the end.</summary>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    /// <param name="minutes">Sleep.HoldMinutes.</param>
    [TestCase("nonprod", 480)]
    [TestCase("prod", 720)]
    [TestCase("nonprod", 1)]
    [Capability("CAP-OCT-009")]
    public void Should_SetHold_Minutes_MergesBothTagsEndingThatManyMinutesFromNow(string tier, int minutes)
    {
        var script = Hold(tier, minutes.ToString(CultureInfo.InvariantCulture), Holder).Group(tier).Reply(TagUpdate(tier, "Merge"));

        var before = DateTimeOffset.UtcNow;
        var run = script.Run();
        var after = DateTimeOffset.UtcNow;

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Calls.Select(call => call.Tool).ShouldBe(["az", "az"], run.Transcript);
        run.Calls[0].Line.ShouldBe($"az group show --name rg-platform-{tier}-aks --output json --only-show-errors");
        var until = run.Outputs["Sleep.HoldUntil"];
        run.Calls[1].Line.ShouldBe(
            $"az tag update --resource-id {GroupId(tier)} --operation Merge --tags platform-sleep-hold-until={until} platform-sleep-hold-by={Holder} --output none --only-show-errors");
        until.ShouldMatch(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
        var end = DateTimeOffset.Parse(until, CultureInfo.InvariantCulture);
        end.ShouldBeGreaterThanOrEqualTo(before.AddMinutes(minutes).AddSeconds(-1));
        end.ShouldBeLessThanOrEqualTo(after.AddMinutes(minutes));
        run.Highlights.ShouldBe([$"Sleep hold on rg-platform-{tier}-aks set by {Holder}: env-sleep keeps aks-platform-{tier} up until {until} unless forced (was no hold)."], run.Transcript);
    }

    /// <summary>0 minutes deletes exactly the two hold tags with their current values; the cost tags stay.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_ReleaseHold_ZeroMinutes_DeletesOnlyTheHoldTags()
    {
        var script = Hold("nonprod", "0", Holder)
            .Group("nonprod", new() { ["platform-sleep-hold-until"] = "2026-09-28T15:00:00Z", ["platform-sleep-hold-by"] = Holder })
            .Reply(TagUpdate("nonprod", "Delete"));

        var run = script.Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.Calls.Select(call => call.Line).ShouldBe(
        [
            "az group show --name rg-platform-nonprod-aks --output json --only-show-errors",
            $"az tag update --resource-id {GroupId("nonprod")} --operation Delete --tags platform-sleep-hold-until=2026-09-28T15:00:00Z platform-sleep-hold-by={Holder} --output none --only-show-errors",
        ], run.Transcript);
        run.Outputs["Sleep.HoldUntil"].ShouldBeEmpty();
        run.Highlights.ShouldBe(
            [$"Sleep hold on rg-platform-nonprod-aks released by {Holder}: was held by {Holder} until 2026-09-28T15:00:00Z. env-sleep applies its normal rules to aks-platform-nonprod again."],
            run.Transcript);
    }

    /// <summary>0 minutes leaves a hold of another holder in place: a run's teardown must not end an operator's hold.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_ReleaseHold_OtherHolder_LeavesTheHold()
    {
        var run = Hold("nonprod", "0", Holder)
            .Group("nonprod", new() { ["platform-sleep-hold-until"] = "2026-09-28T15:00:00Z", ["platform-sleep-hold-by"] = "demo:2026-09-28" })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("tag update").ShouldBeEmpty(run.Transcript);
        run.Highlights.ShouldBe(
            [$"Sleep hold on rg-platform-nonprod-aks left in place: held by demo:2026-09-28 until 2026-09-28T15:00:00Z, not by {Holder}."],
            run.Transcript);
    }

    /// <summary>A hold of another holder that ends later stays as it is and is the output; the new holder does not take it over.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_SetHold_LaterHoldOfOtherHolder_LeavesTheHold()
    {
        var run = Hold("prod", "60", Holder)
            .Group("prod", new() { ["platform-sleep-hold-until"] = "2099-01-01T00:00:00Z", ["platform-sleep-hold-by"] = "demo:2026-09-28" })
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("tag update").ShouldBeEmpty(run.Transcript);
        run.Outputs["Sleep.HoldUntil"].ShouldBe("2099-01-01T00:00:00Z");
        run.Highlights.ShouldBe(
            [$"Sleep hold on rg-platform-prod-aks left in place: held by demo:2026-09-28 until 2099-01-01T00:00:00Z covers {Holder}."],
            run.Transcript);
    }

    /// <summary>A hold of another holder that ends sooner is replaced by the longer hold.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_SetHold_EarlierHoldOfOtherHolder_ReplacesIt()
    {
        var run = Hold("prod", "60", Holder)
            .Group("prod", new() { ["platform-sleep-hold-until"] = "2020-01-01T00:00:00Z", ["platform-sleep-hold-by"] = "demo:2026-09-28" })
            .Reply(TagUpdate("prod", "Merge"))
            .Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching($"--operation Merge .* platform-sleep-hold-by={Holder} ").Count.ShouldBe(1, run.Transcript);
    }

    /// <summary>0 minutes without a hold changes nothing.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_ReleaseHold_NoHold_ChangesNothing()
    {
        var run = Hold("prod", "0", "jane").Group("prod").Run();

        run.Succeeded.ShouldBeTrue(run.Transcript);
        run.CallsMatching("tag update").ShouldBeEmpty(run.Transcript);
        run.Highlights.ShouldBe(["No sleep hold on rg-platform-prod-aks; nothing to release (jane)."], run.Transcript);
    }

    /// <summary>Invalid prompts fail the step before any Azure call.</summary>
    /// <param name="minutes">Sleep.HoldMinutes.</param>
    /// <param name="holder">Sleep.HoldBy.</param>
    /// <param name="failure">Expected failure.</param>
    [TestCase("721", Holder, "Sleep.HoldMinutes must be a whole number from 0 (release) to 720; got '721'.")]
    [TestCase("-5", Holder, "Sleep.HoldMinutes must be a whole number from 0 (release) to 720; got '-5'.")]
    [TestCase("1.5", Holder, "Sleep.HoldMinutes must be a whole number from 0 (release) to 720; got '1.5'.")]
    [TestCase("", Holder, "Sleep.HoldMinutes must be a whole number from 0 (release) to 720; got ''.")]
    [TestCase("60", "", "Sleep.HoldBy must be 1 to 64 letters, digits, ':', '.', '_' or '-', for example conformance:r20260928t0700-abcd1234; got ''.")]
    [TestCase("60", "jane doe", "Sleep.HoldBy must be 1 to 64 letters, digits, ':', '.', '_' or '-', for example conformance:r20260928t0700-abcd1234; got 'jane doe'.")]
    [TestCase("0", "a/b", "Sleep.HoldBy must be 1 to 64 letters, digits, ':', '.', '_' or '-', for example conformance:r20260928t0700-abcd1234; got 'a/b'.")]
    [TestCase("60", "x12345678901234567890123456789012345678901234567890123456789012345",
        "Sleep.HoldBy must be 1 to 64 letters, digits, ':', '.', '_' or '-', for example conformance:r20260928t0700-abcd1234; got 'x12345678901234567890123456789012345678901234567890123456789012345'.")]
    [Capability("CAP-OCT-009")]
    public void Should_SetHold_InvalidInput_FailsBeforeAnyCall(string minutes, string holder, string failure)
    {
        var run = Hold("nonprod", minutes, holder).Group("nonprod").Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Calls.ShouldBeEmpty(run.Transcript);
    }

    /// <summary>An az call that fails fails the step: the group cannot be read, or the tags cannot be written.</summary>
    /// <param name="minutes">Sleep.HoldMinutes.</param>
    /// <param name="failing">The call that fails: <c>show</c> or <c>update</c>.</param>
    /// <param name="failure">Expected failure.</param>
    [TestCase("480", "show", "Cannot read resource group rg-platform-nonprod-aks (see above); the sleep hold is unchanged.")]
    [TestCase("480", "update", "Setting the sleep hold on rg-platform-nonprod-aks failed (see above); the hourly env-sleep may stop aks-platform-nonprod (no hold).")]
    [TestCase("0", "update", "Releasing the sleep hold on rg-platform-nonprod-aks failed (see above); it ends by itself at 2026-09-28T15:00:00Z.")]
    [Capability("CAP-OCT-009")]
    public void Should_SetHold_AzFails_FailsTheStep(string minutes, string failing, string failure)
    {
        var script = Hold("nonprod", minutes, Holder);
        if (failing == "show")
        {
            script.Reply("^az group show ", exitCode: 1, error: "ERROR: (AuthorizationFailed) no access\n");
        }
        else
        {
            script.Group("nonprod", minutes == "0" ? new() { ["platform-sleep-hold-until"] = "2026-09-28T15:00:00Z" } : null)
                .Reply("^az tag update ", exitCode: 1, error: "ERROR: (AuthorizationFailed) no access\n");
        }

        var run = script.Run();

        run.Failure.ShouldBe(failure, run.Transcript);
        run.Outputs.ShouldNotContainKey("Sleep.HoldUntil", run.Transcript);
    }

    /// <summary>The runbook runs in both tiers as the tier's lifecycle identity, and both prompts exist, not required.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_ReadSleepHold_Runbook_RunsAsTheLifecycleAccountWithBothPrompts()
    {
        var steps = OctopusRepository.Steps(OctopusRepository.Read(Runbook));
        var variables = OctopusRepository.Read(".octopus/platform-infrastructure/variables.ocl");

        steps.Select(step => step.Slug).ShouldBe(["set-hold"]);
        var step = steps[0].Text;
        step.ShouldContain("action_type = \"Octopus.AzurePowerShell\"");
        step.ShouldContain("Octopus.Action.Azure.AccountId = \"#{Azure.LifecycleAccount}\"");
        step.ShouldContain("worker_pool = \"hosted-ubuntu\"");
        step.ShouldContain("environments = [\"infra-nonprod\", \"infra-prod\"]");
        step.ShouldContain("image = \"octopusdeploy/worker-tools:6.6.5-ubuntu.24.04\"");
        foreach (var name in new[] { "Sleep.HoldMinutes", "Sleep.HoldBy" })
        {
            var block = variables[variables.IndexOf($"variable \"{name}\" {{", StringComparison.Ordinal)..];
            block = block[..(block.IndexOf("\n}", StringComparison.Ordinal) + 2)];
            block.ShouldContain("prompt {", customMessage: name);
            block.ShouldContain("required = false", customMessage: $"{name}: a required prompt would stop the hourly env-sleep trigger");
        }
    }

    private static RunbookScript Hold(string tier, string minutes, string holder) =>
        RunbookScript.Of(Runbook, "set-hold").InTier(tier).With("Sleep.HoldMinutes", minutes).With("Sleep.HoldBy", holder);

    private static string GroupId(string tier) => SleepHoldReplies.GroupId(tier);

    private static string TagUpdate(string tier, string operation) =>
        $"^az tag update --resource-id {OctopusReplies.Escape(GroupId(tier))} --operation {operation} --tags ";
}

/// <summary>Resource group replies of the sleep-hold tests.</summary>
internal static class SleepHoldReplies
{
    /// <summary>The resource group rg-platform-&lt;tier&gt;-aks with its cost tags and, when given, hold tags.</summary>
    /// <param name="script">The script.</param>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    /// <param name="hold">Extra tags, or <c>null</c>.</param>
    public static RunbookScript Group(this RunbookScript script, string tier, Dictionary<string, string>? hold = null)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["platform-tier"] = tier, ["platform-component"] = "aks" };
        foreach (var (key, value) in hold ?? [])
        {
            tags[key] = value;
        }

        return script.Reply(
            $"^az group show --name rg-platform-{tier}-aks --output json --only-show-errors$",
            OctopusReplies.Json(new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = GroupId(tier), ["name"] = $"rg-platform-{tier}-aks", ["tags"] = tags }));
    }

    /// <summary>The resource ID of rg-platform-&lt;tier&gt;-aks in the tests' subscription.</summary>
    /// <param name="tier"><c>nonprod</c> or <c>prod</c>.</param>
    public static string GroupId(string tier) => $"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-{tier}-aks";
}
