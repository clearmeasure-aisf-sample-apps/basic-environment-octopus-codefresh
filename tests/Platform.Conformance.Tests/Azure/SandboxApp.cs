using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Azure;

/// <summary>
/// HTTP client of the conformance fixture in one environment: <c>https://sandbox-&lt;env&gt;.&lt;apps-domain-&lt;tier&gt;&gt;</c>
/// (fixtures/sandbox-app: <c>GET /healthz</c>, <c>GET</c> and <c>PUT /data/canary</c>). The canary is one SQL row that the
/// tests write before a disruption and read after it.
/// </summary>
public sealed class SandboxApp : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;

    /// <summary>Creates the client.</summary>
    /// <param name="environment">tdd, uat or prod.</param>
    /// <param name="appsDomain">The tier's apps domain, for example <c>20-30-40-50.sslip.io</c>.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public SandboxApp(string environment, string appsDomain, TimeSpan timeout)
    {
        BaseUri = new Uri($"https://{AzurePlatform.Namespace(AzurePlatform.Sandbox, environment)}.{appsDomain}/");
        http = PlatformHttp.Create(BaseUri, timeout);
    }

    /// <summary>The app's base URL.</summary>
    public Uri BaseUri { get; }

    /// <summary>The stored canary, or <c>null</c> before the first write.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="SandboxUnavailableException">The app or its database did not answer (for example 503 during a restore).</exception>
    public async Task<SandboxCanary?> GetCanaryAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri("data/canary", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SandboxUnavailableException($"GET {BaseUri}data/canary answered {(int)response.StatusCode}: {Truncate(body)}");
        }

        var document = JsonSerializer.Deserialize<CanaryBody>(body, Json)
            ?? throw new SandboxUnavailableException($"GET {BaseUri}data/canary returned no body.");
        var updated = DateTimeOffset.TryParse(document.UpdatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return new SandboxCanary(document.Value ?? string.Empty, updated);
    }

    /// <summary>Stores a canary value.</summary>
    /// <param name="value">1 to 200 characters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="SandboxUnavailableException">The app or its database did not accept it.</exception>
    public async Task PutCanaryAsync(string value, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync(new Uri("data/canary", UriKind.Relative), new { value }, Json, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new SandboxUnavailableException($"PUT {BaseUri}data/canary answered {(int)response.StatusCode}: {Truncate(body)}");
        }
    }

    /// <summary>The status of <c>GET /healthz</c> (no database call).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<HttpStatusCode> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri("healthz", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    /// <summary><c>true</c> for failures that a restart, a restore or a rebuild explains: retry them while waiting.</summary>
    /// <param name="exception">The failure.</param>
    public static bool IsTransient(Exception exception) =>
        exception is SandboxUnavailableException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException };

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => http.Dispose();

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private sealed record CanaryBody(string? Value, string? UpdatedAtUtc);
}

/// <summary>A canary row of the fixture app.</summary>
/// <param name="Value">Stored text.</param>
/// <param name="UpdatedAtUtc">When it was last written.</param>
public sealed record SandboxCanary(string Value, DateTimeOffset UpdatedAtUtc);

/// <summary>The fixture app or its database did not answer as expected.</summary>
public sealed class SandboxUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was called and what came back.</param>
    public SandboxUnavailableException(string message)
        : base(message)
    {
    }
}
