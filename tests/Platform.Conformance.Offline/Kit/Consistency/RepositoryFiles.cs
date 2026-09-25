using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>
/// Reads the environment repository as <c>scripts/checks/consistency.sh</c> does: <c>walk()</c> skips <c>.git</c>,
/// <c>.terraform</c>, <c>node_modules</c>, <c>bin</c>, <c>obj</c> and <c>TestResults</c> and returns sorted
/// repository-relative paths with forward slashes; <c>text()</c> decodes strict UTF-8 with universal newlines (a file
/// that does not decode reads as absent); <c>code_text()</c> drops full-line <c>#</c> and <c>//</c> comments.
/// </summary>
internal sealed partial class RepositoryFiles
{
    /// <summary>The extensions <c>walk()</c> keeps by default.</summary>
    public static readonly IReadOnlyList<string> Yaml = [".yaml", ".yml"];

    private static readonly HashSet<string> Pruned = new(StringComparer.Ordinal) { ".git", ".terraform", "node_modules", "bin", "obj", "TestResults" };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly EnumerationOptions Entries = new() { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
    private readonly Dictionary<string, string?> texts = new(StringComparer.Ordinal);

    /// <summary>Creates the reader.</summary>
    /// <param name="root">Repository root.</param>
    public RepositoryFiles(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Absolute repository root.</summary>
    public string Root { get; }

    /// <summary>The script's <c>P(rel)</c>: absolute path of a repository-relative path.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public string PathOf(string relative) => Path.Combine(Root, relative);

    /// <summary>Repository-relative path with forward slashes, as <c>os.path.relpath</c> gives it.</summary>
    /// <param name="path">Absolute path.</param>
    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary><c>os.path.exists</c>: a file or a folder.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public bool Exists(string relative)
    {
        var path = PathOf(relative);
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary><c>os.path.isfile</c>.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public bool IsFile(string relative) => File.Exists(PathOf(relative));

    /// <summary><c>os.path.isdir</c>.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public bool IsDirectory(string relative) => Directory.Exists(PathOf(relative));

    /// <summary>
    /// The script's <c>walk(rel_dir, exts)</c>: every file below a folder whose name ends with one of the extensions
    /// (every file when <paramref name="extensions"/> is <c>null</c>), sorted; symbolic links to folders are not followed.
    /// </summary>
    /// <param name="relativeDirectory">Folder with forward slashes.</param>
    /// <param name="extensions">Extensions, or <c>null</c> for every file.</param>
    public IReadOnlyList<string> Walk(string relativeDirectory, IReadOnlyList<string>? extensions)
    {
        var start = PathOf(relativeDirectory);
        if (!Directory.Exists(start))
        {
            return [];
        }

        var found = new List<string>();
        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", Entries).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo directory)
                {
                    if (!Pruned.Contains(directory.Name) && directory.LinkTarget is null)
                    {
                        pending.Push(directory.FullName);
                    }
                }
                else if (extensions is null || extensions.Any(extension => entry.Name.EndsWith(extension, StringComparison.Ordinal)))
                {
                    found.Add(Relative(entry.FullName));
                }
            }
        }

        found.Sort(Py.StringOrder);
        return found;
    }

    /// <summary><c>sorted(glob.glob(folder/*extension))</c>: files directly in a folder, hidden names excluded.</summary>
    /// <param name="relativeDirectory">Folder with forward slashes.</param>
    /// <param name="extension">Extension, for example <c>.yaml</c>.</param>
    public IReadOnlyList<string> Glob(string relativeDirectory, string extension)
    {
        var folder = PathOf(relativeDirectory);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new DirectoryInfo(folder).EnumerateFiles("*", Entries)
            .Where(file => !file.Name.StartsWith('.') && file.Name.EndsWith(extension, StringComparison.Ordinal))
            .Select(file => Relative(file.FullName))
            .ToList();
        found.Sort(Py.StringOrder);
        return found;
    }

    /// <summary>The script's <c>text(rel)</c>: the content, or <c>null</c> when the file cannot be read or is not UTF-8.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public string? Text(string relative)
    {
        if (texts.TryGetValue(relative, out var cached))
        {
            return cached;
        }

        string? text;
        try
        {
            text = Decode(File.ReadAllBytes(PathOf(relative)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            text = null;
        }

        texts[relative] = text;
        return text;
    }

    /// <summary>The script's <c>code_text(rel)</c>: the lines that are not full-line <c>#</c> or <c>//</c> comments.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public string? CodeText(string relative) =>
        Text(relative) is { } text ? string.Join('\n', Py.SplitLines(text).Where(line => !Comment().IsMatch(line))) : null;

    /// <summary>
    /// A file opened for PyYAML: <c>OSError</c> when it cannot be read, <c>UnicodeDecodeError</c> when it is not UTF-8
    /// (neither is a YAML error, so each propagates as it does in the script).
    /// </summary>
    /// <param name="path">Absolute path.</param>
    public static string ReadForYaml(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PyException(exception is FileNotFoundException or DirectoryNotFoundException ? "FileNotFoundError" : "OSError", exception.Message);
        }

        try
        {
            return Decode(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PyException("UnicodeDecodeError", $"'utf-8' codec can't decode bytes: {exception.Message}");
        }
    }

    /// <summary>Strict UTF-8 with Python's universal newlines (<c>\r\n</c> and <c>\r</c> become <c>\n</c>).</summary>
    /// <param name="bytes">File content.</param>
    public static string Decode(byte[] bytes) =>
        StrictUtf8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// The script's <c>blocks(src, header_re)</c>: for each line matching <paramref name="header"/>, the name (group 1)
    /// and the block up to its closing brace; braces inside strings, after <c>#</c> or <c>//</c>, and in heredocs are
    /// ignored.
    /// </summary>
    /// <param name="source">Source text.</param>
    /// <param name="header">Header pattern, anchored at the start of a line.</param>
    public static IEnumerable<(string Name, string Body)> Blocks(string source, Regex header)
    {
        var lines = Py.SplitLines(source);
        var index = 0;
        while (index < lines.Count)
        {
            var match = header.Match(lines[index]);
            if (!match.Success || match.Index != 0)
            {
                index++;
                continue;
            }

            var depth = 0;
            var body = new List<string>();
            string? heredoc = null;
            var next = index;
            while (next < lines.Count)
            {
                var line = lines[next];
                body.Add(line);
                if (heredoc is not null)
                {
                    if (line.Trim() == heredoc)
                    {
                        heredoc = null;
                    }

                    next++;
                    continue;
                }

                var inString = false;
                var escaped = false;
                for (var position = 0; position < line.Length; position++)
                {
                    var character = line[position];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (character == '\\')
                        {
                            escaped = true;
                        }
                        else if (character == '"')
                        {
                            inString = false;
                        }
                    }
                    else if (character == '"')
                    {
                        inString = true;
                    }
                    else if (character == '#' || line.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
                    {
                        break;
                    }
                    else if (character == '{')
                    {
                        depth++;
                    }
                    else if (character == '}')
                    {
                        depth--;
                    }
                    else if (line.AsSpan(position).StartsWith("<<", StringComparison.Ordinal) && Heredoc().Match(line, position) is { Success: true } opening)
                    {
                        heredoc = opening.Groups[1].Value;
                        break;
                    }
                }

                next++;
                if (depth <= 0 && heredoc is null && body.Any(item => item.Contains('{', StringComparison.Ordinal)))
                {
                    break;
                }
            }

            yield return (match.Groups[1].Value, string.Join('\n', body));
            index = next;
        }
    }

    [GeneratedRegex(@"^\s*(#|//)")]
    private static partial Regex Comment();

    [GeneratedRegex(@"\G<<-?\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Heredoc();
}
