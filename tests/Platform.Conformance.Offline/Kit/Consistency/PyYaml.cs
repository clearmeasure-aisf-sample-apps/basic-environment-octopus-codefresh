using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>YAML that PyYAML's safe loader rejects; the message is the first line of the reason.</summary>
internal sealed class YamlLoadException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">First line of the reason.</param>
    public YamlLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Loads YAML as PyYAML's <c>yaml.safe_load</c> and <c>yaml.safe_load_all</c> do, on top of YamlDotNet's parser, so the
/// checks see the values the script sees: YAML 1.1 implicit types (<c>yes</c> and <c>on</c> are booleans, <c>017</c> is
/// octal, <c>1e3</c> is a string), merge keys, a duplicate key keeps the last value, an alias shares one object, an
/// unknown tag or a duplicate anchor is an error. Mappings load as <see cref="PyDict"/>, sequences as lists, integers as
/// <see cref="BigInteger"/>, floats as <see cref="double"/>, timestamps as <see cref="PyTimestamp"/>.
/// </summary>
internal static partial class PyYaml
{
    private const string Prefix = "tag:yaml.org,2002:";
    private const string NullTag = Prefix + "null";
    private const string BoolTag = Prefix + "bool";
    private const string IntTag = Prefix + "int";
    private const string FloatTag = Prefix + "float";
    private const string BinaryTag = Prefix + "binary";
    private const string TimestampTag = Prefix + "timestamp";
    private const string OmapTag = Prefix + "omap";
    private const string PairsTag = Prefix + "pairs";
    private const string SetTag = Prefix + "set";
    private const string StrTag = Prefix + "str";
    private const string SeqTag = Prefix + "seq";
    private const string MapTag = Prefix + "map";
    private const string MergeTag = Prefix + "merge";
    private const string ValueTag = Prefix + "value";
    private const string YamlTag = Prefix + "yaml";

    /// <summary><c>yaml.safe_load(text)</c>: the only document, or <c>null</c> for an empty stream.</summary>
    /// <param name="text">YAML text.</param>
    /// <exception cref="YamlLoadException">The text does not load (PyYAML's <c>YAMLError</c>).</exception>
    /// <exception cref="PyException">A value PyYAML cannot build (<c>ValueError</c>, <c>KeyError</c>).</exception>
    public static object? Load(string text)
    {
        var events = new EventReader(Prepare(text));
        if (events.Current is StreamEnd)
        {
            events.CheckScanner(long.MaxValue);
            return null;
        }

        var node = new Composer(events).Document();
        if (events.Current is not StreamEnd)
        {
            throw new YamlLoadException("expected a single document in the stream");
        }

        events.CheckScanner(long.MaxValue);
        return new Constructor().Construct(node);
    }

    /// <summary><c>list(yaml.safe_load_all(text))</c>: every document, empty ones as <c>null</c>.</summary>
    /// <param name="text">YAML text.</param>
    /// <exception cref="YamlLoadException">A document does not load.</exception>
    /// <exception cref="PyException">A value PyYAML cannot build.</exception>
    public static IReadOnlyList<object?> LoadAll(string text)
    {
        var events = new EventReader(Prepare(text));
        var documents = new List<object?>();
        while (events.Current is not StreamEnd)
        {
            var node = new Composer(events).Document();
            documents.Add(new Constructor().Construct(node));
        }

        events.CheckScanner(long.MaxValue);
        return documents;
    }

    /// <summary>PyYAML's reader: refuses non-printable characters and skips a byte order mark at the start.</summary>
    private static string Prepare(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '\t' or '\n' or '\r' or (>= ' ' and <= '~') or '\x85' or (>= '\xa0' and <= '\ud7ff') or (>= '\ue000' and <= '\ufffd'))
            {
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
                continue;
            }

            throw new YamlLoadException($"unacceptable character #x{(int)character:x4}: special characters are not allowed");
        }

        return text.StartsWith('\ufeff') ? text[1..] : text;
    }

    /// <summary>The tag PyYAML's resolver gives a plain scalar (YAML 1.1 implicit types, first character first).</summary>
    private static string Implicit(string value)
    {
        if (value.Length == 0)
        {
            return NullTag;
        }

        var first = value[0];
        if ("yYnNtTfFoO".Contains(first, StringComparison.Ordinal) && BoolPattern().IsMatch(value))
        {
            return BoolTag;
        }

        if ("-+0123456789.".Contains(first, StringComparison.Ordinal) && FloatPattern().IsMatch(value))
        {
            return FloatTag;
        }

        if ("-+0123456789".Contains(first, StringComparison.Ordinal) && IntPattern().IsMatch(value))
        {
            return IntTag;
        }

        if (first == '<' && value == "<<")
        {
            return MergeTag;
        }

        if ("~nN".Contains(first, StringComparison.Ordinal) && NullPattern().IsMatch(value))
        {
            return NullTag;
        }

        if (char.IsAsciiDigit(first) && TimestampPattern().IsMatch(value))
        {
            return TimestampTag;
        }

        if (first == '=' && value == "=")
        {
            return ValueTag;
        }

        return "!&*".Contains(first, StringComparison.Ordinal) && value.Length == 1 ? YamlTag : StrTag;
    }

    [GeneratedRegex("^(?:yes|Yes|YES|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$")]
    private static partial Regex BoolPattern();

    [GeneratedRegex(@"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();

    [GeneratedRegex("^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$")]
    private static partial Regex IntPattern();

    [GeneratedRegex("^(?:~|null|Null|NULL|)$")]
    private static partial Regex NullPattern();

    [GeneratedRegex(@"^(?:[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]|[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?(?:[Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9][0-9]?(?::[0-9][0-9])?))?)$")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^(?<year>[0-9][0-9][0-9][0-9])-(?<month>[0-9][0-9]?)-(?<day>[0-9][0-9]?)(?:(?:[Tt]|[ \t]+)(?<hour>[0-9][0-9]?):(?<minute>[0-9][0-9]):(?<second>[0-9][0-9])(?:\.(?<fraction>[0-9]*))?(?:[ \t]*(?<tz>Z|(?<sign>[-+])(?<tzhour>[0-9][0-9]?)(?::(?<tzminute>[0-9][0-9]))?))?)?$")]
    private static partial Regex TimestampParts();

    /// <summary>A node of the composed document (PyYAML's ScalarNode, SequenceNode and MappingNode).</summary>
    private abstract class Node(string tag)
    {
        public string Tag { get; set; } = tag;

        public abstract string Kind { get; }
    }

    private sealed class ScalarNode(string tag, string value) : Node(tag)
    {
        public string Value { get; } = value;

        public override string Kind => "scalar";
    }

    private sealed class SequenceNode(string tag) : Node(tag)
    {
        public List<Node> Items { get; } = [];

        public override string Kind => "sequence";
    }

    private sealed class MappingNode(string tag) : Node(tag)
    {
        public List<(Node Key, Node Value)> Pairs { get; set; } = [];

        public override string Kind => "mapping";
    }

    /// <summary>
    /// YamlDotNet's parser, its errors turned into <see cref="YamlLoadException"/>, with two rules of PyYAML's scanner that
    /// YamlDotNet does not apply: a tab outside a quoted scalar, a block scalar or a comment is an error (PyYAML skips only
    /// spaces between tokens and ends a plain scalar at a tab), and so is a mapping value without a key (<c>: v</c>).
    /// </summary>
    private sealed class EventReader
    {
        private readonly Parser parser;
        private readonly string text;
        private readonly List<(long Start, long End)> literals = [];
        private readonly List<(long Start, long End)> flows = [];
        private readonly Stack<(bool Flow, long Start)> collections = new();
        private long checkedUpTo;

        public EventReader(string text)
        {
            this.text = text;
            parser = new Parser(new StringReader(text));
            Next();
            if (Current is not StreamStart)
            {
                throw new YamlLoadException("expected the start of a stream");
            }

            Next();
        }

        public ParsingEvent Current { get; private set; } = null!;

        public void Next()
        {
            bool moved;
            try
            {
                moved = parser.MoveNext();
            }
            catch (YamlException exception)
            {
                var message = exception.Message.Split('\n')[0].Trim();
                throw new YamlLoadException(exception.Start.Line > 0 ? $"{message} (line {exception.Start.Line}, column {exception.Start.Column})" : message);
            }
            catch (Exception exception)
            {
                // YamlDotNet's scanner gives up on some malformed input (an unclosed flow sequence) with a bare
                // InvalidOperationException; PyYAML reports a parse error there.
                throw new YamlLoadException($"the YAML scanner stopped ({exception.GetType().Name}: {exception.Message})");
            }

            Current = moved ? parser.Current! : throw new YamlLoadException("unexpected end of the stream");
            switch (Current)
            {
                case Scalar { Style: not ScalarStyle.Plain } scalar:
                    literals.Add((scalar.Start.Index, scalar.End.Index));
                    break;
                case SequenceStart sequence:
                    collections.Push((sequence.Style == SequenceStyle.Flow, sequence.Start.Index));
                    break;
                case MappingStart mapping:
                    collections.Push((mapping.Style == MappingStyle.Flow, mapping.Start.Index));
                    break;
                case SequenceEnd or MappingEnd when collections.TryPop(out var collection) && collection.Flow:
                    flows.Add((collection.Start, Current.End.Index));
                    break;
            }
        }

        /// <summary>
        /// Applies PyYAML's scanner rules up to <paramref name="upTo"/>: no tab outside quoted and block scalars and comments,
        /// and, in block context, no line whose first token is a value indicator <c>: </c> unless an explicit key
        /// <c>? </c> stands at the same column above.
        /// </summary>
        public void CheckScanner(long upTo)
        {
            var end = (int)Math.Min(upTo, text.Length);
            if (end <= checkedUpTo)
            {
                return;
            }

            var index = LineStart((int)checkedUpTo);
            var literal = 0;
            var comment = false;
            var lineStart = true;
            for (; index < end; index++)
            {
                var character = text[index];
                if (IsBreak(character))
                {
                    comment = false;
                    lineStart = true;
                    continue;
                }

                if (lineStart)
                {
                    lineStart = false;
                    var token = FirstToken(index);
                    if (token >= checkedUpTo && token < end && IsValueIndicator(token) && !Within(literals, token) && !Within(flows, token) && !ExplicitKeyAbove(index, token - index))
                    {
                        throw new YamlLoadException($"while parsing a block mapping: a value without a key ({Position(token)})");
                    }
                }

                while (literal < literals.Count && literals[literal].End <= index)
                {
                    literal++;
                }

                if (comment || (literal < literals.Count && literals[literal].Start <= index))
                {
                    continue;
                }

                if (character == '#' && (index == 0 || text[index - 1] is ' ' or '\t' || IsBreak(text[index - 1])))
                {
                    comment = true;
                }
                else if (character == '\t' && index >= checkedUpTo)
                {
                    throw new YamlLoadException($"while scanning for the next token: found a tab that PyYAML accepts only in quoted and block scalars and comments ({Position(index)})");
                }
            }

            checkedUpTo = end;
        }

        /// <summary><c>true</c> when an empty key scalar at <paramref name="index"/> stands for a missing key (<c>: v</c>) rather than an explicit <c>?</c> key.</summary>
        public bool IsMissingKey(long index)
        {
            var position = (int)Math.Min(index, text.Length);
            if (position < text.Length && text[position] == ':')
            {
                return true;
            }

            while (position > 0 && text[position - 1] is ' ' or '\t')
            {
                position--;
            }

            return position > 0 && text[position - 1] == ':';
        }

        private static bool IsBreak(char character) => character is '\n' or '\r' or '\x85' or (char)0x2028 or (char)0x2029;

        private static bool Within(List<(long Start, long End)> ranges, int index) => ranges.Any(range => range.Start <= index && index < range.End);

        private int LineStart(int index)
        {
            while (index > 0 && !IsBreak(text[index - 1]))
            {
                index--;
            }

            return index;
        }

        /// <summary>Position of the first token of the line at <paramref name="lineStart"/>, after indentation and block sequence indicators <c>- </c>.</summary>
        private int FirstToken(int lineStart)
        {
            var position = lineStart;
            while (true)
            {
                while (position < text.Length && text[position] == ' ')
                {
                    position++;
                }

                if (position < text.Length && text[position] == '-' && IsSeparator(position + 1))
                {
                    position++;
                    continue;
                }

                return position;
            }
        }

        /// <summary><c>true</c> for <c>:</c> followed by a space, a tab, a line break or the end.</summary>
        private bool IsValueIndicator(int position) => position < text.Length && text[position] == ':' && IsSeparator(position + 1);

        private bool IsSeparator(int position) => position >= text.Length || text[position] is ' ' or '\t' || IsBreak(text[position]);

        /// <summary>
        /// <c>true</c> when the nearest line above that starts at or left of <paramref name="column"/> (blank lines, comments and
        /// deeper lines skipped) starts with an explicit key <c>? </c> at that column.
        /// </summary>
        private bool ExplicitKeyAbove(int lineStart, int column)
        {
            while (lineStart > 0)
            {
                lineStart = LineStart(lineStart - 1);
                var token = FirstToken(lineStart);
                if (token >= text.Length || IsBreak(text[token]) || text[token] == '#' || Within(literals, token))
                {
                    continue;
                }

                var tokenColumn = token - lineStart;
                if (tokenColumn > column)
                {
                    continue;
                }

                return tokenColumn == column && text[token] == '?' && IsSeparator(token + 1);
            }

            return false;
        }

        private string Position(int index)
        {
            var line = 1 + text.AsSpan(0, index).Count('\n');
            return $"line {line}, column {index - LineStart(index) + 1}";
        }
    }

    /// <summary>PyYAML's composer: nodes with resolved tags; anchors are per document and may not repeat.</summary>
    private sealed class Composer(EventReader events)
    {
        private readonly Dictionary<string, Node> anchors = new(StringComparer.Ordinal);

        public Node Document()
        {
            if (events.Current is not DocumentStart)
            {
                throw new YamlLoadException("expected the start of a document");
            }

            events.Next();
            var node = Compose(0, flow: false);
            if (events.Current is not DocumentEnd)
            {
                throw new YamlLoadException("expected the end of a document");
            }

            events.Next();
            events.CheckScanner(events.Current.Start.Index);
            return node;
        }

        private Node Compose(int depth, bool flow)
        {
            if (depth > Py.MaxDepth)
            {
                throw new PyException("RecursionError", "maximum recursion depth exceeded");
            }

            var current = events.Current;
            if (current is AnchorAlias alias)
            {
                events.Next();
                return anchors.TryGetValue(alias.Value.Value, out var target)
                    ? target
                    : throw new YamlLoadException($"found undefined alias {Py.Repr(alias.Value.Value)}");
            }

            if (current is not NodeEvent nodeEvent)
            {
                throw new YamlLoadException($"expected a node, but found {current.GetType().Name}");
            }

            var anchor = nodeEvent.Anchor.IsEmpty ? null : nodeEvent.Anchor.Value;
            if (anchor is not null && anchors.ContainsKey(anchor))
            {
                throw new YamlLoadException($"found duplicate anchor {Py.Repr(anchor)}; first occurrence");
            }

            events.Next();
            switch (current)
            {
                case Scalar scalar:
                    // YamlDotNet accepts, inside a flow collection, a plain scalar that starts with an indicator; PyYAML does not.
                    if (flow && scalar.Style == ScalarStyle.Plain && scalar.Value.Length > 0 && "@%|>`:".Contains(scalar.Value[0], StringComparison.Ordinal))
                    {
                        throw new YamlLoadException($"while scanning for the next token: a plain scalar cannot start with '{scalar.Value[0]}' in a flow collection");
                    }

                    var tag = !scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific
                        ? scalar.Tag.Value
                        : scalar.Tag.IsNonSpecific || scalar.Style == ScalarStyle.Plain ? Implicit(scalar.Value) : StrTag;
                    return Register(anchor, new ScalarNode(tag, scalar.Value));
                case SequenceStart start:
                    var sequence = Register(anchor, new SequenceNode(Explicit(start.Tag) ?? SeqTag));
                    var inFlowSequence = flow || start.Style == SequenceStyle.Flow;
                    while (events.Current is not SequenceEnd)
                    {
                        sequence.Items.Add(Compose(depth + 1, inFlowSequence));
                    }

                    events.Next();
                    return sequence;
                case MappingStart start:
                    var mapping = Register(anchor, new MappingNode(Explicit(start.Tag) ?? MapTag));
                    var inFlowMapping = flow || start.Style == MappingStyle.Flow;
                    while (events.Current is not MappingEnd)
                    {
                        if (events.Current is Scalar { Value.Length: 0, Style: ScalarStyle.Plain } empty && empty.Tag.IsEmpty && empty.Anchor.IsEmpty
                            && empty.Start.Index == empty.End.Index && events.IsMissingKey(empty.Start.Index))
                        {
                            throw new YamlLoadException($"while parsing a mapping: a value without a key (line {empty.Start.Line}, column {empty.Start.Column})");
                        }

                        var key = Compose(depth + 1, inFlowMapping);
                        mapping.Pairs.Add((key, Compose(depth + 1, inFlowMapping)));
                    }

                    events.Next();
                    return mapping;
                default:
                    throw new YamlLoadException($"expected a node, but found {current.GetType().Name}");
            }
        }

        private static string? Explicit(TagName tag) => tag.IsEmpty || tag.IsNonSpecific ? null : tag.Value;

        private T Register<T>(string? anchor, T node)
            where T : Node
        {
            if (anchor is not null)
            {
                anchors[anchor] = node;
            }

            return node;
        }
    }

    /// <summary>PyYAML's SafeConstructor: one object per node, containers registered before their items (recursion allowed).</summary>
    private sealed class Constructor
    {
        private readonly Dictionary<Node, object?> built = new(ReferenceEqualityComparer.Instance);
        private int depth;

        public object? Construct(Node node)
        {
            if (built.TryGetValue(node, out var existing))
            {
                return existing;
            }

            if (++depth > Py.MaxDepth)
            {
                throw new PyException("RecursionError", "maximum recursion depth exceeded");
            }

            try
            {
                return Build(node);
            }
            finally
            {
                depth--;
            }
        }

        private static string ScalarValue(Node node)
        {
            if (node is MappingNode mapping)
            {
                foreach (var (key, value) in mapping.Pairs)
                {
                    if (key.Tag == ValueTag)
                    {
                        return ScalarValue(value);
                    }
                }
            }

            return node is ScalarNode scalar ? scalar.Value : throw new YamlLoadException($"expected a scalar node, but found {node.Kind}");
        }

        private static void Flatten(MappingNode node, int level)
        {
            if (level > Py.MaxDepth)
            {
                throw new PyException("RecursionError", "maximum recursion depth exceeded");
            }

            var merge = new List<(Node Key, Node Value)>();
            var index = 0;
            while (index < node.Pairs.Count)
            {
                var (key, value) = node.Pairs[index];
                if (key.Tag == MergeTag)
                {
                    node.Pairs.RemoveAt(index);
                    switch (value)
                    {
                        case MappingNode source:
                            Flatten(source, level + 1);
                            merge.AddRange(source.Pairs);
                            break;
                        case SequenceNode sources:
                            var submerge = new List<List<(Node Key, Node Value)>>();
                            foreach (var item in sources.Items)
                            {
                                if (item is not MappingNode source)
                                {
                                    throw new YamlLoadException("while constructing a mapping");
                                }

                                Flatten(source, level + 1);
                                submerge.Add(source.Pairs);
                            }

                            submerge.Reverse();
                            submerge.ForEach(merge.AddRange);
                            break;
                        default:
                            throw new YamlLoadException("while constructing a mapping");
                    }
                }
                else
                {
                    if (key.Tag == ValueTag)
                    {
                        key.Tag = StrTag;
                    }

                    index++;
                }
            }

            if (merge.Count > 0)
            {
                node.Pairs = [.. merge, .. node.Pairs];
            }
        }

        private static BigInteger IntValue(Node node)
        {
            var text = ScalarValue(node).Replace("_", string.Empty, StringComparison.Ordinal);
            if (text.Length == 0)
            {
                throw new PyException("IndexError", "string index out of range");
            }

            var sign = text[0] == '-' ? BigInteger.MinusOne : BigInteger.One;
            if (text[0] is '+' or '-')
            {
                text = text[1..];
            }

            if (text == "0")
            {
                return BigInteger.Zero;
            }

            if (text.StartsWith("0b", StringComparison.Ordinal))
            {
                return sign * Digits(text[2..], 2);
            }

            if (text.StartsWith("0x", StringComparison.Ordinal))
            {
                return sign * Digits(text[2..], 16);
            }

            if (text.StartsWith('0'))
            {
                return sign * Digits(text, 8);
            }

            if (text.Contains(':', StringComparison.Ordinal))
            {
                var total = BigInteger.Zero;
                foreach (var part in text.Split(':'))
                {
                    total = (total * 60) + Digits(part, 10);
                }

                return sign * total;
            }

            return sign * Digits(text, 10);
        }

        private static BigInteger Digits(string text, int radix)
        {
            var value = BigInteger.Zero;
            foreach (var character in text)
            {
                var digit = char.IsAsciiDigit(character) ? character - '0' : char.IsAsciiLetter(character) ? char.ToLowerInvariant(character) - 'a' + 10 : 99;
                if (digit >= radix)
                {
                    throw new PyException("ValueError", $"invalid literal for int() with base {radix}: {Py.Repr(text)}");
                }

                value = (value * radix) + digit;
            }

            return text.Length > 0 ? value : throw new PyException("ValueError", $"invalid literal for int() with base {radix}: ''");
        }

        private static double FloatValue(Node node)
        {
            var text = ScalarValue(node).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (text.Length == 0)
            {
                throw new PyException("IndexError", "string index out of range");
            }

            var sign = text[0] == '-' ? -1.0 : 1.0;
            if (text[0] is '+' or '-')
            {
                text = text[1..];
            }

            if (text == ".inf")
            {
                return sign * double.PositiveInfinity;
            }

            if (text == ".nan")
            {
                return double.NaN;
            }

            if (text.Contains(':', StringComparison.Ordinal))
            {
                var total = 0.0;
                foreach (var part in text.Split(':'))
                {
                    total = (total * 60) + ParseFloat(part);
                }

                return sign * total;
            }

            return sign * ParseFloat(text);
        }

        private static double ParseFloat(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new PyException("ValueError", $"could not convert string to float: {Py.Repr(text)}");

        private static object? NullValue(Node node)
        {
            ScalarValue(node);
            return null;
        }

        private static bool BoolValue(Node node) => ScalarValue(node).ToLowerInvariant() switch
        {
            "yes" or "true" or "on" => true,
            "no" or "false" or "off" => false,
            var other => throw PyException.KeyError(other),
        };

        private static byte[] BinaryValue(Node node)
        {
            var text = new string(ScalarValue(node).Where(character => !char.IsWhiteSpace(character)).ToArray());
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException exception)
            {
                throw new YamlLoadException($"failed to decode base64 data: {exception.Message}");
            }
        }

        private static PyTimestamp TimestampValue(Node node)
        {
            var match = TimestampParts().Match(ScalarValue(node));
            if (!match.Success)
            {
                throw new PyException("AttributeError", "'NoneType' object has no attribute 'groupdict'");
            }

            int Part(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
            var (year, month, day) = (Part("year"), Part("month"), Part("day"));
            if (year < 1)
            {
                throw new PyException("ValueError", $"year {year} is out of range");
            }

            if (month is < 1 or > 12)
            {
                throw new PyException("ValueError", "month must be in 1..12");
            }

            if (day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                throw new PyException("ValueError", "day is out of range for month");
            }

            var date = $"{year:0000}-{month:00}-{day:00}";
            if (!match.Groups["hour"].Success)
            {
                return new PyTimestamp(date, $"datetime.date({year}, {month}, {day})", IsDateTime: false);
            }

            var (hour, minute, second) = (Part("hour"), Part("minute"), Part("second"));
            if (hour > 23 || minute > 59 || second > 59)
            {
                throw new PyException("ValueError", hour > 23 ? "hour must be in 0..23" : minute > 59 ? "minute must be in 0..59" : "second must be in 0..59");
            }

            var fraction = match.Groups["fraction"].Value;
            var micro = fraction.Length > 0 ? int.Parse(fraction.Length > 6 ? fraction[..6] : fraction.PadRight(6, '0'), CultureInfo.InvariantCulture) : 0;
            var zone = string.Empty;
            var zoneRepr = string.Empty;
            if (match.Groups["sign"].Success)
            {
                var offset = (Part("tzhour") * 60) + (match.Groups["tzminute"].Success ? Part("tzminute") : 0);
                if (offset >= 24 * 60)
                {
                    throw new PyException("ValueError", "offset must be a timedelta strictly between -timedelta(hours=24) and timedelta(hours=24).");
                }

                var negative = match.Groups["sign"].Value == "-" && offset > 0;
                zone = (negative ? "-" : "+") + $"{offset / 60:00}:{offset % 60:00}";
                zoneRepr = offset == 0 ? "datetime.timezone.utc" : $"datetime.timezone({TimedeltaRepr(negative ? -offset * 60 : offset * 60)})";
            }
            else if (match.Groups["tz"].Success)
            {
                zone = "+00:00";
                zoneRepr = "datetime.timezone.utc";
            }

            // repr() drops a zero microsecond, then a zero second.
            var fields = new List<int> { year, month, day, hour, minute, second, micro };
            for (var drop = 0; drop < 2 && fields[^1] == 0; drop++)
            {
                fields.RemoveAt(fields.Count - 1);
            }

            var text = $"{date} {hour:00}:{minute:00}:{second:00}" + (micro > 0 ? $".{micro:000000}" : string.Empty) + zone;
            var representation = $"datetime.datetime({string.Join(", ", fields)}{(zoneRepr.Length > 0 ? ", tzinfo=" + zoneRepr : string.Empty)})";
            return new PyTimestamp(text, representation, IsDateTime: true);
        }

        /// <summary><c>repr(timedelta(seconds=…))</c>: days and seconds normalized as Python normalizes them.</summary>
        private static string TimedeltaRepr(int seconds)
        {
            var days = (int)Math.Floor(seconds / 86400.0);
            var rest = seconds - (days * 86400);
            var parts = new List<string>();
            if (days != 0)
            {
                parts.Add($"days={days}");
            }

            if (rest != 0)
            {
                parts.Add($"seconds={rest}");
            }

            return $"datetime.timedelta({(parts.Count == 0 ? "0" : string.Join(", ", parts))})";
        }

        private object? Build(Node node)
        {
            switch (node.Tag)
            {
                case SeqTag:
                    var list = new List<object?>();
                    built[node] = list;
                    if (node is not SequenceNode sequence)
                    {
                        throw new YamlLoadException($"expected a sequence node, but found {node.Kind}");
                    }

                    list.AddRange(sequence.Items.Select(Construct));
                    return list;
                case MapTag:
                    var dict = new PyDict();
                    built[node] = dict;
                    Fill(node, dict);
                    return dict;
                case SetTag:
                    var set = new PySet();
                    built[node] = set;
                    var members = new PyDict();
                    Fill(node, members);
                    foreach (var member in members.Keys)
                    {
                        set.Add(member);
                    }

                    return set;
                case OmapTag or PairsTag:
                    var pairs = new List<object?>();
                    built[node] = pairs;
                    var context = node.Tag == OmapTag ? "while constructing an ordered map" : "while constructing pairs";
                    if (node is not SequenceNode items)
                    {
                        throw new YamlLoadException(context);
                    }

                    foreach (var item in items.Items)
                    {
                        if (item is not MappingNode { Pairs.Count: 1 } single)
                        {
                            throw new YamlLoadException(context);
                        }

                        var key = Construct(single.Pairs[0].Key);
                        pairs.Add(new PyTuple([key, Construct(single.Pairs[0].Value)]));
                    }

                    return pairs;
            }

            object? value = node.Tag switch
            {
                NullTag => NullValue(node),
                BoolTag => BoolValue(node),
                IntTag => IntValue(node),
                FloatTag => FloatValue(node),
                BinaryTag => BinaryValue(node),
                TimestampTag => TimestampValue(node),
                StrTag => ScalarValue(node),
                _ => throw new YamlLoadException($"could not determine a constructor for the tag {Py.Repr(node.Tag)}"),
            };
            built[node] = value;
            return value;
        }

        private void Fill(Node node, PyDict dict)
        {
            if (node is not MappingNode mapping)
            {
                throw new YamlLoadException($"expected a mapping node, but found {node.Kind}");
            }

            Flatten(mapping, 0);
            foreach (var (keyNode, valueNode) in mapping.Pairs)
            {
                var key = Construct(keyNode);
                if (key is PyDict or List<object?> or PySet)
                {
                    throw new YamlLoadException("while constructing a mapping");
                }

                dict.Set(key, Construct(valueNode));
            }
        }
    }
}
