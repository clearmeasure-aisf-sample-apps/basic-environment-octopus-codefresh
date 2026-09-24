using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-013 and CAP-CF-014: the step graphs of the pipelines fail closed, and a re-run of a release for the same commit
/// reaches the handoff. Codefresh behaviour behind the rules, seen live on 2026-09-24: with pipeline-level
/// <c>fail_fast: false</c>, a failed step without <c>strict_fail_fast: true</c> leaves the build green; in parallel mode a
/// step skipped by its condition stays pending for the steps that wait on it (<c>skipped</c> is valid only in sequential
/// mode), so a dependency on <c>skipped</c> never fires; a <c>build</c> step without <c>tag</c> also pushes the branch
/// name; and a second release build of a commit cannot push its tags again, because <c>supply_chain</c> locked them.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class PipelineGraphTests
{
    private const string ImageReuse = "image_reuse";
    private const string SupplyChain = "supply_chain";
    private const string SupplyChainReuse = "supply_chain_reuse";
    private const string OctopusPreflight = "octopus_preflight";
    private const string NotReused = "\"${{IMAGES_REUSED}}\" == \"false\"";
    private const string Reused = "\"${{IMAGES_REUSED}}\" != \"false\"";
    private const string VersionTag = "${{VERSION}}";

    /// <summary>A failed clone or image build fails the build, whatever the pipeline-level fail_fast.</summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void Should_ReadPipelines_EveryCloneAndImageBuild_FailsTheBuildWhenItFails()
    {
        var pipelines = CodefreshRepository.Pipelines;

        var offenders = pipelines
            .Select(pipeline => (Pipeline: pipeline, Document: CodefreshRepository.Load(pipeline)))
            .Where(entry => IsFalse(CodefreshRepository.Get(entry.Document, "fail_fast")))
            .SelectMany(entry => CodefreshRepository.Steps(entry.Document)
                .Where(step => step.Type is "git-clone" or "build")
                .Where(step => !IsTrue(step.Body.GetValueOrDefault("strict_fail_fast")))
                .Select(step => $"{entry.Pipeline} {step.Path} ({step.Type})"))
            .ToArray();

        pipelines.ShouldNotBeEmpty("no pipeline YAML under codefresh/");
        offenders.ShouldBeEmpty("clone or image build steps whose failure leaves the build green (fail_fast false, no strict_fail_fast)");
    }

    /// <summary>No step of a parallel pipeline waits for another step to be skipped.</summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void Should_ReadPipelines_ParallelPipelines_NeverWaitOnASkippedStep()
    {
        var pipelines = CodefreshRepository.Pipelines;

        var offenders = pipelines
            .Select(pipeline => (Pipeline: pipeline, Document: CodefreshRepository.Load(pipeline)))
            .Where(entry => CodefreshRepository.Get(entry.Document, "mode") is "parallel")
            .SelectMany(entry => CodefreshRepository.Steps(entry.Document)
                .SelectMany(step => Dependencies(step)
                    .Where(dependency => dependency.On.Contains("skipped", StringComparer.Ordinal))
                    .Select(dependency => $"{entry.Pipeline} {step.Path} waits for {dependency.Name} on skipped")))
            .ToArray();

        offenders.ShouldBeEmpty("a condition-skipped step stays pending in parallel mode, so these dependencies never fire");
    }

    /// <summary>
    /// Every release pipeline has two complementary paths after <c>image_reuse</c>: the image builds (tagged VERSION) with
    /// <c>supply_chain</c>, or <c>supply_chain_reuse</c>; <c>octopus_preflight</c> waits for any of the two.
    /// </summary>
    [Test]
    [Capability("CAP-CF-014")]
    public void Should_ReadReleasePipelines_BuildAndReusePaths_AreComplementaryAndJoinBeforeTheHandoff()
    {
        var releases = CodefreshRepository.Pipelines
            .Where(pipeline => pipeline.EndsWith("/pipelines/release.yml", StringComparison.Ordinal))
            .ToArray();

        var problems = releases.SelectMany(ReleaseGraphProblems).ToArray();

        releases.ShouldNotBeEmpty("no release pipeline under codefresh/");
        problems.ShouldBeEmpty("release pipelines whose re-run cannot reach the handoff, or whose first run can skip it");
    }

    private static IEnumerable<string> ReleaseGraphProblems(string pipeline)
    {
        var steps = CodefreshRepository.Steps(CodefreshRepository.Load(pipeline))
            .Where(step => step.Path.StartsWith("steps.", StringComparison.Ordinal) && step.Path.Count(character => character == '.') == 1)
            .ToDictionary(step => step.Path["steps.".Length..], StringComparer.Ordinal);
        foreach (var name in new[] { ImageReuse, SupplyChain, SupplyChainReuse, OctopusPreflight })
        {
            if (!steps.ContainsKey(name))
            {
                yield return $"{pipeline}: no step {name}";
            }
        }

        if (!steps.TryGetValue(SupplyChain, out var supplyChain)
            || !steps.TryGetValue(SupplyChainReuse, out var supplyChainReuse)
            || !steps.TryGetValue(OctopusPreflight, out var preflight))
        {
            yield break;
        }

        var images = steps.Values.Where(step => step.Type == "build").ToArray();
        if (images.Length == 0)
        {
            yield return $"{pipeline}: no image build step";
        }

        foreach (var image in images)
        {
            if (image.Body.GetValueOrDefault("tag") as string != VersionTag)
            {
                yield return $"{pipeline} {image.Path}: tag is not {VersionTag}";
            }

            if (!DependsOnlyOn(image, ImageReuse) || !Conditions(image).Contains(NotReused, StringComparer.Ordinal))
            {
                yield return $"{pipeline} {image.Path}: does not run after {ImageReuse} on {NotReused} only";
            }
        }

        var supplyChainNames = Dependencies(supplyChain).Select(dependency => dependency.Name).Order(StringComparer.Ordinal);
        var imageNames = images.Select(image => image.Path["steps.".Length..]).Order(StringComparer.Ordinal);
        if (!supplyChainNames.SequenceEqual(imageNames, StringComparer.Ordinal)
            || Dependencies(supplyChain).Any(dependency => !dependency.On.SequenceEqual(["success"])))
        {
            yield return $"{pipeline} {supplyChain.Path}: does not wait for exactly the image builds, on success";
        }

        if (!DependsOnlyOn(supplyChainReuse, ImageReuse) || !Conditions(supplyChainReuse).Contains(Reused, StringComparer.Ordinal))
        {
            yield return $"{pipeline} {supplyChainReuse.Path}: does not run after {ImageReuse} on {Reused}";
        }

        var join = CodefreshRepository.Get(CodefreshRepository.Get(preflight.Body.GetValueOrDefault("when"), "steps"), "any");
        var joined = CodefreshRepository.Items(join).Select(Dependency).ToArray();
        if (joined.Length != 2
            || !joined.Select(dependency => dependency.Name).Order(StringComparer.Ordinal).SequenceEqual([SupplyChain, SupplyChainReuse])
            || joined.Any(dependency => !dependency.On.SequenceEqual(["success"])))
        {
            yield return $"{pipeline} {preflight.Path}: does not wait for any of {SupplyChain} and {SupplyChainReuse} on success";
        }
    }

    private static bool DependsOnlyOn(PipelineStep step, string name) =>
        Dependencies(step) is [{ } only] && only.Name == name && only.On.SequenceEqual(["success"]);

    /// <summary>The <c>when.steps</c> entries of a step: a plain list, or the lists under <c>all</c> and <c>any</c>.</summary>
    private static IReadOnlyList<StepDependency> Dependencies(PipelineStep step)
    {
        var steps = CodefreshRepository.Get(step.Body.GetValueOrDefault("when"), "steps");
        var entries = steps is IDictionary<object, object?>
            ? CodefreshRepository.Items(CodefreshRepository.Get(steps, "all")).Concat(CodefreshRepository.Items(CodefreshRepository.Get(steps, "any")))
            : CodefreshRepository.Items(steps);
        return entries.Select(Dependency).ToArray();
    }

    private static StepDependency Dependency(object? entry)
    {
        var on = CodefreshRepository.Strings(CodefreshRepository.Get(entry, "on")).ToArray();
        return new StepDependency(CodefreshRepository.Get(entry, "name") as string ?? string.Empty, on.Length == 0 ? ["success"] : on);
    }

    /// <summary>The expressions of a step's <c>when.condition.all</c> and <c>when.condition.any</c>.</summary>
    private static IReadOnlyList<string> Conditions(PipelineStep step)
    {
        var condition = CodefreshRepository.Get(step.Body.GetValueOrDefault("when"), "condition");
        return new[] { "all", "any" }
            .Select(key => CodefreshRepository.Get(condition, key))
            .OfType<IDictionary<object, object?>>()
            .SelectMany(map => map.Values.OfType<string>())
            .ToArray();
    }

    private static bool IsTrue(object? value) => value is true || (value is string text && text.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool IsFalse(object? value) => value is false || (value is string text && text.Equals("false", StringComparison.OrdinalIgnoreCase));

    private sealed record StepDependency(string Name, IReadOnlyList<string> On);
}
