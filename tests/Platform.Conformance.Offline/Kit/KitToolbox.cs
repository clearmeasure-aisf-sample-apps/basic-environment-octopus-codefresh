using System.Diagnostics;
using System.Text;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Offline.Kit;

/// <summary>Exit code and output of a process the Kit tests ran.</summary>
/// <param name="ExitCode">Exit code.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    /// <summary>Output and error together, for assertion messages.</summary>
    public string Transcript => $"exit {ExitCode}{Environment.NewLine}{Output}{Environment.NewLine}{Error}".Trim();
}

/// <summary>
/// Runs the kit's command-line pieces for the Kit capability tests: the onboarding tool (built once per test run into
/// a temporary folder), the bash lint scripts and gitleaks. A missing tool makes a test Inconclusive locally and fails
/// it when <c>CI=true</c>, as <c>scripts/checks/validate-all.sh</c> does.
/// </summary>
internal static class KitToolbox
{
    private static readonly Lazy<string> OnboardingDll = new(BuildOnboardingTool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The environment repository root (the folder that holds tests/Platform.Conformance.sln).</summary>
    public static string RepositoryRoot => global::Platform.Conformance.Harness.Support.RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance);

    /// <summary>The Kit test data folder in the source tree.</summary>
    public static string TestData => Path.Combine(RepositoryRoot, "tests", "Platform.Conformance.Offline", "Kit", "TestData");

    /// <summary><c>true</c> when <c>CI</c> is set to a true value.</summary>
    public static bool IsCi => Environment.GetEnvironmentVariable("CI") is "true" or "TRUE" or "True" or "1" or "yes";

    /// <summary>The dotnet host of this test run.</summary>
    public static string DotnetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    /// <summary>Finds an executable: the override variable when set, else a PATH lookup.</summary>
    /// <param name="name">Executable name, for example <c>gitleaks</c>.</param>
    /// <param name="overrideVariable">Variable naming its path, for example <c>GITLEAKS</c>.</param>
    public static string? Find(string name, string? overrideVariable = null)
    {
        if (overrideVariable is not null && Environment.GetEnvironmentVariable(overrideVariable) is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Returns the tool path, or ends the test: Inconclusive locally, failed when <c>CI=true</c>.</summary>
    /// <param name="name">Executable name.</param>
    /// <param name="overrideVariable">Variable naming its path.</param>
    public static string Require(string name, string? overrideVariable = null)
    {
        var path = Find(name, overrideVariable);
        if (path is not null)
        {
            return path;
        }

        var message = $"{name} not found on PATH{(overrideVariable is null ? string.Empty : $" or in {overrideVariable}")}";
        if (IsCi)
        {
            Assert.Fail($"{message} (CI=true: env-checks must provide it)");
        }

        Assert.Inconclusive($"{message}; the check runs where the tool is installed (env-checks)");
        return string.Empty;
    }

    /// <summary>Runs a process to completion and captures its output.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="timeout">Longest run; the process is killed after it.</param>
    public static ProcessResult Run(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // A child build must not inherit the test host's MSBuild state.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment.Remove("PLATFORM_REPO_ROOT");
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (error) { error.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{Path.GetFileName(fileName)} {string.Join(' ', start.ArgumentList)} did not finish in time");
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs <c>bash &lt;script&gt; &lt;args&gt;</c> from the repository root.</summary>
    /// <param name="script">Repository-relative script path.</param>
    /// <param name="arguments">Script arguments.</param>
    public static ProcessResult Bash(string script, params string[] arguments) =>
        Run(Require("bash"), [Path.Combine(RepositoryRoot, script), .. arguments], RepositoryRoot);

    /// <summary>Runs the onboarding tool (built once per test run from tools/Platform.Onboarding).</summary>
    /// <param name="workingDirectory">Working directory; pass <c>--root</c> to name the repository.</param>
    /// <param name="arguments">Tool arguments.</param>
    public static ProcessResult Onboarding(string workingDirectory, params string[] arguments) =>
        Run(DotnetHost, [OnboardingDll.Value, .. arguments], workingDirectory, TimeSpan.FromMinutes(2));

    private static string BuildOnboardingTool()
    {
        var project = Path.Combine(RepositoryRoot, "tools", "Platform.Onboarding", "Platform.Onboarding.csproj");
        if (!File.Exists(project))
        {
            Assert.Fail($"{project} does not exist");
        }

        var output = Path.Combine(Path.GetTempPath(), "platform-kit-tests", $"onboarding-{Guid.NewGuid():N}");
        var result = Run(DotnetHost, ["build", project, "-c", "Release", "-o", output, "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false"], RepositoryRoot, TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
        {
            Assert.Fail($"building tools/Platform.Onboarding failed:{Environment.NewLine}{result.Transcript}");
        }

        return Path.Combine(output, "Platform.Onboarding.dll");
    }
}

/// <summary>
/// A throw-away copy of the parts of the environment repository that the kit reads: the descriptor schema, the
/// starters of each role (from the repository, or the Kit fixture starters when a role's templates folder is absent)
/// and the platform GitOps components. It never contains the committed descriptors, so it starts with no app.
/// </summary>
internal sealed class KitWorkspace : IDisposable
{
    private KitWorkspace(string root, IReadOnlyList<string> fixtureRoles)
    {
        Root = root;
        FixtureRoles = fixtureRoles;
    }

    /// <summary>Root of the copy.</summary>
    public string Root { get; }

    /// <summary>Roles whose starters came from the Kit fixtures because the repository had none.</summary>
    public IReadOnlyList<string> FixtureRoles { get; }

    /// <summary>Creates a workspace.</summary>
    /// <param name="withStarters">Copy the starters and the platform GitOps components.</param>
    public static KitWorkspace Create(bool withStarters = false)
    {
        var repository = KitToolbox.RepositoryRoot;
        var root = Path.Combine(Path.GetTempPath(), "platform-kit-tests", $"workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "apps"));
        File.Copy(Path.Combine(repository, "apps", "schema.json"), Path.Combine(root, "apps", "schema.json"));
        var fixtureRoles = new List<string>();
        if (withStarters)
        {
            foreach (var (role, folder) in new[] { ("codefresh", "codefresh/templates"), ("octopus", "octopus/templates"), ("gitops", "gitops/templates") })
            {
                var source = Path.Combine(repository, folder);
                if (!Directory.Exists(source) || !Directory.EnumerateDirectories(source).Any())
                {
                    source = Path.Combine(KitToolbox.TestData, "starters", role);
                    fixtureRoles.Add(role);
                }

                CopyDirectory(source, Path.Combine(root, folder));
            }

            var components = Path.Combine(repository, "gitops", "platform", "components");
            if (Directory.Exists(components))
            {
                CopyDirectory(components, Path.Combine(root, "gitops", "platform", "components"));
            }
        }

        return new KitWorkspace(root, fixtureRoles);
    }

    /// <summary>Starter names of a role in the workspace.</summary>
    /// <param name="folder">For example <c>gitops/templates</c>.</param>
    public IReadOnlyList<string> Starters(string folder) =>
        Directory.Exists(Path.Combine(Root, folder))
            ? Directory.EnumerateDirectories(Path.Combine(Root, folder)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>Absolute path of a workspace-relative path.</summary>
    /// <param name="relative">Path with forward slashes.</param>
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Runs the onboarding tool against the workspace.</summary>
    /// <param name="arguments">Tool arguments; <c>--root</c> is appended.</param>
    public ProcessResult Onboarding(params string[] arguments) => KitToolbox.Onboarding(Root, [.. arguments, "--root", Root]);

    /// <summary>Deletes the workspace.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
