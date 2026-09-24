using System.Text.RegularExpressions;

namespace Sandbox.Migrator;

/// <summary>Pure planning logic of the migrator: which scripts run, in which order, in which batches.</summary>
public static partial class ScriptPlan
{
    /// <summary>
    /// Returns the scripts that still have to run: every <c>*.sql</c> name not yet journaled, in ordinal order.
    /// </summary>
    /// <param name="available">File names found in the scripts folder.</param>
    /// <param name="applied">Names already recorded in <c>dbo.SchemaVersions</c>.</param>
    public static IReadOnlyList<string> Pending(IEnumerable<string> available, IEnumerable<string> applied)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(applied);
        var done = new HashSet<string>(applied, StringComparer.OrdinalIgnoreCase);
        return available
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Where(name => !done.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Splits a script into batches at lines that hold only <c>GO</c>, as sqlcmd does.</summary>
    /// <param name="script">Script text.</param>
    public static IReadOnlyList<string> Batches(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return GoSeparator().Split(script)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0)
            .ToList();
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoSeparator();
}
