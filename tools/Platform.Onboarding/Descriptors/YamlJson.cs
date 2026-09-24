using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Platform.Onboarding.Descriptors;

/// <summary>A YAML document converted to JSON, with the source line of every JSON pointer.</summary>
/// <param name="Root">The document as JSON; <c>null</c> for an empty document or when <paramref name="Error"/> is set.</param>
/// <param name="Lines">1-based source line of each JSON pointer (<c>""</c> is the root).</param>
/// <param name="Error">Why the text is not a usable single YAML document, or <c>null</c>.</param>
/// <param name="ErrorLine">Line of <paramref name="Error"/>, when known.</param>
internal sealed record YamlJsonDocument(JsonNode? Root, IReadOnlyDictionary<string, int> Lines, string? Error, int? ErrorLine = null)
{
    /// <summary>Line of <paramref name="pointer"/>, or of its nearest ancestor that has one.</summary>
    /// <param name="pointer">JSON pointer such as <c>/deployables/0/name</c>.</param>
    public int? LineOf(string pointer)
    {
        for (var current = pointer; ; current = current[..current.LastIndexOf('/')])
        {
            if (Lines.TryGetValue(current, out var line))
            {
                return line;
            }

            if (current.Length == 0)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Converts YAML to JSON with YAML 1.2 core-schema typing: plain scalars become null, booleans, integers or
/// floats when they look like one; quoted and block scalars stay strings. Duplicate keys are errors.
/// </summary>
internal static partial class YamlJson
{
    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    /// <summary>Parses one YAML document.</summary>
    /// <param name="yaml">YAML text.</param>
    public static YamlJsonDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            return new YamlJsonDocument(null, new Dictionary<string, int>(), $"invalid YAML: {ex.Message}", checked((int)ex.Start.Line));
        }

        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0)
        {
            return new YamlJsonDocument(null, lines, null);
        }

        if (stream.Documents.Count > 1)
        {
            return new YamlJsonDocument(null, lines, $"holds {stream.Documents.Count} YAML documents; a descriptor holds one");
        }

        try
        {
            var root = Convert(stream.Documents[0].RootNode, string.Empty, lines);
            return new YamlJsonDocument(root, lines, null);
        }
        catch (YamlJsonException ex)
        {
            return new YamlJsonDocument(null, lines, ex.Message, ex.Line);
        }
    }

    /// <summary>Escapes one JSON pointer segment (RFC 6901).</summary>
    /// <param name="segment">Raw key.</param>
    public static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static JsonNode? Convert(YamlNode node, string pointer, Dictionary<string, int> lines)
    {
        lines[pointer] = checked((int)node.Start.Line);
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { } key })
                    {
                        throw new YamlJsonException("mapping keys must be plain strings", checked((int)keyNode.Start.Line));
                    }

                    if (obj.ContainsKey(key))
                    {
                        throw new YamlJsonException($"duplicate key '{key}'", checked((int)keyNode.Start.Line));
                    }

                    obj[key] = Convert(valueNode, $"{pointer}/{Escape(key)}", lines);
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    array.Add(Convert(item, $"{pointer}/{index}", lines));
                    index++;
                }

                return array;
            case YamlScalarNode scalar:
                return ConvertScalar(scalar);
            default:
                throw new YamlJsonException($"unsupported YAML node {node.NodeType}", checked((int)node.Start.Line));
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any))
        {
            return JsonValue.Create(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (IntegerPattern().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (FloatPattern().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    private sealed class YamlJsonException(string message, int line) : Exception(message)
    {
        public int Line { get; } = line;
    }
}
