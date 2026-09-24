using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Platform.Conformance.Harness;

/// <summary>
/// Thrown when a live test cannot run because a secret, a setting or a credential is missing.
/// NUnit records the test as <b>Inconclusive</b> wherever this is thrown (it is a <see cref="ResultStateException"/>),
/// so a missing prerequisite never fails and never passes a test.
/// </summary>
public sealed class PlatformPrerequisiteException : ResultStateException
{
    /// <summary>Creates the exception with a message that names what is missing and how to supply it.</summary>
    /// <param name="message">Explanation shown as the Inconclusive reason.</param>
    public PlatformPrerequisiteException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that revealed the missing prerequisite.</summary>
    /// <param name="message">Explanation shown as the Inconclusive reason.</param>
    /// <param name="innerException">The underlying failure, for example a credential that is unavailable.</param>
    public PlatformPrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Always <see cref="ResultState.Inconclusive"/>.</summary>
    public override ResultState ResultState => ResultState.Inconclusive;
}
