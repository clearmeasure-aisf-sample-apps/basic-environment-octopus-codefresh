using Platform.Onboarding.Cli;

namespace Platform.Onboarding.Repository;

/// <summary>The environment repository: its root and the platform paths of §6.1 and §7.0.</summary>
internal sealed class PlatformRepository
{
    /// <summary>Environment variable that names the root instead of searching for it.</summary>
    public const string RootVariable = "PLATFORM_REPO_ROOT";

    /// <summary>Creates a repository view.</summary>
    /// <param name="root">Absolute path of the repository root.</param>
    public PlatformRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Absolute path of the root.</summary>
    public string Root { get; }

    /// <summary><c>apps/</c>.</summary>
    public string AppsDirectory => Path.Combine(Root, "apps");

    /// <summary><c>apps/schema.json</c>.</summary>
    public string SchemaPath => Path.Combine(AppsDirectory, "schema.json");

    /// <summary>
    /// Finds the root: <paramref name="explicitRoot"/>, else <c>PLATFORM_REPO_ROOT</c>, else the nearest folder at or above
    /// <paramref name="startDirectory"/> that holds <c>apps/schema.json</c>.
    /// </summary>
    /// <param name="explicitRoot">The <c>--root</c> option, or <c>null</c>.</param>
    /// <param name="startDirectory">Where the search starts, usually the current directory.</param>
    /// <param name="environment">Environment variables.</param>
    /// <returns>The repository, or <c>null</c> when no root is found.</returns>
    public static PlatformRepository? Locate(string? explicitRoot, string startDirectory, IEnvironment environment)
    {
        var configured = explicitRoot ?? environment.Get(RootVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Directory.Exists(configured) ? new PlatformRepository(configured) : null;
        }

        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "apps", "schema.json")))
            {
                return new PlatformRepository(directory.FullName);
            }
        }

        return null;
    }

    /// <summary>Absolute path of a repository-relative path written with forward slashes.</summary>
    /// <param name="relative">For example <c>gitops/apps/demo</c>.</param>
    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Repository-relative path with forward slashes.</summary>
    /// <param name="absolute">An absolute path under the root.</param>
    public string Relative(string absolute) => Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary><c>apps/&lt;app&gt;.yaml</c>.</summary>
    /// <param name="app">App slug.</param>
    public static string DescriptorPath(string app) => $"apps/{app}.yaml";

    /// <summary>The app-scoped path roots of <paramref name="app"/> (contracts <c>descriptors.appScopedPaths</c>).</summary>
    /// <param name="app">App slug.</param>
    public static IReadOnlyList<string> AppScopedRoots(string app) =>
        [$"codefresh/apps/{app}/", $".octopus/apps/{app}/", $"gitops/apps/{app}/", $"containers/apps/{app}/"];

    /// <summary>Extra paths owned by the conformance fixture.</summary>
    public static IReadOnlyList<string> FixtureRoots { get; } = ["fixtures/sandbox-app/"];

    /// <summary>
    /// The app a repository-relative path belongs to, or <c>null</c> for a platform path. <c>apps/schema.json</c> is a
    /// platform path; <c>apps/&lt;x&gt;.yaml</c> belongs to <c>x</c>.
    /// </summary>
    /// <param name="relative">Path with forward slashes.</param>
    public static string? AppOf(string relative)
    {
        var path = relative.TrimStart('/');
        if (path.StartsWith("apps/", StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal) && path.Count(c => c == '/') == 1)
        {
            return path["apps/".Length..^".yaml".Length];
        }

        foreach (var prefix in new[] { "codefresh/apps/", ".octopus/apps/", "gitops/apps/", "containers/apps/" })
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/');
                return slash > 0 ? rest[..slash] : null;
            }
        }

        return FixtureRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)) ? Naming.PlatformNames.FixtureApp : null;
    }
}
