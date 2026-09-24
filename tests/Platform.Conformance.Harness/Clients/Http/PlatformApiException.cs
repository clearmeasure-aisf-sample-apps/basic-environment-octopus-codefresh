using System.Net;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// A platform REST call failed. The message names the system, method, path and status and includes the start of
/// the response body; it never includes request headers, so API keys and tokens cannot leak into test output.
/// </summary>
public sealed class PlatformApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="system">System name, for example <c>Octopus</c>.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path relative to the API base.</param>
    /// <param name="statusCode">Response status, or <c>null</c> when no response arrived.</param>
    /// <param name="detail">Start of the response body or the failure reason.</param>
    /// <param name="innerException">Underlying exception, if any.</param>
    public PlatformApiException(string system, string method, string path, HttpStatusCode? statusCode, string detail, Exception? innerException = null)
        : base(BuildMessage(system, method, path, statusCode, detail), innerException)
    {
        System = system;
        Method = method;
        Path = path;
        StatusCode = statusCode;
    }

    /// <summary>System name, for example <c>Octopus</c>.</summary>
    public string System { get; }

    /// <summary>HTTP method.</summary>
    public string Method { get; }

    /// <summary>Request path relative to the API base.</summary>
    public string Path { get; }

    /// <summary>Response status, or <c>null</c> when no response arrived.</summary>
    public HttpStatusCode? StatusCode { get; }

    private static string BuildMessage(string system, string method, string path, HttpStatusCode? statusCode, string detail)
    {
        var status = statusCode is { } code ? $"{(int)code} {code}" : "no response";
        return $"{system} {method} {path} failed ({status}): {detail}";
    }
}
