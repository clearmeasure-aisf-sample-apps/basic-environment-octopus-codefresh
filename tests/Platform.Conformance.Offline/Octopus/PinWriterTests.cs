using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Offline.Kit;
using Platform.Conformance.Offline.Kit.Boundaries;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-012, offline half: the bot-path audit of the checked-out history passes. <see cref="BotCommitAudit"/>, the C#
/// port of <c>scripts/checks/tool-boundaries.sh --audit-bot-commits</c>, fails when a first-parent commit by a bot
/// identity (<c>PLATFORM_BOT_AUTHORS</c>, else the design value: the Octopus image-tag step and
/// <c>octopus-argocd-pin-bot</c>) changes anything but a pin field under
/// <c>gitops/apps/&lt;app&gt;/envs/&lt;env&gt;/&lt;deployable&gt;/</c>. <c>AUDIT_DEPTH</c> sets how many first-parent commits
/// it reads (default 20). Without git the test is Inconclusive, and failed when <c>CI=true</c>; a tree without Git
/// history, or a branch without a commit, has nothing to audit and is Inconclusive. With <c>CI=true</c>,
/// <c>PLATFORM_BOT_AUTHORS</c> must be set, as for the script.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PinWriterTests
{
    /// <summary>The bot commits on the checked-out history change only pin lines.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_AuditBotCommits_CheckedOutHistory_OnlyPinsChanged()
    {
        var git = KitToolbox.Require("git");
        var settings = BotAuditSettings.From(ProcessEnvironmentVariables.Instance);
        var identitiesMissingInCi = KitToolbox.IsCi && !settings.BotAuthorsFromEnvironment;
        identitiesMissingInCi.ShouldBeFalse($"{BotAuditSettings.BotAuthorsVariable} is not set, so the bot-path audit cannot run (CI=true)");

        var result = BotCommitAudit.Run(git, OctopusRepository.Root, settings.BotAuthors, settings.Depth);

        TestContext.Out.WriteLine($"bot identities: {settings.BotAuthors}{(settings.BotAuthorsFromEnvironment ? string.Empty : $" (design value; {BotAuditSettings.BotAuthorsVariable} is not set)")}");
        TestContext.Out.Write(result.Report());
        if (result.Outcome == BotAuditOutcome.Skipped)
        {
            Assert.Inconclusive(result.Report());
        }

        result.Outcome.ShouldBe(BotAuditOutcome.Passed, $"the bot-path audit failed:{Environment.NewLine}{result.Report()}");
    }
}
