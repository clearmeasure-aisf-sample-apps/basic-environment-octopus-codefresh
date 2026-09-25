using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>
/// Turns the POSIX extended regular expressions of <c>scripts/checks/tool-boundaries.sh</c> (grep -E, awk, sed in the C
/// locale) into .NET regexes, so each rule keeps the script's pattern text. Bracket classes such as
/// <c>[[:space:]]</c> become their ASCII sets, and a backslash inside a bracket expression stays a literal backslash.
/// Everything else in the patterns the lint uses means the same in both dialects; lines are matched one at a time, so
/// <c>^</c> and <c>$</c> anchor at the line's ends.
/// </summary>
internal static class PosixPatterns
{
    private static readonly Dictionary<string, string> Classes = new(StringComparer.Ordinal)
    {
        ["alnum"] = "A-Za-z0-9",
        ["alpha"] = "A-Za-z",
        ["blank"] = @" \t",
        ["cntrl"] = @"\x00-\x1F\x7F",
        ["digit"] = "0-9",
        ["graph"] = @"\x21-\x7E",
        ["lower"] = "a-z",
        ["print"] = @"\x20-\x7E",
        ["punct"] = @"!-/:-@\[-`{-~",
        ["space"] = @" \t\n\x0B\f\r",
        ["upper"] = "A-Z",
        ["xdigit"] = "0-9A-Fa-f",
    };

    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex> Cache = new();

    /// <summary>Compiles an extended regular expression (<c>grep -E</c>) as a .NET regex.</summary>
    /// <param name="ere">The pattern as grep receives it, for example <c>type:[[:space:]]*["']?deploy</c>.</param>
    /// <param name="ignoreCase"><c>true</c> for <c>grep -i</c> (ASCII case folding).</param>
    /// <exception cref="ArgumentException">The pattern is not a valid extended regular expression.</exception>
    public static Regex Ere(string ere, bool ignoreCase = false) =>
        Cache.GetOrAdd((ere, ignoreCase), key => new Regex(
            Translate(key.Pattern),
            RegexOptions.CultureInvariant | (key.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)));

    /// <summary>The .NET spelling of an extended regular expression.</summary>
    /// <param name="ere">POSIX extended regular expression.</param>
    public static string Translate(string ere)
    {
        ArgumentNullException.ThrowIfNull(ere);
        var output = new StringBuilder(ere.Length + 16);
        for (var index = 0; index < ere.Length; index++)
        {
            var character = ere[index];
            if (character == '\\' && index + 1 < ere.Length)
            {
                output.Append(character).Append(ere[++index]);
            }
            else if (character == '[')
            {
                index = Bracket(ere, index, output);
            }
            else
            {
                output.Append(character);
            }
        }

        return output.ToString();
    }

    private static int Bracket(string ere, int start, StringBuilder output)
    {
        var index = start + 1;
        output.Append('[');
        if (index < ere.Length && ere[index] == '^')
        {
            output.Append('^');
            index++;
        }

        for (var first = true; index < ere.Length; index++, first = false)
        {
            var character = ere[index];
            if (character == ']' && !first)
            {
                output.Append(']');
                return index;
            }

            if (character == '[' && index + 1 < ere.Length && ere[index + 1] == ':')
            {
                var end = ere.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (end > 0 && Classes.TryGetValue(ere[(index + 2)..end], out var set))
                {
                    output.Append(set);
                    index = end + 1;
                    continue;
                }
            }

            output.Append(character switch
            {
                '\\' => @"\\",
                '[' => @"\[",
                ']' => @"\]",
                '^' => @"\^",
                _ => character.ToString(),
            });
        }

        throw new ArgumentException($"unterminated bracket expression in '{ere}'", nameof(ere));
    }
}

/// <summary>
/// Bash <c>case</c> patterns (<c>*</c> and <c>?</c>) as the script uses them to classify repository-relative paths. In a
/// <c>case</c> pattern <c>*</c> also matches <c>/</c>, unlike a path glob.
/// </summary>
internal static class CasePattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary><c>true</c> when <paramref name="text"/> matches any of the patterns.</summary>
    /// <param name="text">Text to test, usually a repository-relative path.</param>
    /// <param name="patterns">Bash <c>case</c> patterns, for example <c>codefresh/apps/*/pipelines/release-*.yml</c>.</param>
    public static bool Matches(string text, params string[] patterns) =>
        patterns.Any(pattern => Cache.GetOrAdd(pattern, Compile).IsMatch(text));

    private static Regex Compile(string pattern) =>
        new(
            "^" + string.Concat(pattern.Select(character => character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            })) + "$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
}

/// <summary>Path globs in which <c>*</c> matches within one path segment, for the TB23 exception list.</summary>
internal static class PathGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary><c>true</c> when the repository-relative <paramref name="path"/> matches <paramref name="glob"/>.</summary>
    /// <param name="path">Path with forward slashes.</param>
    /// <param name="glob">Glob such as <c>containers/apps/*/*/migrate.sh</c>.</param>
    public static bool Matches(string path, string glob) =>
        Cache.GetOrAdd(glob, pattern => new Regex(
            "^" + string.Concat(pattern.Select(character => character switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(character.ToString()),
            })) + "$",
            RegexOptions.CultureInvariant)).IsMatch(path);
}
