using System.Diagnostics;
using Platform.Onboarding.Output;
using Platform.Onboarding.Repository;

namespace Platform.Onboarding.Checks;

/// <summary>One changed path, as <c>git diff --name-status</c> prints it.</summary>
/// <param name="Status">A (added), M (modified), D (deleted) or R (renamed; <paramref name="Path"/> is the new path).</param>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="OldPath">The old path of a rename.</param>
internal sealed record ChangedPath(char Status, string Path, string? OldPath = null);

/// <summary>
/// The blast-radius rule (CAP-KIT-004): a change that adds or removes a descriptor is an onboarding or retirement change,
/// and it may touch only the app-scoped paths of the apps it adds or removes (contracts
/// <c>descriptors.appScopedPaths</c>). Nothing platform-wide changes when an app is added (directive §5).
/// </summary>
internal static class BlastRadius
{
    /// <summary>Parses <c>git diff --name-status</c> output; a line without a status is a modified path.</summary>
    /// <param name="text">One change per line: <c>A\tpath</c>, <c>R100\told\tnew</c> or a bare path.</param>
    public static IReadOnlyList<ChangedPath> Parse(string text)
    {
        var changes = new List<ChangedPath>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length == 1)
            {
                changes.Add(new ChangedPath('M', Normalize(fields[0])));
            }
            else if (fields[0].Length > 0 && fields[0][0] is 'R' or 'C' && fields.Length >= 3)
            {
                changes.Add(new ChangedPath(fields[0][0], Normalize(fields[2]), Normalize(fields[1])));
            }
            else
            {
                changes.Add(new ChangedPath(char.ToUpperInvariant(fields[0].Trim()[0]), Normalize(fields[1])));
            }
        }

        return changes;
    }

    /// <summary>Runs <c>git diff --name-status &lt;base&gt;...HEAD</c> in the repository.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="baseReference">Base reference, for example <c>origin/main</c>.</param>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    public static IReadOnlyList<ChangedPath> FromGit(string root, string baseReference)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (var argument in new[] { "diff", "--name-status", "--no-renames", $"{baseReference}...HEAD" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git diff failed: {error.Trim()}");
        }

        return Parse(output);
    }

    /// <summary>Evaluates a change set.</summary>
    /// <param name="changes">The changed paths.</param>
    public static IReadOnlyList<Finding> Evaluate(IReadOnlyList<ChangedPath> changes)
    {
        var findings = new List<Finding>();
        var lifecycleApps = changes
            .Where(change => change.Status is 'A' or 'D' && IsDescriptor(change.Path))
            .Select(change => PlatformRepository.AppOf(change.Path)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var touched = changes.SelectMany(change => change.OldPath is null ? [change.Path] : new[] { change.OldPath, change.Path }).Distinct(StringComparer.Ordinal).ToArray();
        if (lifecycleApps.Length > 0)
        {
            var subject = string.Join(",", lifecycleApps);
            foreach (var path in touched)
            {
                var owner = PlatformRepository.AppOf(path);
                if (owner is null)
                {
                    findings.Add(Finding.Error("blast-radius", subject, $"an onboarding or retirement change touches the platform path {path}; move platform changes to their own pull request", path));
                }
                else if (!lifecycleApps.Contains(owner, StringComparer.Ordinal))
                {
                    findings.Add(Finding.Error("blast-radius", subject, $"an onboarding or retirement change touches app '{owner}'", path));
                }
            }

            return findings;
        }

        var apps = touched.Select(PlatformRepository.AppOf).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var platform = touched.Where(path => PlatformRepository.AppOf(path) is null).ToArray();
        if (apps.Length > 1)
        {
            findings.Add(Finding.Warning("blast-radius", string.Join(",", apps), "one change touches several apps; review it as a platform change"));
        }

        if (apps.Length > 0 && platform.Length > 0)
        {
            findings.Add(Finding.Warning("blast-radius", string.Join(",", apps), $"one change touches app paths and {platform.Length} platform path(s); review it as a platform change"));
        }

        return findings;
    }

    private static bool IsDescriptor(string path) =>
        path.StartsWith("apps/", StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal) && path.Count(c => c == '/') == 1;

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}
