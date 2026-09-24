using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>A line that matched a pattern.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Text">The whole line, without its line feed.</param>
internal sealed record LineHit(string Path, int Line, string Text)
{
    /// <summary>The hit as a finding of a rule.</summary>
    /// <param name="ruleId">Rule ID.</param>
    public BoundaryFinding Finding(string ruleId) => new(ruleId, Path, Line, Text);
}

/// <summary>A line that matched a pattern, with the OCL step or YAML key chain around it.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Context">The enclosing step slug, the key chain (for example <c>/steps/wake_nonprod/commands</c>) or <c>-</c>.</param>
/// <param name="Text">The whole line.</param>
internal sealed record ContextHit(string Path, int Line, string Context, string Text)
{
    /// <summary>The hit as a finding, spelled <c>path:line:context:text</c> like the script.</summary>
    /// <param name="ruleId">Rule ID.</param>
    public BoundaryFinding Finding(string ruleId) => new(ruleId, Path, Line, $"{Context}:{Text}");
}

/// <summary>
/// The files of an environment-repository tree as <c>scripts/checks/tool-boundaries.sh</c> sees them: the folders
/// <c>.git</c>, <c>.terraform</c>, <c>node_modules</c>, <c>bin</c> and <c>obj</c> are never entered, Markdown files are never
/// read, symbolic links inside folders are not followed, and a file holding a NUL byte is binary and never matches
/// (<c>grep -I</c>). Lines split at line feeds; text is decoded as UTF-8. Contents are read once and cached.
/// </summary>
internal sealed class BoundaryTree
{
    private static readonly HashSet<string> PrunedNames = new(StringComparer.Ordinal) { ".git", ".terraform", "node_modules", "bin", "obj" };
    private static readonly Regex GrepComment = PosixPatterns.Ere("^[[:space:]]*(#|//)");
    private static readonly Regex ContextComment = PosixPatterns.Ere("^[ \t]*(#|//)");
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>?> lines = new(StringComparer.Ordinal);

    /// <summary>Reads the tree under <paramref name="root"/>.</summary>
    /// <param name="root">Repository root.</param>
    public BoundaryTree(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Absolute path of the repository root.</summary>
    public string Root { get; }

    /// <summary>Absolute path of a repository-relative path.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The specs that exist, as files or folders, in the given order (the script's <c>targets</c>).</summary>
    /// <param name="specs">Repository-relative paths.</param>
    public IReadOnlyList<string> Targets(params string[] specs) =>
        specs.Where(spec => File.Exists(FullPath(spec)) || Directory.Exists(FullPath(spec))).ToArray();

    /// <summary>
    /// The regular files under the specs, sorted, without Markdown, VCS folders, build output or Terraform caches (the
    /// script's <c>files_in</c>). A spec that is a file is listed as it is.
    /// </summary>
    /// <param name="specs">Repository-relative files or folders; absent ones are ignored.</param>
    public IReadOnlyList<string> FilesIn(params string[] specs)
    {
        var files = new List<string>();
        foreach (var spec in Targets(specs))
        {
            if (File.Exists(FullPath(spec)))
            {
                files.Add(spec);
            }
            else if (!PrunedNames.Contains(Path.GetFileName(spec)))
            {
                Walk(FullPath(spec), files, pruneFiles: true);
            }
        }

        return files.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The lines of a file, or <c>null</c> when it is binary. A final line feed ends the last line and adds none.
    /// </summary>
    /// <param name="relative">Repository-relative path.</param>
    public IReadOnlyList<string>? Lines(string relative) =>
        lines.GetOrAdd(relative, path =>
        {
            var bytes = File.ReadAllBytes(FullPath(path));
            if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            {
                return null;
            }

            if (bytes.Length == 0)
            {
                return [];
            }

            var text = Encoding.UTF8.GetString(bytes);
            var split = text.Split('\n');
            return text.EndsWith('\n') ? split[..^1] : split;
        });

    /// <summary>
    /// Every line matching <paramref name="pattern"/> in the specs, recursively (the script's <c>search</c>:
    /// <c>grep -rnIE</c> with its excluded folders and <c>--exclude='*.md'</c>).
    /// </summary>
    /// <param name="pattern">Compiled pattern, see <see cref="PosixPatterns.Ere"/>.</param>
    /// <param name="specs">Repository-relative files or folders that exist.</param>
    /// <param name="pruneFolders"><c>false</c> to also enter the VCS, build and cache folders (a bare <c>grep -r</c>).</param>
    public IEnumerable<LineHit> Search(Regex pattern, IEnumerable<string> specs, bool pruneFolders = true)
    {
        foreach (var file in SearchFiles(specs, pruneFolders))
        {
            if (Lines(file) is not { } text)
            {
                continue;
            }

            for (var index = 0; index < text.Count; index++)
            {
                if (pattern.IsMatch(text[index]))
                {
                    yield return new LineHit(file, index + 1, text[index]);
                }
            }
        }
    }

    /// <summary>
    /// Every line matching <paramref name="pattern"/> in the files, with its context, leaving out lines whose first
    /// non-blank characters are <c>#</c> or <c>//</c> (the script's <c>hits_in_context</c>).
    /// </summary>
    /// <param name="pattern">Compiled pattern.</param>
    /// <param name="files">Repository-relative files, usually from <see cref="FilesIn"/>.</param>
    public IEnumerable<ContextHit> SearchInContext(Regex pattern, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (Lines(file) is not { } text)
            {
                continue;
            }

            IReadOnlyList<string>? contexts = null;
            for (var index = 0; index < text.Count; index++)
            {
                if (!pattern.IsMatch(text[index]) || ContextComment.IsMatch(text[index]))
                {
                    continue;
                }

                contexts ??= LineContext.Of(file, text);
                yield return new ContextHit(file, index + 1, contexts[index], text[index]);
            }
        }
    }

    /// <summary>
    /// The hits whose line is not a comment: its first non-blank characters are not <c>#</c> or <c>//</c> (the script's
    /// <c>strip_comments</c>).
    /// </summary>
    /// <param name="hits">Hits of <see cref="Search"/>.</param>
    public static IEnumerable<LineHit> WithoutComments(IEnumerable<LineHit> hits) => hits.Where(hit => !GrepComment.IsMatch(hit.Text));

    private IEnumerable<string> SearchFiles(IEnumerable<string> specs, bool pruneFolders)
    {
        var files = new List<string>();
        foreach (var spec in specs)
        {
            var full = FullPath(spec);
            if (File.Exists(full))
            {
                if (!spec.EndsWith(".md", StringComparison.Ordinal))
                {
                    files.Add(spec);
                }
            }
            else if (Directory.Exists(full) && !(pruneFolders && PrunedNames.Contains(Path.GetFileName(spec))))
            {
                Walk(full, files, pruneFiles: false, pruneFolders: pruneFolders);
            }
        }

        return files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    private void Walk(string directory, List<string> files, bool pruneFiles, bool pruneFolders = true, bool skipMarkdown = true)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is DirectoryInfo folder)
            {
                if (!(pruneFolders && PrunedNames.Contains(folder.Name)))
                {
                    Walk(folder.FullName, files, pruneFiles, pruneFolders, skipMarkdown);
                }
            }
            else if (!(skipMarkdown && entry.Name.EndsWith(".md", StringComparison.Ordinal)) && !(pruneFiles && PrunedNames.Contains(entry.Name)))
            {
                files.Add(Path.GetRelativePath(Root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
    }
}
