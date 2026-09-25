namespace Platform.Conformance.Offline.Kit.Consistency;

/// <summary>Status of a finding, as <c>scripts/checks/consistency.sh</c> prints it.</summary>
internal enum FindingStatus
{
    /// <summary>PASS: the path agrees with the contracts.</summary>
    Pass,

    /// <summary>FAIL: a violation; the check fails.</summary>
    Fail,

    /// <summary>WARN: advisory; the check still passes.</summary>
    Warn,

    /// <summary>SKIP: the path's top-level folder is absent (a partial tree); the check still passes.</summary>
    Skip,
}

/// <summary>
/// One line of a consistency check: status, check ID, the package that owns the path (contracts <c>ownership</c>,
/// longest prefix wins), the path and the message. <see cref="ToString"/> prints it exactly as the script does:
/// <c>STATUS ID [owner] path: message</c>.
/// </summary>
/// <param name="Status">PASS, FAIL, WARN or SKIP.</param>
/// <param name="Id">Check ID, for example <c>C09</c>; <c>C99</c> for a check that crashed.</param>
/// <param name="Owner">Owning package, or <c>-</c>.</param>
/// <param name="Path">Repository-relative path, or <c>-</c>.</param>
/// <param name="Message">What was found.</param>
internal sealed record ConsistencyFinding(FindingStatus Status, string Id, string Owner, string Path, string Message)
{
    /// <summary><c>STATUS ID [owner] path: message</c>, padded like the script's output.</summary>
    public override string ToString() => $"{Status.ToString().ToUpperInvariant(),-4} {Id,-4} [{Owner}] {(Path.Length == 0 ? "-" : Path)}: {Message}";
}

/// <summary>A check of the port: its ID, the name of the script's function (used in C99 lines) and its title.</summary>
/// <param name="Id">Check ID, for example <c>C02</c>.</param>
/// <param name="Function">The script's function, for example <c>c02_annotations</c>.</param>
/// <param name="Title">Title from the script's header, for example <c>Octopus annotations</c>.</param>
internal sealed record ConsistencyCheck(string Id, string Function, string Title);
