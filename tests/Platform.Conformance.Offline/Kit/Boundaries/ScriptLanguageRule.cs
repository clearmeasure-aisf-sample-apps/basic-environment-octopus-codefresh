using System.Text.RegularExpressions;

namespace Platform.Conformance.Offline.Kit.Boundaries;

/// <summary>A shell script that stays a shell script.</summary>
/// <param name="Glob">Repository-relative path glob; <c>*</c> matches within one path segment.</param>
/// <param name="Reason">Why PowerShell 7 does not fit there.</param>
internal sealed record ShellScriptException(string Glob, string Reason);

/// <summary>
/// TB23: scripts are PowerShell 7. The platform standardises on PowerShell 7 and .NET, so a tracked <c>*.sh</c> file is
/// either a permanent exception (with its reason) or pending conversion, and an Octopus step in an OCL file under
/// <c>.octopus/</c> or <c>octopus/templates/</c> sets <c>Octopus.Action.Script.Syntax = "PowerShell"</c> unless its file is
/// pending conversion. The pending lists only shrink: the rule also fails when a pending entry no longer needs to be there,
/// so each entry goes in the change that converts its file. Scripts embedded in Kubernetes manifests under
/// <c>gitops/</c> are not files and are out of scope.
/// </summary>
/// <remarks>
/// "Tracked" means <c>git ls-files</c> when the tree is a Git work tree and git is on PATH; otherwise every <c>*.sh</c>
/// file of the tree outside <c>.git</c>, <c>.terraform</c>, <c>node_modules</c>, <c>bin</c> and <c>obj</c>. A pending entry
/// whose top-level folder is absent (a partial tree) is not reported; a tree without shell scripts and OCL files skips
/// the rule.
/// </remarks>
internal static class ScriptLanguageRule
{
    /// <summary>Rule ID.</summary>
    public const string Id = "TB23";

    /// <summary>Rule statement.</summary>
    public const string Description = "Scripts are PowerShell 7: shell scripts and Bash steps only from the exception and pending lists";

    private static readonly Regex Syntax = PosixPatterns.Ere("^[ \t]*\"?Octopus\\.Action\\.Script\\.Syntax\"?[ \t]*=[ \t]*\"([^\"]*)\"");
    private static readonly Regex Comment = PosixPatterns.Ere("^[ \t]*(#|//)");

    /// <summary>Shell scripts that stay shell scripts, each with its reason.</summary>
    public static IReadOnlyList<ShellScriptException> PermanentExceptions { get; } =
    [
        new("containers/apps/*/*/migrate.sh", "Container entrypoint on a .NET runtime image, which ships no pwsh"),
        new("terraform/tier/scripts/aks-token.sh", "Exec credential plugin of the Terraform Kubernetes providers, started once per client, so start-up time matters"),
    ];

    /// <summary>
    /// Shell scripts pending conversion to PowerShell 7 (<c>.ps1</c>), as path globs (<c>*</c> within one segment); remove an
    /// entry when no file it matches is a shell script any more.
    /// </summary>
    public static IReadOnlyList<string> PendingShellScripts { get; } =
    [
        // Every app's copies of the starter scripts (scaffold, then own), until the starters below are converted.
        "codefresh/apps/*/scripts/*.sh",
        "codefresh/register.sh",
        "codefresh/templates/dotnet-buildps1/scripts/buildinfo.sh",
        "codefresh/templates/dotnet-buildps1/scripts/changed-paths.sh",
        "codefresh/templates/dotnet-buildps1/scripts/gate.sh",
        "codefresh/templates/dotnet-buildps1/scripts/stage-built.sh",
        "codefresh/templates/dotnet-buildps1/scripts/supply-chain.sh",
        "codefresh/templates/dotnet-buildps1/scripts/version.sh",
        "codefresh/templates/minimal/scripts/buildinfo.sh",
        "codefresh/templates/minimal/scripts/supply-chain.sh",
        "codefresh/templates/minimal/scripts/version.sh",
        "codefresh/templates/multi-image/scripts/buildinfo.sh",
        "codefresh/templates/multi-image/scripts/supply-chain.sh",
        "codefresh/templates/multi-image/scripts/version.sh",
        "octopus/apply.sh",
        "octopus/step-templates/db-backup.sh",
        "octopus/step-templates/pin-writer.sh",
        "octopus/step-templates/sod-guard.sh",
    ];

    /// <summary>
    /// OCL files with steps whose inline script is still Bash, as path globs; remove an entry when every step of the files it
    /// matches runs PowerShell.
    /// </summary>
    public static IReadOnlyList<string> PendingBashSteps { get; } =
    [
        // Every app's copies of the starter processes and runbooks, until octopus/templates is converted.
        ".octopus/apps/*/*/deployment_process.ocl",
        ".octopus/apps/*/*/runbooks/*.ocl",
        ".octopus/platform-infrastructure/runbooks/apps-apply.ocl",
        ".octopus/platform-infrastructure/runbooks/apps-plan.ocl",
        ".octopus/platform-infrastructure/runbooks/env-apply.ocl",
        ".octopus/platform-infrastructure/runbooks/env-destroy.ocl",
        ".octopus/platform-infrastructure/runbooks/env-plan.ocl",
        ".octopus/platform-infrastructure/runbooks/env-sleep.ocl",
        ".octopus/platform-infrastructure/runbooks/env-wake.ocl",
        ".octopus/platform-infrastructure/runbooks/rotate-db-passwords.ocl",
        ".octopus/platform-wake/deployment_process.ocl",
        "octopus/templates/db-runbooks/runbooks/db-restore.ocl",
        "octopus/templates/deploy-minimal/deployment_process.ocl",
        "octopus/templates/deploy-with-db/deployment_process.ocl",
    ];

    /// <summary>Checks the tree: shell scripts, Octopus script steps, and both pending lists.</summary>
    /// <param name="tree">The repository tree.</param>
    public static BoundaryResult Check(BoundaryTree tree)
    {
        var scripts = tree.TrackedFiles(".sh");
        var processes = tree.FilesIn(".octopus", "octopus/templates").Where(file => file.EndsWith(".ocl", StringComparison.Ordinal)).ToArray();
        if (scripts.Count == 0 && processes.Length == 0)
        {
            return BoundaryResult.Skip(Id, Description, "*.sh .octopus octopus/templates");
        }

        var unlisted = scripts
            .Where(script => !PermanentExceptions.Any(exception => PathGlob.Matches(script, exception.Glob)) && !PendingShellScripts.Any(entry => PathGlob.Matches(script, entry)))
            .Select(script => new BoundaryFinding(Id, script, null,
                "shell script in neither list: convert it to PowerShell 7 (.ps1), or list it in ScriptLanguageRule (a permanent exception with its reason, or pending conversion)"));
        var convertedScripts = PendingShellScripts
            .Where(entry => InTree(tree, entry) && !scripts.Any(script => PathGlob.Matches(script, entry)))
            .Select(entry => new BoundaryFinding(Id, entry, null,
                "pending conversion, but it matches no tracked shell script: remove the entry from ScriptLanguageRule.PendingShellScripts"));
        var steps = processes.SelectMany(process => NonPowerShellSteps(tree, process)).ToArray();
        var bashSteps = steps
            .Where(step => !PendingBashSteps.Any(entry => PathGlob.Matches(step.Path, entry)))
            .Select(step => step.Finding(Id));
        var convertedProcesses = PendingBashSteps
            .Where(entry => InTree(tree, entry) && !steps.Any(step => PathGlob.Matches(step.Path, entry)))
            .Select(entry => new BoundaryFinding(Id, entry, null,
                "pending conversion, but the file holds no Bash step (or no longer exists): remove the entry from ScriptLanguageRule.PendingBashSteps"));
        return BoundaryResult.Of(Id, Description, unlisted.Concat(convertedScripts).Concat(bashSteps).Concat(convertedProcesses));
    }

    /// <summary>
    /// <c>true</c> when the top-level folder of a list entry exists: an entry under a folder that a partial tree lacks is
    /// absent, not converted.
    /// </summary>
    private static bool InTree(BoundaryTree tree, string entry) => Directory.Exists(tree.FullPath(entry[..entry.IndexOf('/', StringComparison.Ordinal)]));

    private static IEnumerable<LineHit> NonPowerShellSteps(BoundaryTree tree, string process)
    {
        var lines = tree.Lines(process) ?? [];
        IReadOnlyList<string>? contexts = null;
        for (var index = 0; index < lines.Count; index++)
        {
            if (Comment.IsMatch(lines[index]) || Syntax.Match(lines[index]) is not { Success: true } syntax
                || string.Equals(syntax.Groups[1].Value, "PowerShell", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            contexts ??= LineContext.Of(process, lines);
            yield return new LineHit(process, index + 1,
                $"step {contexts[index]}: Octopus.Action.Script.Syntax = \"{syntax.Groups[1].Value}\"; scripts are PowerShell 7 (Syntax \"PowerShell\"), or list the file in ScriptLanguageRule.PendingBashSteps");
        }
    }
}
