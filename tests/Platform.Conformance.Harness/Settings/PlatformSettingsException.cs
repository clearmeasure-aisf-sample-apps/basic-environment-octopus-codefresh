namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// The settings file is malformed or holds something it must not (such as a secret). Unlike a missing
/// prerequisite this fails the fixture: it is a mistake to fix, not a reason to skip.
/// </summary>
public sealed class PlatformSettingsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong and where.</param>
    public PlatformSettingsException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    /// <param name="message">What is wrong and where.</param>
    /// <param name="innerException">The parser error.</param>
    public PlatformSettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
