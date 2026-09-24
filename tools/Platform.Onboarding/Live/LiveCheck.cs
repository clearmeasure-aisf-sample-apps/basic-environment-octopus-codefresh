using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Platform.Onboarding.Cli;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Output;

namespace Platform.Onboarding.Live;

/// <summary>
/// <c>check --live</c>: read-only GETs that confirm each descriptor's Octopus group and projects and its Codefresh projects
/// exist, and that a frozen app's Octopus projects are disabled. Tenants, vaults and disks are checked by the harness
/// (CAP-KIT-003), which holds the Azure and cluster credentials.
/// </summary>
internal sealed class LiveCheck
{
    /// <summary>Octopus server URL variable.</summary>
    public const string OctopusUrlVariable = "OCTOPUS_URL";

    /// <summary>Octopus space ID variable.</summary>
    public const string OctopusSpaceVariable = "OCTOPUS_SPACE_ID";

    /// <summary>Octopus API key variable (the Space Manager key; never logged).</summary>
    public const string OctopusKeyVariable = "OCTOPUS_API_KEY";

    /// <summary>Codefresh API key variable.</summary>
    public const string CodefreshKeyVariable = "CODEFRESH_API_KEY";

    /// <summary>Codefresh API base variable; default <c>https://g.codefresh.io/api</c>.</summary>
    public const string CodefreshUrlVariable = "CODEFRESH_URL";

    private readonly IEnvironment environment;
    private readonly Func<HttpMessageHandler> handlerFactory;

    /// <summary>Creates the live check.</summary>
    /// <param name="environment">Source of URLs and keys.</param>
    /// <param name="handlerFactory">HTTP handler factory; tests pass a stub.</param>
    public LiveCheck(IEnvironment environment, Func<HttpMessageHandler>? handlerFactory = null)
    {
        this.environment = environment;
        this.handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
    }

    /// <summary>Names of the missing variables; the Octopus ones are required, the Codefresh key is optional.</summary>
    public IReadOnlyList<string> MissingOctopusVariables() =>
        new[] { OctopusUrlVariable, OctopusSpaceVariable, OctopusKeyVariable }.Where(name => string.IsNullOrWhiteSpace(environment.Get(name))).ToArray();

    /// <summary>Checks the live objects of the given apps.</summary>
    /// <param name="descriptors">The apps.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<IReadOnlyList<Finding>> RunAsync(IReadOnlyList<AppDescriptor> descriptors, CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        await CheckOctopusAsync(descriptors, findings, cancellationToken).ConfigureAwait(false);
        await CheckCodefreshAsync(descriptors, findings, cancellationToken).ConfigureAwait(false);
        return findings;
    }

    private async Task CheckOctopusAsync(IReadOnlyList<AppDescriptor> descriptors, List<Finding> findings, CancellationToken cancellationToken)
    {
        var baseUrl = environment.Get(OctopusUrlVariable)!.TrimEnd('/');
        var space = environment.Get(OctopusSpaceVariable)!.Trim();
        using var http = new HttpClient(handlerFactory(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.Add("X-Octopus-ApiKey", environment.Get(OctopusKeyVariable)!.Trim());
        using var groups = await GetJsonAsync(http, $"{baseUrl}/api/{space}/projectgroups/all", cancellationToken).ConfigureAwait(false);
        using var projects = await GetJsonAsync(http, $"{baseUrl}/api/{space}/projects/all", cancellationToken).ConfigureAwait(false);
        var groupIds = groups.RootElement.EnumerateArray().ToDictionary(group => group.GetProperty("Name").GetString() ?? string.Empty, group => group.GetProperty("Id").GetString(), StringComparer.Ordinal);
        var projectsByName = projects.RootElement.EnumerateArray().ToDictionary(project => project.GetProperty("Name").GetString() ?? string.Empty, project => project, StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var groupName = $"app-{descriptor.Name}";
            groupIds.TryGetValue(groupName, out var groupId);
            if (groupId is null)
            {
                findings.Add(Finding.Error("live", descriptor.Name, $"Octopus project group '{groupName}' does not exist; apply octopus/terraform"));
            }

            foreach (var spec in descriptor.OctopusProjects)
            {
                if (!projectsByName.TryGetValue(spec.Name, out var project))
                {
                    findings.Add(Finding.Error("live", descriptor.Name, $"Octopus project '{spec.Name}' does not exist; apply octopus/terraform"));
                    continue;
                }

                if (groupId is not null && project.GetProperty("ProjectGroupId").GetString() != groupId)
                {
                    findings.Add(Finding.Error("live", descriptor.Name, $"Octopus project '{spec.Name}' is not in group '{groupName}'"));
                }

                var disabled = project.TryGetProperty("IsDisabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (descriptor.IsFrozen != disabled)
                {
                    findings.Add(Finding.Error("live", descriptor.Name, $"Octopus project '{spec.Name}' is {(disabled ? "disabled" : "enabled")} but the app is {descriptor.Status}; apply octopus/terraform"));
                }
            }
        }
    }

    private async Task CheckCodefreshAsync(IReadOnlyList<AppDescriptor> descriptors, List<Finding> findings, CancellationToken cancellationToken)
    {
        var key = environment.Get(CodefreshKeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            findings.Add(Finding.Warning("live", "-", $"{CodefreshKeyVariable} is not set; Codefresh projects not checked"));
            return;
        }

        var baseUrl = (environment.Get(CodefreshUrlVariable) is { Length: > 0 } configured ? configured : "https://g.codefresh.io/api").TrimEnd('/');
        using var http = new HttpClient(handlerFactory(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(key.Trim());
        foreach (var descriptor in descriptors)
        {
            foreach (var project in descriptor.CodefreshProjects)
            {
                // [VERIFY] GET /projects/name/{name} answers 404 for an unknown project.
                using var response = await http.GetAsync(new Uri($"{baseUrl}/projects/name/{Uri.EscapeDataString(project)}"), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    findings.Add(Finding.Error("live", descriptor.Name, $"Codefresh project '{project}' does not exist; run codefresh/register.sh --app {descriptor.Name}"));
                }
                else if (!response.IsSuccessStatusCode)
                {
                    findings.Add(Finding.Error("live", descriptor.Name, $"Codefresh answered {(int)response.StatusCode} for project '{project}'"));
                }
            }
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {new Uri(url).AbsolutePath} answered {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
