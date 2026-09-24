using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-012, offline half: the bot-path audit of <c>main</c> passes. <c>scripts/checks/tool-boundaries.sh
/// --audit-bot-commits</c> fails when a commit by the pin bot (<c>octopus-argocd-pin-bot</c>) changes anything but a pin
/// field under <c>gitops/apps/&lt;app&gt;/envs/&lt;env&gt;/&lt;deployable&gt;/</c>. A tree without Git history has nothing to audit.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PinWriterTests
{
    /// <summary>The pin bot's commits on the checked-out history change only pin lines.</summary>
    [Test]
    [Capability("CAP-OCT-012")]
    public void Should_AuditBotCommits_CheckedOutHistory_OnlyPinsChanged()
    {
        var bash = OctopusRepository.FindOnPath("bash");
        if (bash is null)
        {
            Assert.Inconclusive("bash is not on PATH; the audit runs in env-checks");
        }

        var root = OctopusRepository.Root;

        var (exitCode, output) = OctopusRepository.Run(
            bash!,
            [Path.Combine(root, "scripts", "checks", "tool-boundaries.sh"), "--audit-bot-commits", "--root", root, "--bot-author", "^octopus-argocd-pin-bot"],
            root);

        TestContext.Out.WriteLine(output);
        exitCode.ShouldBeOneOf([0, 3], $"the bot-path audit failed:{Environment.NewLine}{output}");
    }
}
