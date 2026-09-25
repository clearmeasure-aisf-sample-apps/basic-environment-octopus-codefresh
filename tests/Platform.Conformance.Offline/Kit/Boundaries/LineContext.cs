using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// The context of every line of a file, as the script's <c>ctx_map</c> computes it: the slug of the enclosing
/// <c>step "&lt;slug&gt;"</c> block of an OCL file (heredoc bodies belong to their step), the chain of ancestor keys of a
/// YAML file (<c>/steps/wake_nonprod/commands</c>), or <c>-</c>. The scan is textual on purpose; it reproduces the
/// script line for line, including its reading of block scalars.
/// </summary>
internal static class LineContext
{
    private static readonly Regex StepHeader = PosixPatterns.Ere("^[ \t]*step[ \t]+\"([^\"]+)\"");
    private static readonly Regex HeredocStart = PosixPatterns.Ere("<<-?([A-Za-z_][A-Za-z0-9_]*)[ \t]*$");
    private static readonly Regex BlankOrComment = PosixPatterns.Ere("^[ \t]*(#|$)");
    private static readonly Regex KeyLine = PosixPatterns.Ere("^ *[A-Za-z0-9_.-]+:([ \t]|$)");

    /// <summary>One context per line of <paramref name="lines"/>.</summary>
    /// <param name="path">Repository-relative path; its extension selects the reading (<c>.ocl</c>, <c>.yml</c>, <c>.yaml</c>).</param>
    /// <param name="lines">The file's lines.</param>
    public static IReadOnlyList<string> Of(string path, IReadOnlyList<string> lines)
    {
        if (path.EndsWith(".ocl", StringComparison.Ordinal))
        {
            return Ocl(lines);
        }

        return path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal)
            ? Yaml(lines)
            : lines.Select(_ => "-").ToArray();
    }

    private static string[] Ocl(IReadOnlyList<string> lines)
    {
        var contexts = new string[lines.Count];
        var steps = new List<(string Slug, int Depth)>();
        var depth = 0;
        var heredoc = string.Empty;
        var pending = string.Empty;
        for (var number = 0; number < lines.Count; number++)
        {
            var line = lines[number];
            if (heredoc.Length > 0)
            {
                contexts[number] = steps.Count > 0 ? steps[^1].Slug : "-";
                if (line.Trim(' ', '\t') == heredoc)
                {
                    heredoc = string.Empty;
                }

                continue;
            }

            if (StepHeader.Match(line) is { Success: true } header)
            {
                pending = header.Groups[1].Value;
            }

            var inString = false;
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (inString)
                {
                    if (character == '\\')
                    {
                        index++;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '#' || (character == '/' && index + 1 < line.Length && line[index + 1] == '/'))
                {
                    break;
                }
                else if (character == '{')
                {
                    depth++;
                    if (pending.Length > 0)
                    {
                        steps.Add((pending, depth));
                        pending = string.Empty;
                    }
                }
                else if (character == '}')
                {
                    if (steps.Count > 0 && depth == steps[^1].Depth)
                    {
                        steps.RemoveAt(steps.Count - 1);
                    }

                    depth--;
                }
            }

            contexts[number] = steps.Count > 0 ? steps[^1].Slug : pending.Length > 0 ? pending : "-";
            if (HeredocStart.Match(line) is { Success: true } start)
            {
                heredoc = start.Groups[1].Value;
            }
        }

        return contexts;
    }

    private static string[] Yaml(IReadOnlyList<string> lines)
    {
        var contexts = new string[lines.Count];
        var keys = new List<(string Key, int Indent)>();
        for (var number = 0; number < lines.Count; number++)
        {
            var line = lines[number];
            if (!BlankOrComment.IsMatch(line) && KeyLine.IsMatch(line))
            {
                var indent = line.Length - line.TrimStart(' ').Length;
                var key = line[indent..];
                key = key[..key.IndexOf(':', StringComparison.Ordinal)];
                while (keys.Count > 0 && keys[^1].Indent >= indent)
                {
                    keys.RemoveAt(keys.Count - 1);
                }

                keys.Add((key, indent));
            }

            contexts[number] = keys.Count == 0 ? "-" : string.Concat(keys.Select(entry => "/" + entry.Key));
        }

        return contexts;
    }
}
