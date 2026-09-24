using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness.Settings;

/// <summary>
/// Settings of the conformance harness. Non-secret values come from a JSON file (default
/// <c>tests/platform.settings.json</c>, overridden by <c>PLATFORM_SETTINGS_FILE</c>); secrets come only from
/// environment variables. A value that is blank or still a <c>&lt;placeholder&gt;</c> counts as missing, and a live
/// test that needs it is Inconclusive (see <see cref="Check"/>).
/// </summary>
public sealed partial class PlatformSettings
{
    /// <summary>Settings file name under the <c>tests/</c> folder.</summary>
    public const string DefaultFileName = "platform.settings.json";

    /// <summary>Codefresh API base used when the settings file does not name one.</summary>
    public const string DefaultCodefreshUrl = "https://g.codefresh.io/api";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IReadOnlyDictionary<PlatformTier, PlatformTierSettings> tiers;

    private PlatformSettings(SettingsFile file, string source, string? sourceFile, IEnvironmentVariables environment)
    {
        SettingsSource = source;
        SourceFile = sourceFile;
        OctopusUrl = Clean(file.OctopusUrl);
        OctopusSpaceId = Clean(file.OctopusSpaceId);
        CodefreshUrl = Clean(file.CodefreshUrl) ?? DefaultCodefreshUrl;
        AzureSubscriptionId = Clean(environment.Get(EnvironmentVariableNames.AzureSubscriptionId)) ?? Clean(file.AzureSubscriptionId);
        AzureTenantId = Clean(environment.Get(EnvironmentVariableNames.AzureTenantId)) ?? Clean(file.AzureTenantId);
        RegistryLoginServer = Clean(file.RegistryLoginServer);
        GitHubOrg = Clean(file.GitHubOrg);
        EnvRepo = Clean(file.EnvRepo);
        AppRepos = (file.AppRepos ?? []).Select(repo => repo.Trim()).Where(repo => repo.Length > 0).ToArray();
        tiers = ReadTiers(file.Tiers, source);
        TimeLimits = ReadTimeLimits(file.TimeLimits, source);
        Secrets = PlatformSecrets.FromEnvironment(environment);
        TlsSystemTrust = string.Equals(Clean(environment.Get(EnvironmentVariableNames.TlsSystemTrust)), "true", StringComparison.OrdinalIgnoreCase);
        ValidateUrl(nameof(OctopusUrl), OctopusUrl, source);
        ValidateUrl(nameof(CodefreshUrl), CodefreshUrl, source);
    }

    /// <summary>Where the settings came from, for messages: the file path, or a note that no file was found.</summary>
    public string SettingsSource { get; }

    /// <summary>Full path of the settings file, or <c>null</c> when none was found.</summary>
    public string? SourceFile { get; }

    /// <summary>Octopus server URL, for example <c>https://example.octopus.app</c>.</summary>
    public string? OctopusUrl { get; }

    /// <summary>Octopus space ID, for example <c>Spaces-1</c>.</summary>
    public string? OctopusSpaceId { get; }

    /// <summary>Codefresh API base URL (default <c>https://g.codefresh.io/api</c>).</summary>
    public string CodefreshUrl { get; }

    /// <summary>Azure subscription ID; <c>AZURE_SUBSCRIPTION_ID</c> wins over the file.</summary>
    public string? AzureSubscriptionId { get; }

    /// <summary>Entra tenant ID; <c>AZURE_TENANT_ID</c> wins over the file.</summary>
    public string? AzureTenantId { get; }

    /// <summary>Container registry login server, for example <c>myregistry.azurecr.io</c>.</summary>
    public string? RegistryLoginServer { get; }

    /// <summary>GitHub organization of the platform repositories.</summary>
    public string? GitHubOrg { get; }

    /// <summary>Environment repository as <c>owner/name</c>.</summary>
    public string? EnvRepo { get; }

    /// <summary>Application repositories as <c>owner/name</c>.</summary>
    public IReadOnlyList<string> AppRepos { get; }

    /// <summary>Time limits for waits.</summary>
    public PlatformTimeLimits TimeLimits { get; }

    /// <summary>Secrets from environment variables.</summary>
    public PlatformSecrets Secrets { get; }

    /// <summary><c>true</c> when <c>PLATFORM_TLS_SYSTEM_TRUST=true</c>: validate cluster TLS against the system trust store instead of pinning the cluster CA.</summary>
    public bool TlsSystemTrust { get; }

    /// <summary>Returns the settings of <paramref name="tier"/>, empty when the file does not configure it.</summary>
    /// <param name="tier">The tier.</param>
    public PlatformTierSettings Tier(PlatformTier tier) => tiers.TryGetValue(tier, out var settings) ? settings : PlatformTierSettings.Empty;

    /// <summary>Starts a prerequisite check whose messages name this settings source.</summary>
    /// <param name="purpose">What needs the prerequisites, for example "the Octopus API".</param>
    public PrerequisiteCheck Check(string purpose) => new(purpose, SettingsSource);

    /// <summary><c>true</c> when <paramref name="value"/> is blank or still contains a <c>&lt;placeholder&gt;</c>.</summary>
    /// <param name="value">A setting value.</param>
    public static bool IsMissing(string? value) => string.IsNullOrWhiteSpace(value) || IsPlaceholder(value);

    /// <summary><c>true</c> when <paramref name="value"/> contains a <c>&lt;placeholder&gt;</c> such as <c>&lt;OCTOPUS_URL&gt;</c>.</summary>
    /// <param name="value">A setting value.</param>
    public static bool IsPlaceholder(string? value) => value is not null && PlaceholderPattern().IsMatch(value);

    /// <summary>
    /// Loads the settings: the file named by <c>PLATFORM_SETTINGS_FILE</c>, else <c>tests/platform.settings.json</c> under
    /// the repository root found from <paramref name="startDirectory"/>. Without a file every setting is missing (live tests
    /// are Inconclusive); a malformed file throws.
    /// </summary>
    /// <param name="environment">Environment variables; the process environment when omitted.</param>
    /// <param name="startDirectory">Where to start looking for the repository root; the test assembly folder when omitted.</param>
    /// <exception cref="PlatformSettingsException">The named file does not exist, or the file is malformed or holds a secret.</exception>
    public static PlatformSettings Load(IEnvironmentVariables? environment = null, string? startDirectory = null)
    {
        var variables = environment ?? ProcessEnvironmentVariables.Instance;
        var explicitPath = Clean(variables.Get(EnvironmentVariableNames.SettingsFile));
        if (explicitPath is not null)
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
            {
                throw new PlatformSettingsException($"{EnvironmentVariableNames.SettingsFile} names {fullPath}, which does not exist.");
            }

            return Parse(File.ReadAllText(fullPath), fullPath, variables);
        }

        var root = RepositoryRoot.TryFind(startDirectory ?? AppContext.BaseDirectory, variables);
        var defaultPath = root is null ? null : Path.Combine(root, "tests", DefaultFileName);
        if (defaultPath is null || !File.Exists(defaultPath))
        {
            var note = defaultPath is null
                ? $"no settings file (repository root not found; set {EnvironmentVariableNames.SettingsFile})"
                : $"no settings file ({defaultPath} does not exist)";
            return new PlatformSettings(new SettingsFile(), note, null, variables);
        }

        return Parse(File.ReadAllText(defaultPath), defaultPath, variables);
    }

    /// <summary>Parses settings JSON and merges the environment.</summary>
    /// <param name="json">Content of the settings file.</param>
    /// <param name="sourcePath">Path or name of the file, used in messages.</param>
    /// <param name="environment">Environment variables for secrets and overrides.</param>
    /// <exception cref="PlatformSettingsException">The JSON is malformed, has an unknown property (secrets are never allowed) or an invalid value.</exception>
    public static PlatformSettings Parse(string json, string sourcePath, IEnvironmentVariables environment)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(environment);
        SettingsFile file;
        try
        {
            file = JsonSerializer.Deserialize<SettingsFile>(json, FileOptions) ?? new SettingsFile();
        }
        catch (JsonException ex)
        {
            throw new PlatformSettingsException(DescribeJsonError(ex, sourcePath), ex);
        }

        return new PlatformSettings(file, sourcePath, sourcePath, environment);
    }

    private static string DescribeJsonError(JsonException ex, string sourcePath)
    {
        var location = ex.LineNumber is { } line ? $"{sourcePath}:{line + 1}" : sourcePath;
        var property = ex.Path?.Split('.').LastOrDefault()?.Trim('$', '[', ']', '\'') ?? string.Empty;
        var hint = SecretLikeName().IsMatch(property)
            ? $" Property '{property}' looks like a secret: secrets never go in the settings file; set {string.Join(", ", EnvironmentVariableNames.Secrets)} in the environment instead."
            : string.Empty;
        return $"{location}: invalid settings file ({ex.Message}).{hint}";
    }

    private static IReadOnlyDictionary<PlatformTier, PlatformTierSettings> ReadTiers(Dictionary<string, TierFile>? file, string source)
    {
        var result = new Dictionary<PlatformTier, PlatformTierSettings>();
        foreach (var (key, value) in file ?? [])
        {
            if (!PlatformTierNames.TryParse(key, out var tier))
            {
                throw new PlatformSettingsException($"{source}: unknown tier '{key}' under Tiers; use build, nonprod or prod.");
            }

            result[tier] = new PlatformTierSettings
            {
                SubscriptionId = Clean(value.SubscriptionId),
                ResourceGroup = Clean(value.ResourceGroup),
                ClusterName = Clean(value.ClusterName),
                ResourceGroups = (value.ResourceGroups ?? []).Select(group => group.Trim()).Where(group => group.Length > 0).ToArray(),
            };
        }

        return result;
    }

    private static PlatformTimeLimits ReadTimeLimits(TimeLimitsFile? file, string source)
    {
        var defaults = new PlatformTimeLimits();
        if (file is null)
        {
            return defaults;
        }

        return new PlatformTimeLimits
        {
            PollInterval = Positive(file.PollIntervalSeconds, TimeSpan.FromSeconds, defaults.PollInterval, "TimeLimits.PollIntervalSeconds", source),
            HttpTimeout = Positive(file.HttpTimeoutSeconds, TimeSpan.FromSeconds, defaults.HttpTimeout, "TimeLimits.HttpTimeoutSeconds", source),
            RunbookTimeout = Positive(file.RunbookMinutes, TimeSpan.FromMinutes, defaults.RunbookTimeout, "TimeLimits.RunbookMinutes", source),
            DeploymentTimeout = Positive(file.DeploymentMinutes, TimeSpan.FromMinutes, defaults.DeploymentTimeout, "TimeLimits.DeploymentMinutes", source),
            BuildTimeout = Positive(file.BuildMinutes, TimeSpan.FromMinutes, defaults.BuildTimeout, "TimeLimits.BuildMinutes", source),
            WakeTimeout = Positive(file.WakeMinutes, TimeSpan.FromMinutes, defaults.WakeTimeout, "TimeLimits.WakeMinutes", source),
            ArgoSyncTimeout = Positive(file.ArgoSyncMinutes, TimeSpan.FromMinutes, defaults.ArgoSyncTimeout, "TimeLimits.ArgoSyncMinutes", source),
        };
    }

    private static TimeSpan Positive(double? value, Func<double, TimeSpan> unit, TimeSpan fallback, string name, string source)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value <= 0)
        {
            throw new PlatformSettingsException($"{source}: {name} must be positive (was {value}).");
        }

        return unit(value.Value);
    }

    private static void ValidateUrl(string name, string? value, string source)
    {
        if (IsMissing(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PlatformSettingsException($"{source}: {name} must be an absolute https URL (was '{value}').");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("<[^<>]+>")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("(?i)(api.?key|secret|token|password|credential)")]
    private static partial Regex SecretLikeName();

    private sealed class SettingsFile
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }

        public string? OctopusUrl { get; set; }

        public string? OctopusSpaceId { get; set; }

        public string? CodefreshUrl { get; set; }

        public string? AzureSubscriptionId { get; set; }

        public string? AzureTenantId { get; set; }

        public string? RegistryLoginServer { get; set; }

        public string? GitHubOrg { get; set; }

        public string? EnvRepo { get; set; }

        public List<string>? AppRepos { get; set; }

        public Dictionary<string, TierFile>? Tiers { get; set; }

        public TimeLimitsFile? TimeLimits { get; set; }
    }

    private sealed class TierFile
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }

        public string? SubscriptionId { get; set; }

        public string? ResourceGroup { get; set; }

        public string? ClusterName { get; set; }

        public List<string>? ResourceGroups { get; set; }
    }

    private sealed class TimeLimitsFile
    {
        [JsonPropertyName("$comment")]
        public string? Comment { get; set; }

        public double? PollIntervalSeconds { get; set; }

        public double? HttpTimeoutSeconds { get; set; }

        public double? RunbookMinutes { get; set; }

        public double? DeploymentMinutes { get; set; }

        public double? BuildMinutes { get; set; }

        public double? WakeMinutes { get; set; }

        public double? ArgoSyncMinutes { get; set; }
    }
}
