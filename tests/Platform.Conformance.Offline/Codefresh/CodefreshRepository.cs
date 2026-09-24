using Platform.Conformance.Tests.Codefresh;
using YamlDotNet.Serialization;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>A Codefresh pipeline step: its path (for example <c>steps.gate</c> or <c>hooks.on_finish</c>) and its mapping.</summary>
/// <param name="Path">Where the step sits in the pipeline.</param>
/// <param name="Body">The step's keys.</param>
internal sealed record PipelineStep(string Path, IReadOnlyDictionary<string, object?> Body)
{
    /// <summary>The step type (<c>freestyle</c> when absent).</summary>
    public string Type => Body.TryGetValue("type", out var type) && type is string text ? text : "freestyle";

    /// <summary>The step's shell commands (freestyle <c>commands</c>, hook <c>exec.commands</c>).</summary>
    public IEnumerable<string> Commands =>
        CodefreshRepository.Strings(Body.TryGetValue("commands", out var commands) ? commands : null)
            .Concat(Body.TryGetValue("exec", out var exec) && exec is IDictionary<object, object?> hook && hook.TryGetValue("commands", out var hookCommands)
                ? CodefreshRepository.Strings(hookCommands)
                : []);
}

/// <summary>
/// Reads the Codefresh files of the environment repository for the offline CAP-CF tests: pipeline YAML and specs under
/// codefresh/apps, codefresh/platform and codefresh/templates, the integration declarations and the pipeline scripts.
/// </summary>
internal static class CodefreshRepository
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>The environment repository root.</summary>
    public static string Root => CodefreshPlatform.RepositoryRoot;

    /// <summary>Every pipeline spec (<c>codefresh/**/specs/*.yml</c>), relative to the root.</summary>
    public static IReadOnlyList<string> Specs => Files("specs");

    /// <summary>Every pipeline YAML (<c>codefresh/**/pipelines/*.yml</c>), relative to the root.</summary>
    public static IReadOnlyList<string> Pipelines => Files("pipelines");

    /// <summary>Every script of the pipelines (<c>codefresh/**/scripts/*</c>), relative to the root.</summary>
    public static IReadOnlyList<string> Scripts => Files("scripts", "*");

    /// <summary>Every integration declaration (<c>codefresh/**/integrations.yaml</c>), relative to the root.</summary>
    public static IReadOnlyList<string> Integrations =>
        Directory.EnumerateFiles(Path.Combine(Root, "codefresh"), "integrations.yaml", SearchOption.AllDirectories)
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary><c>true</c> for a file of an app or a starter (codefresh/apps/*, codefresh/templates/*).</summary>
    /// <param name="relative">Path relative to the root.</param>
    public static bool IsAppFile(string relative) =>
        relative.StartsWith("codefresh/apps/", StringComparison.Ordinal) || relative.StartsWith("codefresh/templates/", StringComparison.Ordinal);

    /// <summary>Reads a file relative to the root.</summary>
    /// <param name="relative">Path relative to the root.</param>
    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    /// <summary>Parses a YAML file into dictionaries and lists.</summary>
    /// <param name="relative">Path relative to the root.</param>
    public static IDictionary<object, object?> Load(string relative) =>
        Yaml.Deserialize<IDictionary<object, object?>>(Read(relative)) ?? new Dictionary<object, object?>();

    /// <summary>A mapping value by key, or <c>null</c>.</summary>
    /// <param name="node">A mapping.</param>
    /// <param name="key">Key.</param>
    public static object? Get(object? node, string key) =>
        node is IDictionary<object, object?> map && map.TryGetValue(key, out var value) ? value : null;

    /// <summary>The items of a sequence.</summary>
    /// <param name="node">A sequence, or anything else (no items).</param>
    public static IEnumerable<object?> Items(object? node) => node is IList<object?> list ? list : [];

    /// <summary>The string items of a sequence, or the node itself when it is a string.</summary>
    /// <param name="node">A sequence or a scalar.</param>
    public static IEnumerable<string> Strings(object? node) => node switch
    {
        string text => [text],
        IList<object?> list => list.OfType<string>(),
        _ => [],
    };

    /// <summary>Every step of a pipeline, including the steps of parallel steps and the hooks.</summary>
    /// <param name="pipeline">A parsed pipeline YAML.</param>
    public static IEnumerable<PipelineStep> Steps(IDictionary<object, object?> pipeline)
    {
        foreach (var step in StepsOf(Get(pipeline, "steps"), "steps"))
        {
            yield return step;
        }

        foreach (var step in Hooks(Get(pipeline, "hooks"), "hooks"))
        {
            yield return step;
        }
    }

    /// <summary>The contexts a spec attaches.</summary>
    /// <param name="spec">A parsed spec.</param>
    public static IReadOnlyList<string> Contexts(IDictionary<object, object?> spec) =>
        Strings(Get(Get(spec, "spec"), "contexts")).ToArray();

    private static IEnumerable<PipelineStep> StepsOf(object? steps, string path)
    {
        if (steps is not IDictionary<object, object?> map)
        {
            yield break;
        }

        foreach (var (key, value) in map)
        {
            if (value is not IDictionary<object, object?> body)
            {
                continue;
            }

            var here = $"{path}.{key}";
            var typed = body.ToDictionary(pair => pair.Key.ToString() ?? string.Empty, pair => pair.Value);
            yield return new PipelineStep(here, typed);
            foreach (var nested in StepsOf(Get(body, "steps"), here))
            {
                yield return nested;
            }

            foreach (var hook in Hooks(Get(body, "hooks"), here + ".hooks"))
            {
                yield return hook;
            }
        }
    }

    private static IEnumerable<PipelineStep> Hooks(object? hooks, string path)
    {
        if (hooks is not IDictionary<object, object?> map)
        {
            yield break;
        }

        foreach (var (key, value) in map)
        {
            if (value is IDictionary<object, object?> hook)
            {
                yield return new PipelineStep($"{path}.{key}", hook.ToDictionary(pair => pair.Key.ToString() ?? string.Empty, pair => pair.Value));
                foreach (var nested in StepsOf(Get(hook, "steps"), $"{path}.{key}"))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IReadOnlyList<string> Files(string folder, string pattern = "*.yml") =>
        Directory.EnumerateFiles(Path.Combine(Root, "codefresh"), pattern, SearchOption.AllDirectories)
            .Select(Relative)
            .Where(relative => relative.Split('/') is { Length: > 2 } parts && parts[^2] == folder)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
