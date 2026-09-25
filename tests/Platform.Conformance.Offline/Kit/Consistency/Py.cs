using System.Globalization;
using System.Numerics;
using System.Text;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>
/// A Python exception, raised where the Python of <c>scripts/checks/consistency.sh</c> raises one, so a check that crashes
/// in the script crashes here too and is reported as C99 with the same type and message.
/// </summary>
internal sealed class PyException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="type">Python exception type, for example <c>KeyError</c>.</param>
    /// <param name="message">What <c>str(exception)</c> prints.</param>
    public PyException(string type, string message)
        : base(message)
    {
        Type = type;
    }

    /// <summary>Python exception type, for example <c>KeyError</c>.</summary>
    public string Type { get; }

    /// <summary><c>KeyError</c> of a missing key (Python prints the key's repr).</summary>
    /// <param name="key">The key.</param>
    public static PyException KeyError(object? key) => new("KeyError", Py.Repr(key));

    /// <summary><c>TypeError</c> with a message.</summary>
    /// <param name="message">Message.</param>
    public static PyException TypeError(string message) => new("TypeError", message);

    /// <summary><c>AttributeError</c> of a method called on the wrong type, such as <c>.get</c> on a list.</summary>
    /// <param name="value">The receiver.</param>
    /// <param name="attribute">The attribute.</param>
    public static PyException AttributeError(object? value, string attribute) =>
        new("AttributeError", $"'{Py.TypeName(value)}' object has no attribute '{attribute}'");
}

/// <summary>A dict key or set member as Python hashes it: <c>1</c>, <c>1.0</c> and <c>True</c> are one key.</summary>
/// <param name="Kind">N none, I integral number, F other float, S str, T timestamp, B bytes, U tuple.</param>
/// <param name="Text">Text of a str, timestamp, bytes or tuple key.</param>
/// <param name="Integer">Value of an integral number.</param>
/// <param name="Real">Value of a non-integral float.</param>
internal readonly record struct PyKey(char Kind, string Text, BigInteger Integer, double Real)
{
    /// <summary>The key of a value; unhashable values (dict, list, set) raise <c>TypeError</c> as in Python.</summary>
    /// <param name="value">The value.</param>
    public static PyKey Of(object? value) => value switch
    {
        null => new('N', string.Empty, BigInteger.Zero, 0),
        bool flag => new('I', string.Empty, flag ? BigInteger.One : BigInteger.Zero, 0),
        BigInteger integer => new('I', string.Empty, integer, 0),
        double real when double.IsFinite(real) && Math.Floor(real) == real => new('I', string.Empty, new BigInteger(real), 0),
        double real => new('F', string.Empty, BigInteger.Zero, real),
        string text => new('S', text, BigInteger.Zero, 0),
        PyTimestamp stamp => new('T', (stamp.IsDateTime ? "T" : "D") + stamp.Text, BigInteger.Zero, 0),
        byte[] bytes => new('B', Convert.ToBase64String(bytes), BigInteger.Zero, 0),
        PyTuple tuple => new('U', string.Join('\u0001', tuple.Items.Select(item => Of(item).ToString())), BigInteger.Zero, 0),
        _ => throw PyException.TypeError($"unhashable type: '{Py.TypeName(value)}'"),
    };
}

/// <summary>A Python dict: insertion-ordered; assigning an existing key keeps its position and replaces the value.</summary>
internal sealed class PyDict
{
    private readonly Dictionary<PyKey, int> index = new();
    private readonly List<KeyValuePair<object?, object?>> items = [];

    /// <summary>Number of keys.</summary>
    public int Count => items.Count;

    /// <summary>Keys in insertion order.</summary>
    public IEnumerable<object?> Keys => items.Select(item => item.Key);

    /// <summary>Key-value pairs in insertion order.</summary>
    public IReadOnlyList<KeyValuePair<object?, object?>> Items => items;

    /// <summary><c>dict[key] = value</c>.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Value.</param>
    public void Set(object? key, object? value)
    {
        var hashed = PyKey.Of(key);
        if (index.TryGetValue(hashed, out var position))
        {
            items[position] = new KeyValuePair<object?, object?>(items[position].Key, value);
            return;
        }

        index[hashed] = items.Count;
        items.Add(new KeyValuePair<object?, object?>(key, value));
    }

    /// <summary>The value of a key, if present.</summary>
    /// <param name="key">Key.</param>
    /// <param name="value">Its value.</param>
    public bool TryGetValue(object? key, out object? value)
    {
        if (index.TryGetValue(PyKey.Of(key), out var position))
        {
            value = items[position].Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary><c>key in dict</c>.</summary>
    /// <param name="key">Key.</param>
    public bool ContainsKey(object? key) => index.ContainsKey(PyKey.Of(key));
}

/// <summary>A Python set.</summary>
internal sealed class PySet
{
    private readonly Dictionary<PyKey, object?> members = new();

    /// <summary>Creates an empty set.</summary>
    public PySet()
    {
    }

    /// <summary>Creates a set of the items, as <c>set(items)</c> does.</summary>
    /// <param name="items">Items.</param>
    public PySet(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>Number of members.</summary>
    public int Count => members.Count;

    /// <summary>Members, in no particular order.</summary>
    public IEnumerable<object?> Items => members.Values;

    /// <summary><c>set.add(item)</c>.</summary>
    /// <param name="item">Item.</param>
    public void Add(object? item) => members.TryAdd(PyKey.Of(item), item);

    /// <summary><c>item in set</c>.</summary>
    /// <param name="item">Item.</param>
    public bool Contains(object? item) => members.ContainsKey(PyKey.Of(item));

    /// <summary><c>self - other</c>.</summary>
    /// <param name="other">Members to leave out.</param>
    public PySet Except(PySet other) => new(Items.Where(item => !other.Contains(item)));
}

/// <summary>A Python tuple (the items of <c>!!omap</c> and <c>!!pairs</c>).</summary>
/// <param name="Items">Items.</param>
internal sealed record PyTuple(IReadOnlyList<object?> Items);

/// <summary>A <c>datetime.date</c> or <c>datetime.datetime</c> loaded from a YAML timestamp.</summary>
/// <param name="Text">What <c>str()</c> prints, for example <c>2026-09-24</c>.</param>
/// <param name="Representation">What <c>repr()</c> prints, for example <c>datetime.date(2026, 9, 24)</c>.</param>
/// <param name="IsDateTime"><c>true</c> for a datetime, <c>false</c> for a date.</param>
internal sealed record PyTimestamp(string Text, string Representation, bool IsDateTime);

/// <summary>
/// Python semantics that the checks of <c>scripts/checks/consistency.sh</c> rely on: <c>str()</c>, <c>repr()</c>, truth,
/// equality, <c>in</c>, iteration, <c>dict.get</c>, subscripts, sorting and <c>str.splitlines()</c>, over the values
/// <see cref="PyYaml"/> loads (<see cref="PyDict"/>, lists, <see cref="BigInteger"/>, <see cref="double"/>, strings).
/// </summary>
internal static class Py
{
    /// <summary>Deepest nesting the traversals follow before raising <c>RecursionError</c>, as Python's recursion limit does.</summary>
    public const int MaxDepth = 900;

    /// <summary>Python's order of <c>str</c> values: by code point.</summary>
    public static IComparer<string> StringOrder { get; } = Comparer<string>.Create(CompareStrings);

    /// <summary>Python's name of the value's type, as error messages print it.</summary>
    /// <param name="value">The value.</param>
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        bool => "bool",
        BigInteger => "int",
        double => "float",
        string => "str",
        PyDict => "dict",
        PySet => "set",
        PyTuple => "tuple",
        PyTimestamp stamp => stamp.IsDateTime ? "datetime" : "date",
        byte[] => "bytes",
        List<object?> => "list",
        _ => value.GetType().Name,
    };

    /// <summary><c>str(value)</c>.</summary>
    /// <param name="value">The value.</param>
    public static string Str(object? value) => value switch
    {
        string text => text,
        PyTimestamp stamp => stamp.Text,
        _ => Repr(value),
    };

    /// <summary><c>repr(value)</c>.</summary>
    /// <param name="value">The value.</param>
    public static string Repr(object? value) => Repr(value, []);

    /// <summary><c>bool(value)</c>.</summary>
    /// <param name="value">The value.</param>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        BigInteger integer => !integer.IsZero,
        double real => real != 0,
        string text => text.Length > 0,
        PyDict dict => dict.Count > 0,
        PySet set => set.Count > 0,
        PyTuple tuple => tuple.Items.Count > 0,
        List<object?> list => list.Count > 0,
        byte[] bytes => bytes.Length > 0,
        _ => true,
    };

    /// <summary><c>value or fallback</c>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="fallback">Returned when the value is falsy.</param>
    public static object? Or(object? value, object? fallback) => Truthy(value) ? value : fallback;

    /// <summary><c>left == right</c>.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool Eq(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return left is double || right is double ? ToDouble(left) == ToDouble(right) : ToInteger(left) == ToInteger(right);
        }

        return (left, right) switch
        {
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (PyDict a, PyDict b) => a.Count == b.Count && a.Items.All(pair => b.TryGetValue(pair.Key, out var other) && Eq(pair.Value, other)),
            (PySet a, PySet b) => a.Count == b.Count && a.Items.All(b.Contains),
            (PyTuple a, PyTuple b) => SequenceEq(a.Items, b.Items),
            (List<object?> a, List<object?> b) => SequenceEq(a, b),
            (PyTimestamp a, PyTimestamp b) => a == b,
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _ => false,
        };
    }

    /// <summary><c>item in container</c>: a key of a dict, a member of a set, an item of a list or tuple, a substring of a str.</summary>
    /// <param name="item">The item.</param>
    /// <param name="container">The container.</param>
    public static bool In(object? item, object? container) => container switch
    {
        PyDict dict => dict.ContainsKey(item),
        PySet set => set.Contains(item),
        string text => item is string needle
            ? text.Contains(needle, StringComparison.Ordinal)
            : throw PyException.TypeError($"'in <string>' requires string as left operand, not {TypeName(item)}"),
        PyTuple tuple => tuple.Items.Any(member => Eq(member, item)),
        List<object?> list => list.Any(member => Eq(member, item)),
        _ => throw PyException.TypeError($"argument of type '{TypeName(container)}' is not iterable"),
    };

    /// <summary><c>for item in value</c>: the keys of a dict, the characters of a str, the items of a list.</summary>
    /// <param name="value">The iterable.</param>
    public static IReadOnlyList<object?> Iterate(object? value) => value switch
    {
        PyDict dict => dict.Keys.ToList(),
        PySet set => set.Items.ToList(),
        string text => text.EnumerateRunes().Select(rune => (object?)rune.ToString()).ToList(),
        PyTuple tuple => tuple.Items,
        List<object?> list => list,
        byte[] bytes => bytes.Select(item => (object?)new BigInteger(item)).ToList(),
        _ => throw PyException.TypeError($"'{TypeName(value)}' object is not iterable"),
    };

    /// <summary><c>value.get(key, fallback)</c>; a value that is not a dict raises <c>AttributeError</c>.</summary>
    /// <param name="value">A dict.</param>
    /// <param name="key">Key.</param>
    /// <param name="fallback">Returned when the key is absent.</param>
    public static object? Get(object? value, object? key, object? fallback = null) =>
        value is PyDict dict ? dict.TryGetValue(key, out var found) ? found : fallback : throw PyException.AttributeError(value, "get");

    /// <summary><c>value.items()</c>; a value that is not a dict raises <c>AttributeError</c>.</summary>
    /// <param name="value">A dict.</param>
    public static IReadOnlyList<KeyValuePair<object?, object?>> Items(object? value) =>
        value is PyDict dict ? dict.Items : throw PyException.AttributeError(value, "items");

    /// <summary><c>container[key]</c> with a str key: <c>KeyError</c> when a dict lacks it, <c>TypeError</c> for other types.</summary>
    /// <param name="container">A dict.</param>
    /// <param name="key">Key.</param>
    public static object? Item(object? container, string key) => container switch
    {
        PyDict dict => dict.TryGetValue(key, out var value) ? value : throw PyException.KeyError(key),
        List<object?> => throw PyException.TypeError("list indices must be integers or slices, not str"),
        string => throw PyException.TypeError("string indices must be integers, not 'str'"),
        _ => throw PyException.TypeError($"'{TypeName(container)}' object is not subscriptable"),
    };

    /// <summary>The script's <c>g(d, path, default)</c>: walks dict keys, or returns the fallback at the first miss.</summary>
    /// <param name="root">Where to start.</param>
    /// <param name="fallback">Returned at the first missing key.</param>
    /// <param name="path">Keys.</param>
    public static object? G(object? root, object? fallback, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current is PyDict dict && dict.TryGetValue(key, out var value))
            {
                current = value;
            }
            else
            {
                return fallback;
            }
        }

        return current;
    }

    /// <summary>A value that must be a str (a contract name or path); anything else raises <c>TypeError</c>.</summary>
    /// <param name="value">The value.</param>
    public static string AsStr(object? value) =>
        value as string ?? throw PyException.TypeError($"must be str, not {TypeName(value)}");

    /// <summary>The script's <c>all_dicts(obj)</c>: every dict, depth first, a dict before its values.</summary>
    /// <param name="root">Where to start.</param>
    public static IReadOnlyList<PyDict> AllDicts(object? root)
    {
        var found = new List<PyDict>();
        Collect(root, 0, found, collectStrings: false);
        return found;
    }

    /// <summary>The script's <c>all_strings(obj)</c>: every str value (not keys), depth first.</summary>
    /// <param name="root">Where to start.</param>
    public static IReadOnlyList<string> AllStrings(object? root)
    {
        var found = new List<object?>();
        Collect(root, 0, found, collectStrings: true);
        return found.Cast<string>().ToList();
    }

    /// <summary><c>sorted(items)</c>: stable, Python's ordering; values that Python cannot compare raise <c>TypeError</c>.</summary>
    /// <param name="items">Items.</param>
    public static List<object?> Sorted(IEnumerable<object?> items) =>
        items.OrderBy(item => item, Comparer<object?>.Create(Compare)).ToList();

    /// <summary><c>text.splitlines()</c>: splits at every line boundary Python knows, no trailing empty line.</summary>
    /// <param name="text">Text.</param>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var position = 0;
        while (position < text.Length)
        {
            var character = text[position];
            if (character is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..position]);
                position += character == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;
                start = position;
            }
            else
            {
                position++;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private static bool IsNumber(object value) => value is bool or BigInteger or double;

    private static double ToDouble(object value) => value switch
    {
        bool flag => flag ? 1 : 0,
        BigInteger integer => (double)integer,
        _ => (double)value,
    };

    private static BigInteger ToInteger(object value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        _ => (BigInteger)value,
    };

    private static bool SequenceEq(IReadOnlyList<object?> left, IReadOnlyList<object?> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => Eq(pair.First, pair.Second));

    private static int CompareStrings(string? left, string? right)
    {
        var a = left ?? string.Empty;
        var b = right ?? string.Empty;
        for (var index = 0; index < a.Length && index < b.Length; index++)
        {
            if (a[index] == b[index])
            {
                continue;
            }

            // A surrogate belongs to a code point above U+FFFF: it sorts after every other character.
            var surrogateA = char.IsSurrogate(a[index]);
            var surrogateB = char.IsSurrogate(b[index]);
            return surrogateA != surrogateB ? (surrogateA ? 1 : -1) : a[index].CompareTo(b[index]);
        }

        return a.Length.CompareTo(b.Length);
    }

    private static int Compare(object? left, object? right)
    {
        if (left is string a && right is string b)
        {
            return CompareStrings(a, b);
        }

        if (left is not null && right is not null && IsNumber(left) && IsNumber(right))
        {
            return left is double || right is double ? ToDouble(left).CompareTo(ToDouble(right)) : ToInteger(left).CompareTo(ToInteger(right));
        }

        if (left is List<object?> x && right is List<object?> y)
        {
            return CompareSequences(x, y);
        }

        if (left is PyTuple p && right is PyTuple q)
        {
            return CompareSequences(p.Items, q.Items);
        }

        throw PyException.TypeError($"'<' not supported between instances of '{TypeName(right)}' and '{TypeName(left)}'");
    }

    private static int CompareSequences(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        for (var index = 0; index < left.Count && index < right.Count; index++)
        {
            if (!Eq(left[index], right[index]))
            {
                return Compare(left[index], right[index]);
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static void Collect(object? node, int depth, System.Collections.IList found, bool collectStrings)
    {
        if (depth > MaxDepth)
        {
            throw new PyException("RecursionError", "maximum recursion depth exceeded");
        }

        switch (node)
        {
            case string text when collectStrings:
                found.Add(text);
                break;
            case PyDict dict:
                if (!collectStrings)
                {
                    found.Add(dict);
                }

                foreach (var pair in dict.Items)
                {
                    Collect(pair.Value, depth + 1, found, collectStrings);
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    Collect(item, depth + 1, found, collectStrings);
                }

                break;
        }
    }

    private static string Repr(object? value, List<object> active)
    {
        switch (value)
        {
            case null:
                return "None";
            case bool flag:
                return flag ? "True" : "False";
            case BigInteger integer:
                return integer.ToString(CultureInfo.InvariantCulture);
            case double real:
                return FloatRepr(real);
            case string text:
                return StringRepr(text);
            case PyTimestamp stamp:
                return stamp.Representation;
            case byte[] bytes:
                return "b" + StringRepr(new string(bytes.Select(item => (char)item).ToArray()));
        }

        if (active.Any(container => ReferenceEquals(container, value)))
        {
            return value is PyDict ? "{...}" : "[...]";
        }

        active.Add(value);
        try
        {
            return value switch
            {
                PyDict dict => "{" + string.Join(", ", dict.Items.Select(pair => Repr(pair.Key, active) + ": " + Repr(pair.Value, active))) + "}",
                PySet set => set.Count == 0 ? "set()" : "{" + string.Join(", ", set.Items.Select(item => Repr(item, active))) + "}",
                PyTuple { Items.Count: 1 } tuple => "(" + Repr(tuple.Items[0], active) + ",)",
                PyTuple tuple => "(" + string.Join(", ", tuple.Items.Select(item => Repr(item, active))) + ")",
                List<object?> list => "[" + string.Join(", ", list.Select(item => Repr(item, active))) + "]",
                _ => value.ToString() ?? string.Empty,
            };
        }
        finally
        {
            active.RemoveAt(active.Count - 1);
        }
    }

    private static string StringRepr(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var rune in text.EnumerateRunes())
        {
            var code = rune.Value;
            if (code == quote || code == '\\')
            {
                builder.Append('\\').Append((char)code);
            }
            else if (code == '\t')
            {
                builder.Append("\\t");
            }
            else if (code == '\n')
            {
                builder.Append("\\n");
            }
            else if (code == '\r')
            {
                builder.Append("\\r");
            }
            else if (code < 0x20 || code == 0x7f)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\x{code:x2}");
            }
            else if (code < 0x7f || IsPrintable(rune))
            {
                builder.Append(rune.ToString());
            }
            else
            {
                var (escape, width) = code < 0x100 ? ('x', 2) : code < 0x10000 ? ('u', 4) : ('U', 8);
                builder.Append('\\').Append(escape).Append(code.ToString("x" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            }
        }

        return builder.Append(quote).ToString();
    }

    private static bool IsPrintable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator => rune.Value == ' ',
        _ => true,
    };

    /// <summary><c>repr(float)</c>: shortest round-trip digits, fixed notation for exponents from -4 to 15.</summary>
    private static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0;
        var marker = text.IndexOf('E', StringComparison.Ordinal);
        if (marker >= 0)
        {
            exponent = int.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..marker];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point >= 0 ? text[..point] : text;
        var digits = whole + (point >= 0 ? text[(point + 1)..] : string.Empty);
        var decimalPoint = whole.Length + exponent;
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        decimalPoint -= leading;
        var scientific = decimalPoint - 1;
        string body;
        if (scientific is >= -4 and < 16)
        {
            body = decimalPoint <= 0
                ? "0." + new string('0', -decimalPoint) + digits
                : decimalPoint >= digits.Length
                    ? digits + new string('0', decimalPoint - digits.Length) + ".0"
                    : digits[..decimalPoint] + "." + digits[decimalPoint..];
        }
        else
        {
            var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            body = mantissa + "e" + (scientific < 0 ? "-" : "+") + Math.Abs(scientific).ToString("00", CultureInfo.InvariantCulture);
        }

        return negative ? "-" + body : body;
    }
}
