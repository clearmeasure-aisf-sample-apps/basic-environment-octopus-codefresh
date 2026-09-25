using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>A step of an OCL process: its slug and its text up to the next step.</summary>
/// <param name="Slug">Step slug, for example <c>sod-guard</c>.</param>
/// <param name="Text">The step block and anything before the next step.</param>
internal sealed record OclStep(string Slug, string Text);

/// <summary>
/// Reads the Octopus files of the environment repository for the offline Octopus capability tests: config as code under
/// <c>.octopus/</c>, the starters under <c>octopus/templates/</c>, the step-template scripts and octopus/terraform.
/// </summary>
internal static partial class OctopusRepository
{
    /// <summary>The environment repository root (the folder that holds tests/Platform.Conformance.sln).</summary>
    public static string Root => RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance);

    /// <summary>Full path of a repository file.</summary>
    /// <param name="relativePath">Path with forward slashes, for example <c>octopus/step-templates/sod-guard.ps1</c>.</param>
    public static string PathOf(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Content of a repository file.</summary>
    /// <param name="relativePath">Path with forward slashes.</param>
    public static string Read(string relativePath) => File.ReadAllText(PathOf(relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Repository-relative paths of the OCL files below a folder, sorted.</summary>
    /// <param name="relativeFolder">Folder with forward slashes, for example <c>.octopus/apps</c>.</param>
    public static IReadOnlyList<string> OclFiles(string relativeFolder)
    {
        var folder = PathOf(relativeFolder);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.ocl", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    /// <summary>The app processes and starter processes that may inline a step-template script.</summary>
    public static IReadOnlyList<string> AppProcesses() =>
        OclFiles(".octopus/apps").Concat(OclFiles("octopus/templates"))
            .Where(file => file.EndsWith("/deployment_process.ocl", StringComparison.Ordinal))
            .ToArray();

    /// <summary>Lines of a step-template script (PowerShell 7) with trailing whitespace removed.</summary>
    /// <param name="template">Script name without extension, for example <c>sod-guard</c>.</param>
    public static IReadOnlyList<string> CanonicalLines(string template) =>
        Normalize(Read($"octopus/step-templates/{template}.ps1").TrimEnd('\n').Split('\n'));

    /// <summary>
    /// Every inline copy of a step-template script in an OCL text: the lines between a line that is exactly
    /// <c># &gt;&gt;&gt; octopus/step-templates/&lt;template&gt;.ps1</c> and the matching <c># &lt;&lt;&lt; …</c> line, with the
    /// indentation of the marker line removed and trailing whitespace dropped.
    /// </summary>
    /// <param name="ocl">OCL text.</param>
    /// <param name="template">Script name without extension.</param>
    public static IReadOnlyList<IReadOnlyList<string>> InlineCopies(string ocl, string template)
    {
        var start = $"# >>> octopus/step-templates/{template}.ps1";
        var end = $"# <<< octopus/step-templates/{template}.ps1";
        var lines = ocl.Split('\n');
        var copies = new List<IReadOnlyList<string>>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim() != start)
            {
                continue;
            }

            var indent = lines[index].Length - lines[index].TrimStart().Length;
            var body = new List<string>();
            var closed = false;
            for (index++; index < lines.Length; index++)
            {
                if (lines[index].Trim() == end)
                {
                    closed = true;
                    break;
                }

                var line = lines[index];
                body.Add(line.Trim().Length == 0 ? string.Empty : line.Length >= indent ? line[indent..] : line.TrimStart());
            }

            closed.ShouldBeTrue($"inline copy of {template}.ps1 has no closing marker");
            copies.Add(Normalize(body));
        }

        return copies;
    }

    /// <summary>Splits an OCL process or runbook into its steps.</summary>
    /// <param name="ocl">OCL text.</param>
    public static IReadOnlyList<OclStep> Steps(string ocl)
    {
        var matches = StepHeader().Matches(ocl);
        return matches.Select((match, index) =>
        {
            var end = index + 1 < matches.Count ? matches[index + 1].Index : ocl.Length;
            return new OclStep(match.Groups["slug"].Value, ocl[match.Index..end]);
        }).ToArray();
    }

    /// <summary>The text of an OCL file without its <c>//</c> comment lines.</summary>
    /// <param name="ocl">OCL text.</param>
    public static string WithoutComments(string ocl) =>
        string.Join('\n', ocl.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>The string items of a Terraform list assignment such as <c>name = ["a", "b"]</c>.</summary>
    /// <param name="terraform">Terraform text.</param>
    /// <param name="name">Attribute or local name.</param>
    public static IReadOnlyList<string> TerraformList(string terraform, string name)
    {
        var match = Regex.Match(terraform, $@"\b{Regex.Escape(name)}\s*=\s*\[(?<items>[^\]]*)\]");
        match.Success.ShouldBeTrue($"no list {name} in the Terraform text");
        return Regex.Matches(match.Groups["items"].Value, "\"(?<item>[^\"]*)\"").Select(item => item.Groups["item"].Value).ToArray();
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> lines) => lines.Select(line => line.TrimEnd()).ToArray();

    [GeneratedRegex(@"^[ \t]*step ""(?<slug>[a-z0-9-]+)"" \{", RegexOptions.Multiline)]
    private static partial Regex StepHeader();
}
