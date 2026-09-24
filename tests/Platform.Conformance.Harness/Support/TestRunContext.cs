using System.Security.Cryptography;
using System.Text;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Harness.Support;

/// <summary>
/// Identity of one conformance run: an ID used in the names of resources the run creates, the start time,
/// and a folder for artifacts such as task logs.
/// </summary>
public sealed class TestRunContext
{
    private const int MaxResourceNameLength = 63;
    private static readonly Lazy<TestRunContext> CurrentRun = new(() => Create(
        ProcessEnvironmentVariables.Instance,
        SystemClock.Instance,
        RepositoryRoot.TryFind(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance)));

    /// <summary>Creates a context.</summary>
    /// <param name="runId">Run identifier; lowercase letters, digits and hyphens.</param>
    /// <param name="startedAt">Start of the run.</param>
    /// <param name="artifactsDirectory">Folder for artifacts; created when the first artifact is written.</param>
    public TestRunContext(string runId, DateTimeOffset startedAt, string artifactsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        RunId = runId;
        StartedAt = startedAt;
        ArtifactsDirectory = Path.GetFullPath(artifactsDirectory);
    }

    /// <summary>The context of the current process, created on first use from the environment.</summary>
    public static TestRunContext Current => CurrentRun.Value;

    /// <summary>Run identifier: <c>PLATFORM_RUN_ID</c>, or <c>yyyyMMdd-HHmmss-xxxx</c> generated at start.</summary>
    public string RunId { get; }

    /// <summary>When the run started (UTC).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Folder for artifacts: <c>PLATFORM_ARTIFACTS_DIR</c>, or <c>tests/TestResults/artifacts/&lt;run id&gt;</c>.</summary>
    public string ArtifactsDirectory { get; }

    /// <summary>Creates a context from environment variables.</summary>
    /// <param name="environment">Source of <c>PLATFORM_RUN_ID</c> and <c>PLATFORM_ARTIFACTS_DIR</c>.</param>
    /// <param name="clock">Supplies the start time.</param>
    /// <param name="repositoryRoot">Repository root for the default artifacts folder; the temp folder is used when <c>null</c>.</param>
    public static TestRunContext Create(IEnvironmentVariables environment, IClock clock, string? repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        var startedAt = clock.UtcNow;
        var configuredId = environment.Get(EnvironmentVariableNames.RunId);
        var runId = string.IsNullOrWhiteSpace(configuredId)
            ? $"{startedAt:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}"
            : ToDnsLabel(configuredId);
        var configuredArtifacts = environment.Get(EnvironmentVariableNames.ArtifactsDirectory);
        var artifacts = !string.IsNullOrWhiteSpace(configuredArtifacts)
            ? configuredArtifacts.Trim()
            : repositoryRoot is not null
                ? Path.Combine(repositoryRoot, "tests", "TestResults", "artifacts", runId)
                : Path.Combine(Path.GetTempPath(), "platform-conformance", runId);
        return new TestRunContext(runId, startedAt, artifacts);
    }

    /// <summary>
    /// A name for a resource this run creates, valid as a Kubernetes name and an Azure tag value:
    /// <c>conf-&lt;run id&gt;-&lt;purpose&gt;</c>, lowercase, at most 63 characters.
    /// </summary>
    /// <param name="purpose">Short purpose such as <c>policy-probe</c>.</param>
    public string ResourceName(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return ToDnsLabel($"conf-{RunId}-{purpose}");
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="fileName"/> in the artifacts folder.</summary>
    /// <param name="fileName">A plain file name without folders, for example <c>env-wake-task.log</c>.</param>
    /// <param name="content">Text to write (UTF-8).</param>
    /// <returns>The full path of the written file.</returns>
    public string WriteArtifact(string fileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{fileName}' must be a plain file name.", nameof(fileName));
        }

        Directory.CreateDirectory(ArtifactsDirectory);
        var path = Path.Combine(ArtifactsDirectory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string ToDnsLabel(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        var label = builder.ToString();
        if (label.Length > MaxResourceNameLength)
        {
            label = label[..MaxResourceNameLength];
        }

        label = label.Trim('-');
        return label.Length == 0 ? "run" : label;
    }
}
