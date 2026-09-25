using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>
/// kustomize for C09: found as the script finds it (<c>command -v "${KUSTOMIZE:-kustomize}"</c>: the <c>KUSTOMIZE</c>
/// variable, a path or a name, else <c>kustomize</c> on the PATH), and run as the script's <c>render_dir</c> runs it.
/// </summary>
internal static class Kustomize
{
    /// <summary>Longest <c>kustomize build</c>, as in the script.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>The kustomize executable, or <c>null</c> when it is not installed.</summary>
    public static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("KUSTOMIZE");
        var name = string.IsNullOrWhiteSpace(configured) ? "kustomize" : configured.Trim();
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        string[] candidates = OperatingSystem.IsWindows() ? [name + ".exe", name] : [name];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => candidates.Select(candidate => Path.Combine(directory, candidate)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The script's <c>render_dir</c>: <c>kustomize build &lt;directory&gt;</c>, parsed as PyYAML parses it. Returns the
    /// rendered mappings, or the error: the last line of kustomize's standard error, or why the output does not parse.
    /// </summary>
    /// <param name="kustomize">kustomize executable.</param>
    /// <param name="directory">Absolute path of the overlay.</param>
    public static (IReadOnlyList<PyDict>? Rendered, string? Error) Build(string kustomize, string directory)
    {
        var start = new ProcessStartInfo(kustomize)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(directory);
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception exception)
        {
            return (null, exception.Message);
        }

        if (process is null)
        {
            return (null, $"{kustomize} did not start");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return (null, $"Command '{kustomize} build {directory}' timed out after {Timeout.TotalSeconds:0} seconds");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var lines = Py.SplitLines(error.GetAwaiter().GetResult().Trim());
                return (null, lines.Count > 0 ? lines[^1] : "kustomize failed");
            }

            var text = output.GetAwaiter().GetResult().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            try
            {
                return (PyYaml.LoadAll(text).OfType<PyDict>().ToList(), null);
            }
            catch (YamlLoadException exception)
            {
                return (null, "rendered YAML does not parse: " + exception.Message);
            }
        }
    }
}
