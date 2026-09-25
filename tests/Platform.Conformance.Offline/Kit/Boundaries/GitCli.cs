using System.Diagnostics;
using System.Text;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>Exit code, standard output and standard error of one git command.</summary>
/// <param name="ExitCode">Exit code.</param>
/// <param name="Output">Standard output, unchanged.</param>
/// <param name="Error">Standard error.</param>
internal sealed record GitResult(int ExitCode, string Output, string Error)
{
    /// <summary>The output without trailing line feeds, as a shell command substitution returns it.</summary>
    public string Trimmed => Output.TrimEnd('\n');

    /// <summary>The non-empty lines of the output.</summary>
    public IReadOnlyList<string> Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The git command line, the only way the boundary rules and the bot-path audit read Git.</summary>
internal static class GitCli
{
    /// <summary>The git executable on PATH, or <c>null</c>.</summary>
    public static string? Find() => KitToolbox.Find("git");

    /// <summary>Runs git in <paramref name="workingDirectory"/> and waits at most two minutes.</summary>
    /// <param name="git">The git executable.</param>
    /// <param name="workingDirectory">Folder to run in (the script's <c>git -C</c>).</param>
    /// <param name="arguments">Arguments.</param>
    /// <exception cref="TimeoutException">git did not finish in time.</exception>
    public static GitResult Run(string git, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"cannot start {git}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', arguments)} did not finish within two minutes");
        }

        return new GitResult(process.ExitCode, output.Result, error.Result);
    }
}
