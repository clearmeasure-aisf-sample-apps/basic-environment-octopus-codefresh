namespace Platform.Conformance.Report;

/// <summary>Escaping and truncation for Markdown tables and code blocks.</summary>
internal static class MarkdownText
{
    /// <summary>Makes <paramref name="text"/> safe inside a table cell: one line, pipes escaped, at most <paramref name="maxLength"/> characters.</summary>
    /// <param name="text">Cell text.</param>
    /// <param name="maxLength">Longest cell.</param>
    public static string Cell(string? text, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var singleLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Truncate(singleLine, maxLength).Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>Shortens <paramref name="text"/> to <paramref name="maxLength"/> characters, ending with an ellipsis.</summary>
    /// <param name="text">Text.</param>
    /// <param name="maxLength">Longest result.</param>
    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, Math.Max(0, maxLength - 1)), "…");

    /// <summary>Makes <paramref name="text"/> safe inside a fenced code block.</summary>
    /// <param name="text">Block text.</param>
    /// <param name="maxLength">Longest block.</param>
    public static string Block(string text, int maxLength) =>
        Truncate(text.Replace("```", "'''", StringComparison.Ordinal).TrimEnd(), maxLength);

    /// <summary>Formats a code span for a test name.</summary>
    /// <param name="name">Test name.</param>
    public static string Code(string name) => $"`{name.Replace("`", "'", StringComparison.Ordinal)}`";
}
