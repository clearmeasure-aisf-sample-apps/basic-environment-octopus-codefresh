using Platform.Conformance.Harness.Catalogue;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Report;

/// <summary>The commands of the report tool. Exit codes: 0 success, 1 failed tests, 2 invalid input.</summary>
internal static class ReportCommands
{
    /// <summary>Exit code for success.</summary>
    public const int Success = 0;

    /// <summary>Exit code when the TRX results hold a failed test.</summary>
    public const int TestsFailed = 1;

    /// <summary>Exit code for invalid arguments or unreadable input.</summary>
    public const int InvalidInput = 2;

    /// <summary>Usage text.</summary>
    public const string Usage = """
        Usage: Platform.Conformance.Report <command> [options]

          map               Reflect over test assemblies and write the capability map JSON.
                              --assembly <dll>        test assembly (repeat for each)
                              --out <file>            output file (default capability-map.json)

          report            Summarise TRX results per capability; writes summary.md and summary.json.
                              --trx <file|folder>     TRX file, or a folder searched for *.trx (repeat)
                              --map <file>            capability map from 'map', or
                              --assembly <dll>        test assembly to reflect over (repeat)
                              --repo-root <folder>    repository root (default: found from the current folder)
                              --catalogue <file>      a single catalogue file instead of the merged catalogue/
                              --out <folder>          output folder (default: the folder of the first TRX file)
                              --title <text>          report title
                            Exits 1 when any test failed.

          render-catalogue  Render the merged catalogue as Markdown (a table per owner).
                              --repo-root <folder>    repository root (default: found from the current folder)
                              --catalogue <file>      a single catalogue file instead of the merged catalogue/
                              --out <file>            output file (default <repo-root>/docs/capabilities.md)
        """;

    /// <summary>Options accepted by each command.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Options { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["map"] = new HashSet<string>(StringComparer.Ordinal) { "assembly", "out" },
        ["report"] = new HashSet<string>(StringComparer.Ordinal) { "trx", "map", "assembly", "repo-root", "catalogue", "out", "title" },
        ["render-catalogue"] = new HashSet<string>(StringComparer.Ordinal) { "repo-root", "catalogue", "out" },
    };

    /// <summary>Runs the tool.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="environment">Environment variables (for the repository root).</param>
    /// <param name="currentDirectory">Folder relative paths and the repository search start from.</param>
    /// <param name="clock">Clock for the report timestamp.</param>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, IEnvironmentVariables environment, string currentDirectory, IClock clock)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Count == 0 ? InvalidInput : Success;
        }

        try
        {
            var command = CommandLine.Parse(args, Options);
            return command.Command switch
            {
                "map" => Map(command, output, currentDirectory),
                "report" => Report(command, output, environment, currentDirectory, clock),
                _ => RenderCatalogue(command, output, environment, currentDirectory),
            };
        }
        catch (CommandLineException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine();
            error.WriteLine(Usage);
            return InvalidInput;
        }
        catch (Exception ex) when (ex is CatalogueValidationException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            error.WriteLine(ex.Message);
            return InvalidInput;
        }
    }

    private static int Map(CommandLine command, TextWriter output, string currentDirectory)
    {
        var assemblies = command.All("assembly").Select(path => Path.GetFullPath(path, currentDirectory)).ToArray();
        if (assemblies.Length == 0)
        {
            throw new CommandLineException("map needs at least one --assembly.");
        }

        var map = CapabilityMap.FromAssemblyFiles(assemblies);
        var target = Path.GetFullPath(command.Single("out") ?? "capability-map.json", currentDirectory);
        WriteFile(target, map.ToJson());
        output.WriteLine($"Wrote {target}: {map.Tests.Count} tests from {string.Join(", ", map.Assemblies)}.");
        return Success;
    }

    private static int Report(CommandLine command, TextWriter output, IEnvironmentVariables environment, string currentDirectory, IClock clock)
    {
        var trxFiles = command.All("trx").SelectMany(path => ExpandTrx(Path.GetFullPath(path, currentDirectory))).Distinct(StringComparer.Ordinal).ToArray();
        if (trxFiles.Length == 0)
        {
            throw new CommandLineException("report needs at least one --trx file or folder that holds *.trx files.");
        }

        var mapFile = command.Single("map");
        var assemblies = command.All("assembly");
        if ((mapFile is null) == (assemblies.Count == 0))
        {
            throw new CommandLineException("report needs either --map or --assembly (not both).");
        }

        var tests = mapFile is not null
            ? CapabilityMap.FromJson(File.ReadAllText(Path.GetFullPath(mapFile, currentDirectory)), mapFile).ToDiscoveredTests()
            : CapabilityMap.FromAssemblyFiles(assemblies.Select(path => Path.GetFullPath(path, currentDirectory))).ToDiscoveredTests();
        var catalogue = LoadCatalogue(command, environment, currentDirectory, out _);
        var runs = trxFiles.Select(TrxReader.Load).ToArray();
        var report = ConformanceReportBuilder.Build(command.Single("title") ?? "Platform conformance summary", runs, catalogue, tests, clock.UtcNow);
        var outputFolder = Path.GetFullPath(command.Single("out") ?? Path.GetDirectoryName(trxFiles[0])!, currentDirectory);
        var markdownPath = Path.Combine(outputFolder, "summary.md");
        var jsonPath = Path.Combine(outputFolder, "summary.json");
        WriteFile(markdownPath, MarkdownSummaryWriter.Write(report));
        WriteFile(jsonPath, SummaryJsonWriter.Write(report));
        output.WriteLine($"{(report.HasFailures ? "FAILED" : "PASSED")}: {report.ResultCount} results; {report.Failures.Count} failed, {report.Inconclusive.Count} inconclusive; capabilities {report.CountOf(CapabilityStatus.Pass)} pass, {report.CountOf(CapabilityStatus.Fail)} fail, {report.CountOf(CapabilityStatus.Inconclusive)} inconclusive, {report.CountOf(CapabilityStatus.NotRun)} not run.");
        output.WriteLine($"Wrote {markdownPath}");
        output.WriteLine($"Wrote {jsonPath}");
        return report.HasFailures ? TestsFailed : Success;
    }

    private static int RenderCatalogue(CommandLine command, TextWriter output, IEnvironmentVariables environment, string currentDirectory)
    {
        var catalogue = LoadCatalogue(command, environment, currentDirectory, out var repositoryRoot);
        var target = command.Single("out") is { } explicitTarget
            ? Path.GetFullPath(explicitTarget, currentDirectory)
            : repositoryRoot is not null
                ? Path.Combine(repositoryRoot, "docs", "capabilities.md")
                : throw new CommandLineException("render-catalogue with --catalogue needs --out.");
        WriteFile(target, CatalogueMarkdownRenderer.Render(catalogue));
        output.WriteLine($"Wrote {target}: {catalogue.Capabilities.Count} capabilities from {catalogue.Sources.Count} file(s).");
        return Success;
    }

    private static CapabilityCatalogue LoadCatalogue(CommandLine command, IEnvironmentVariables environment, string currentDirectory, out string? repositoryRoot)
    {
        var file = command.Single("catalogue");
        var root = command.Single("repo-root");
        if (file is not null && root is not null)
        {
            throw new CommandLineException("Use --catalogue or --repo-root, not both.");
        }

        if (file is not null)
        {
            repositoryRoot = null;
            return CapabilityCatalogue.Load(Path.GetFullPath(file, currentDirectory));
        }

        repositoryRoot = root is not null ? Path.GetFullPath(root, currentDirectory) : RepositoryRoot.Find(currentDirectory, environment);
        return CapabilityCatalogue.LoadDirectory(repositoryRoot);
    }

    private static IEnumerable<string> ExpandTrx(string path)
    {
        if (Directory.Exists(path))
        {
            return Directory.EnumerateFiles(path, "*.trx", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        }

        return File.Exists(path) ? [path] : throw new FileNotFoundException($"TRX file or folder {path} does not exist.", path);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
