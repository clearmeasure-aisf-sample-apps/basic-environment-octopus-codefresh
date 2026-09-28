using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-004 and CAP-CF-013 for <c>workorders/release</c>: when <c>prepare</c> found that <c>codefresh/ci</c> passed the
/// release commit's tree (<c>CI_TREE_VERIFIED</c>, from <c>scripts/ci-tree.ps1</c>), the static gates exit early inside
/// their steps and leave their success markers; they are never skipped by a condition (a condition-skipped step stays
/// pending in parallel mode, and <c>gate</c> would wait for it forever), and <c>build_sql</c>, whose Release build the
/// package and the images use, always runs. The image builds use BuildKit with a registry layer cache that their own
/// registry integration can read.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class ReleaseGateReuseTests
{
    private const string Pipeline = "codefresh/apps/workorders/pipelines/release.yml";
    private const string Verified = "\"${CI_TREE_VERIFIED:-}\" = \"true\"";
    private const string Registry = "acrplatformi3aldz.azurecr.io";

    private static readonly string[] StaticGates = ["code_analysis", "build_sqlite", "qodana", "security_scan"];

    /// <summary>prepare runs ci-tree.ps1 after prepare.ps1, for the app repository.</summary>
    [Test]
    [Capability("CAP-CF-004")]
    public void Should_ReadWorkordersRelease_Prepare_RunsTheTreeCheck()
    {
        var commands = Step("prepare").Commands.ToArray();

        commands.Length.ShouldBe(2);
        commands[0].ShouldContain("/scripts/prepare.ps1\" -Release");
        commands[1].ShouldContain("/scripts/ci-tree.ps1\"");
        commands[1].ShouldContain("-Repository clearmeasure-aisf-sample-apps/20260923-001");
    }

    /// <summary>
    /// Each static gate's first command exits early when CI verified the tree, after writing its marker (qodana's is written
    /// by qodana_result on its success); no static gate has a condition on CI_TREE_VERIFIED, and build_sql never reads it.
    /// </summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void Should_ReadWorkordersRelease_StaticGates_ExitEarlyWithTheirMarkersAndBuildSqlAlwaysRuns()
    {
        var problems = new List<string>();
        foreach (var name in StaticGates)
        {
            var step = Step(name);
            var first = step.Commands.FirstOrDefault() ?? string.Empty;
            if (!first.StartsWith($"if [ {Verified} ]; then", StringComparison.Ordinal) || !first.EndsWith("exit 0; fi", StringComparison.Ordinal))
            {
                problems.Add($"{name}: the first command is not the early exit on CI_TREE_VERIFIED");
            }

            if (name != "qodana" && !first.Contains($"echo success > \"${{ARTIFACTS_DIR}}/gates/{name}\"; exit 0", StringComparison.Ordinal))
            {
                problems.Add($"{name}: the early exit does not write the success marker");
            }

            if (Serialized(step.Body.GetValueOrDefault("when")).Contains("CI_TREE_VERIFIED", StringComparison.Ordinal))
            {
                problems.Add($"{name}: skipped by a condition; the gate would wait for it forever");
            }

            if (step.Body.ContainsKey("cmd"))
            {
                problems.Add($"{name}: cmd keeps the entry point and cannot exit early");
            }
        }

        // A plain scalar with ": " in it parses as a mapping, which Codefresh cannot run as a command.
        var nonStrings = Steps()
            .SelectMany(step => CodefreshRepository.Items(step.Body.GetValueOrDefault("commands")).Where(command => command is not string).Select(_ => step.Path))
            .ToArray();
        nonStrings.ShouldBeEmpty("commands that YAML parses as something other than a string");
        Serialized(Step("build_sql").Body).ShouldNotContain("CI_TREE_VERIFIED");
        Step("qodana").Commands.Skip(1).Single().ShouldStartWith("qodana scan ");
        Step("qodana_result").Commands.Single().ShouldContain("echo success > \"${ARTIFACTS_DIR}/gates/qodana\"");
        Step("gate").Commands.Last().ShouldContain("build_sql build_sqlite code_analysis qodana security_scan");
        problems.ShouldBeEmpty();
    }

    /// <summary>
    /// The three image builds: BuildKit, Codefresh's own cache lookup off (it pulled with the domain's primary, pull-only
    /// platform/* integration and failed), the cache imported from their own repository at IMAGE_CACHE_TAG through
    /// acr-apps-release, and inline cache metadata written into each pushed image.
    /// </summary>
    [Test]
    [Capability("CAP-CF-013")]
    public void Should_ReadWorkordersRelease_ImageBuilds_UseAReadableBuildKitLayerCache()
    {
        var images = Steps().Where(step => step.Type == "build").ToArray();
        var problems = new List<string>();
        foreach (var image in images)
        {
            var name = image.Body.GetValueOrDefault("image_name") as string;
            if (!IsTrue(image.Body.GetValueOrDefault("buildkit")) || !IsTrue(image.Body.GetValueOrDefault("no_cf_cache")))
            {
                problems.Add($"{image.Path}: not buildkit with no_cf_cache");
            }

            if (!CodefreshRepository.Strings(image.Body.GetValueOrDefault("registry_contexts")).SequenceEqual(["acr-apps-release"]))
            {
                problems.Add($"{image.Path}: registry_contexts is not acr-apps-release");
            }

            if (!CodefreshRepository.Strings(image.Body.GetValueOrDefault("cache_from")).SequenceEqual([$"{Registry}/{name}:${{{{IMAGE_CACHE_TAG}}}}"]))
            {
                problems.Add($"{image.Path}: cache_from is not {Registry}/{name}:${{{{IMAGE_CACHE_TAG}}}}");
            }

            if (!CodefreshRepository.Strings(image.Body.GetValueOrDefault("build_arguments")).Contains("BUILDKIT_INLINE_CACHE=1", StringComparer.Ordinal))
            {
                problems.Add($"{image.Path}: no BUILDKIT_INLINE_CACHE=1");
            }
        }

        images.Length.ShouldBe(3);
        Step("image_reuse").Commands.ShouldContain(command => command.StartsWith("cf_export IMAGE_CACHE_TAG=\"sha-$(git rev-parse --verify --quiet 'HEAD^1' | cut -c1-7)\"", StringComparison.Ordinal));
        problems.ShouldBeEmpty();
    }

    private static PipelineStep[] Steps() => CodefreshRepository.Steps(CodefreshRepository.Load(Pipeline)).ToArray();

    private static PipelineStep Step(string name) => Steps().Single(step => step.Path == $"steps.{name}");

    private static string Serialized(object? node) => node switch
    {
        null => string.Empty,
        string text => text,
        IDictionary<object, object?> map => string.Join('\n', map.Select(pair => $"{pair.Key}: {Serialized(pair.Value)}")),
        IReadOnlyDictionary<string, object?> map => string.Join('\n', map.Select(pair => $"{pair.Key}: {Serialized(pair.Value)}")),
        IList<object?> list => string.Join('\n', list.Select(Serialized)),
        _ => node.ToString() ?? string.Empty,
    };

    private static bool IsTrue(object? value) => value is true || (value is string text && text.Equals("true", StringComparison.OrdinalIgnoreCase));
}
