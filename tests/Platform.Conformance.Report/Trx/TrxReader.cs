using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Platform.Conformance.Report;

/// <summary>A test outcome reduced to what the conformance report distinguishes.</summary>
internal enum TestVerdict
{
    /// <summary>The test passed.</summary>
    Passed,

    /// <summary>The test failed, errored, timed out or was aborted.</summary>
    Failed,

    /// <summary>No verdict: inconclusive, not executed, skipped or pending.</summary>
    Inconclusive,
}

/// <summary>One <c>UnitTestResult</c> of a TRX file.</summary>
/// <param name="TestName">Fully qualified name without arguments, <c>Namespace.Class.Method</c>.</param>
/// <param name="DisplayName">Name as the adapter reported it, with any arguments.</param>
/// <param name="Outcome">Raw TRX outcome, for example <c>Passed</c> or <c>NotExecuted</c>.</param>
/// <param name="Duration">Duration of the result.</param>
/// <param name="Message">Error or skip message, if any.</param>
/// <param name="StackTrace">Stack trace of a failure, if any.</param>
internal sealed record TrxTestResult(string TestName, string DisplayName, string Outcome, TimeSpan Duration, string? Message, string? StackTrace)
{
    /// <summary>The outcome reduced to <see cref="TestVerdict"/>.</summary>
    public TestVerdict Verdict => TrxReader.ToVerdict(Outcome);
}

/// <summary>The results of one TRX file.</summary>
/// <param name="Path">File path.</param>
/// <param name="Start">Run start, if recorded.</param>
/// <param name="Finish">Run finish, if recorded.</param>
/// <param name="Results">Every test result.</param>
internal sealed record TrxRun(string Path, DateTimeOffset? Start, DateTimeOffset? Finish, IReadOnlyList<TrxTestResult> Results)
{
    /// <summary>Wall-clock duration of the run, when both times are recorded.</summary>
    public TimeSpan? WallClock => Start is { } start && Finish is { } finish && finish >= start ? finish - start : null;
}

/// <summary>Reads Visual Studio TRX files (the .NET-native test result format) with <see cref="System.Xml.Linq"/>.</summary>
internal static class TrxReader
{
    /// <summary>Reads a TRX file.</summary>
    /// <param name="path">TRX path.</param>
    /// <exception cref="InvalidDataException">The file is not TRX.</exception>
    public static TrxRun Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, path);
    }

    /// <summary>Parses TRX from a stream.</summary>
    /// <param name="stream">TRX content.</param>
    /// <param name="path">Name for messages and the result.</param>
    /// <exception cref="InvalidDataException">The content is not TRX.</exception>
    public static TrxRun Parse(Stream stream, string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(stream);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"{path} is not valid XML: {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "TestRun")
        {
            throw new InvalidDataException($"{path} is not a TRX file (the root element is not TestRun).");
        }

        var ns = root.Name.Namespace;
        var definitions = root.Element(ns + "TestDefinitions")?.Elements(ns + "UnitTest")
            .Where(test => test.Attribute("id") is not null)
            .GroupBy(test => (string)test.Attribute("id")!)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? [];
        var results = new List<TrxTestResult>();
        foreach (var result in root.Element(ns + "Results")?.Elements(ns + "UnitTestResult") ?? [])
        {
            var displayName = (string?)result.Attribute("testName") ?? "";
            var testId = (string?)result.Attribute("testId");
            var method = testId is not null && definitions.TryGetValue(testId, out var definition) ? definition.Element(ns + "TestMethod") : null;
            var errorInfo = result.Element(ns + "Output")?.Element(ns + "ErrorInfo");
            results.Add(new TrxTestResult(
                FullName((string?)method?.Attribute("className"), (string?)method?.Attribute("name") ?? displayName),
                displayName,
                (string?)result.Attribute("outcome") ?? "NotExecuted",
                ParseDuration((string?)result.Attribute("duration")),
                Blank(errorInfo?.Element(ns + "Message")?.Value),
                Blank(errorInfo?.Element(ns + "StackTrace")?.Value)));
        }

        var times = root.Element(ns + "Times");
        return new TrxRun(path, ParseTime((string?)times?.Attribute("start")), ParseTime((string?)times?.Attribute("finish")), results);
    }

    /// <summary>Reduces a raw TRX outcome to a <see cref="TestVerdict"/>.</summary>
    /// <param name="outcome">Raw outcome, for example <c>Timeout</c>.</param>
    public static TestVerdict ToVerdict(string outcome) => outcome switch
    {
        "Passed" or "PassedButRunAborted" => TestVerdict.Passed,
        "Failed" or "Error" or "Timeout" or "Aborted" => TestVerdict.Failed,
        _ => TestVerdict.Inconclusive,
    };

    /// <summary>Builds <c>Namespace.Class.Method</c> from a TRX class and method name, dropping any arguments.</summary>
    /// <param name="className">Class name from <c>TestMethod/@className</c>.</param>
    /// <param name="methodName">Method or display name.</param>
    public static string FullName(string? className, string methodName)
    {
        var name = StripArguments(methodName);
        if (string.IsNullOrWhiteSpace(className) || name.StartsWith(className + ".", StringComparison.Ordinal))
        {
            return name;
        }

        return $"{className}.{name}";
    }

    private static string StripArguments(string name)
    {
        var parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        return (parenthesis >= 0 ? name[..parenthesis] : name).Trim();
    }

    private static TimeSpan ParseDuration(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration) ? duration : TimeSpan.Zero;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
