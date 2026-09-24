using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// Finds the repository root: the nearest ancestor folder that holds <c>tests/Platform.Conformance.sln</c>
/// (or <c>.slnx</c>). <c>PLATFORM_REPO_ROOT</c> overrides the search.
/// </summary>
public static class RepositoryRoot
{
    /// <summary>Returns the repository root above <paramref name="startDirectory"/>, or <c>null</c> when there is none.</summary>
    /// <param name="startDirectory">Folder to start from, usually the test assembly folder.</param>
    /// <param name="environment">Environment variables; when <c>PLATFORM_REPO_ROOT</c> is set it is returned as a full path.</param>
    public static string? TryFind(string startDirectory, IEnvironmentVariables? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var configured = environment?.Get(EnvironmentVariableNames.RepositoryRoot);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (IsRepositoryRoot(directory.FullName))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>Returns the repository root above <paramref name="startDirectory"/>.</summary>
    /// <param name="startDirectory">Folder to start from, usually the test assembly folder.</param>
    /// <param name="environment">Environment variables; <c>PLATFORM_REPO_ROOT</c> overrides the search.</param>
    /// <exception cref="DirectoryNotFoundException">No ancestor folder holds <c>tests/Platform.Conformance.sln</c>.</exception>
    public static string Find(string startDirectory, IEnvironmentVariables? environment = null) =>
        TryFind(startDirectory, environment)
        ?? throw new DirectoryNotFoundException(
            $"No repository root above {startDirectory}: expected a folder holding tests/Platform.Conformance.sln. Set {EnvironmentVariableNames.RepositoryRoot}.");

    private static bool IsRepositoryRoot(string directory)
    {
        var tests = Path.Combine(directory, "tests");
        return File.Exists(Path.Combine(tests, "Platform.Conformance.sln")) || File.Exists(Path.Combine(tests, "Platform.Conformance.slnx"));
    }
}
