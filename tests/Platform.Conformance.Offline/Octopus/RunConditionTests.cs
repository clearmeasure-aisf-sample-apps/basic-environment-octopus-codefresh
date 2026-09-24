using System.Text.RegularExpressions;
using Platform.Conformance.Harness;

namespace Platform.Conformance.Offline.Octopus;

/// <summary>
/// CAP-OCT-016: every variable run condition sits under the property Octopus reads for its level. A step reads
/// <c>Octopus.Step.ConditionVariableExpression</c> from the step properties; a child action reads
/// <c>Octopus.Action.ConditionVariableExpression</c> from the action properties (the key names of the Octopus web UI).
/// Under the other key the expression resolves to empty and the step always skips: env-sleep's Stop cluster skipped
/// every sleep decision, so no cluster ever stopped (live, 2026-09-24).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public partial class RunConditionTests
{
    private const string StepKey = "Octopus.Step.ConditionVariableExpression";
    private const string ActionKey = "Octopus.Action.ConditionVariableExpression";

    /// <summary>A step or action with a variable run condition names its expression under the key of its level, and only there.</summary>
    [Test]
    [Capability("CAP-OCT-016")]
    public void Should_ReadOcl_VariableRunConditions_UseTheKeyOfTheirLevel()
    {
        var files = OctopusRepository.OclFiles(".octopus").Concat(OctopusRepository.OclFiles("octopus/templates")).ToArray();
        var problems = new List<string>();
        var conditioned = 0;

        foreach (var file in files)
        {
            var ocl = WithoutHeredocBodies(OctopusRepository.WithoutComments(OctopusRepository.Read(file)));
            foreach (var step in OctopusRepository.Steps(ocl))
            {
                var parts = ActionHeader().Split(step.Text);
                conditioned += IsVariable(parts[0]) ? 1 : 0;
                problems.AddRange(Problems($"{file} step {step.Slug}", parts[0], StepKey, ActionKey));
                for (var index = 1; index < parts.Length; index++)
                {
                    conditioned += IsVariable(parts[index]) ? 1 : 0;
                    problems.AddRange(Problems($"{file} step {step.Slug} action {index}", parts[index], ActionKey, StepKey));
                }
            }
        }

        files.ShouldNotBeEmpty("no OCL file under .octopus/ or octopus/templates/");
        conditioned.ShouldBeGreaterThan(0, "no step or action with a variable run condition; the rule checks nothing");
        problems.ShouldBeEmpty("variable run conditions that Octopus reads as empty, so the step always skips");
    }

    private static IEnumerable<string> Problems(string where, string block, string key, string wrongKey)
    {
        if (block.Contains(wrongKey, StringComparison.Ordinal))
        {
            yield return $"{where}: {wrongKey} is not read at this level (use {key})";
        }

        var expression = Expression(block, key);
        if (IsVariable(block) && string.IsNullOrWhiteSpace(expression))
        {
            yield return $"{where}: condition Variable without a {key}";
        }

        if (!IsVariable(block) && expression is not null)
        {
            yield return $"{where}: {key} without condition Variable";
        }
    }

    private static bool IsVariable(string block) => VariableCondition().IsMatch(block);

    /// <summary>The value assigned to <paramref name="key"/>: a quoted string or a heredoc marker, or <c>null</c>.</summary>
    private static string? Expression(string block, string key)
    {
        var match = Regex.Match(block, $@"^[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*(?:""(?<value>(?:[^""\\]|\\.)*)""|(?<value><<-?[A-Za-z_]+))", RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>The OCL text with the bodies of heredocs removed, so script text never reads as OCL.</summary>
    private static string WithoutHeredocBodies(string ocl) => Heredoc().Replace(ocl, match => match.Groups["open"].Value + match.Groups["close"].Value);

    [GeneratedRegex(@"^[ \t]*action(?:[ \t]+""[^""]*"")?[ \t]*\{", RegexOptions.Multiline)]
    private static partial Regex ActionHeader();

    [GeneratedRegex(@"^[ \t]*condition[ \t]*=[ \t]*""Variable""", RegexOptions.Multiline)]
    private static partial Regex VariableCondition();

    [GeneratedRegex(@"(?<open><<-?(?<tag>[A-Za-z_]+)[ \t]*\n).*?(?<close>^[ \t]*\k<tag>[ \t]*$)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Heredoc();
}
