using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-005, offline half: fork pull requests never start a pipeline, because every git trigger of every spec (apps,
/// platform and starters) sets <c>pullRequestAllowForkEvents: false</c> explicitly (handshake, contract
/// <c>codefresh.handshake.forkEvents</c>). The live half watches a real fork pull request.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ForkPullRequestTests
{
    /// <summary>Every git trigger keeps fork events off.</summary>
    [Test]
    [Capability("CAP-CF-005")]
    public void Should_ReadSpecs_EveryGitTrigger_KeepsForkEventsOff()
    {
        var specs = CodefreshRepository.Specs;

        var triggers = specs
            .SelectMany(spec => CodefreshRepository.Items(CodefreshRepository.Get(CodefreshRepository.Get(CodefreshRepository.Load(spec), "spec"), "triggers"))
                .Select(trigger => (Spec: spec, Trigger: trigger)))
            .Where(entry => CodefreshRepository.Get(entry.Trigger, "type") as string == "git")
            .ToArray();

        specs.ShouldNotBeEmpty("no pipeline spec under codefresh/");
        triggers.ShouldNotBeEmpty("no git trigger under codefresh/");
        triggers
            .Where(entry => !string.Equals(CodefreshRepository.Get(entry.Trigger, "pullRequestAllowForkEvents") as string, "false", StringComparison.OrdinalIgnoreCase))
            .Select(entry => $"{entry.Spec}: trigger {CodefreshRepository.Get(entry.Trigger, "name")}")
            .ShouldBeEmpty("git triggers without pullRequestAllowForkEvents: false");
    }
}
