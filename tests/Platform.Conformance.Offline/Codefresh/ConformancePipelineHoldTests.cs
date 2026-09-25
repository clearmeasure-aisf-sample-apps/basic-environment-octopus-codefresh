using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-OCT-009 (the sleep hold of a conformance run): the destructive conformance pipeline, which has no arm, holds the
/// hourly env-sleep of infra-nonprod in a step of its own before its tests, and the tests run only after that hold.
/// Its teardown (conformance-teardown.ps1, shared with the nightly run) releases the hold.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ConformancePipelineHoldTests
{
    private const string Pipeline = "codefresh/platform/pipelines/conformance-destructive.yml";

    /// <summary>Step hold runs sleep-hold in infra-nonprod, fails the build when it fails, and gates the run step.</summary>
    [Test]
    [Capability("CAP-OCT-009")]
    public void Should_ReadConformanceDestructive_HoldStep_HoldsNonprodBeforeTheTests()
    {
        var steps = CodefreshRepository.Steps(CodefreshRepository.Load(Pipeline)).ToDictionary(step => step.Path, StringComparer.Ordinal);

        steps.ShouldContainKey("steps.hold");
        var hold = steps["steps.hold"];
        var command = string.Join(' ', CodefreshRepository.Strings(hold.Body.GetValueOrDefault("commands")));
        command.ShouldContain("octopus-runbook.ps1");
        command.ShouldContain("-Runbook sleep-hold -Environment infra-nonprod");
        command.ShouldContain("\"Sleep.HoldMinutes=300\"");
        command.ShouldContain("\"Sleep.HoldBy=conformance-destructive:${{CF_BUILD_ID}}\"");
        command.ShouldContain("-WaitMinutes 10; exit $LASTEXITCODE");
        IsTrue(hold.Body.GetValueOrDefault("strict_fail_fast")).ShouldBeTrue("a failed hold must fail the build");
        Waits(hold).ShouldBe(["main_clone:success"]);
        Waits(steps["steps.run"]).ShouldBe(["hold:success"], "the tests run only once the hold is set");
        Waits(steps["steps.teardown"]).ShouldBe(["run:finished"], "the teardown, which releases the hold, runs after every run");
    }

    private static string[] Waits(PipelineStep step) =>
        CodefreshRepository.Items(CodefreshRepository.Get(step.Body.GetValueOrDefault("when"), "steps"))
            .SelectMany(dependency => CodefreshRepository.Strings(CodefreshRepository.Get(dependency, "on"))
                .Select(on => $"{CodefreshRepository.Get(dependency, "name")}:{on}"))
            .ToArray();

    private static bool IsTrue(object? value) => value is true || value is string text && text.Equals("true", StringComparison.OrdinalIgnoreCase);
}
