using System.Text.RegularExpressions;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// The §7.0 names the CAP-CF tests read: the runtime, the build cluster, the sandbox pipelines and the variables that
/// <c>platform-env/conformance-arm</c> hands to <c>platform-env/conformance</c>. The names are contracts
/// (contracts/platform-contracts.yaml, key <c>codefresh</c>), so the tests derive them instead of reading copies.
/// </summary>
public static partial class CodefreshPlatform
{
    /// <summary>The platform runtime <c>&lt;cf-runtime&gt;</c>, the account default.</summary>
    public const string Runtime = "aks-platform-build/codefresh";

    /// <summary>The runner agent of the runtime.</summary>
    public const string Agent = "aks-platform-build_codefresh";

    /// <summary>Group of the build cluster and the registry.</summary>
    public const string BuildGroup = "rg-platform-build";

    /// <summary>The build cluster.</summary>
    public const string BuildCluster = "aks-platform-build";

    /// <summary>AKS node resource group of the build cluster.</summary>
    public const string BuildNodeGroup = "rg-platform-build-aks-nodes";

    /// <summary>Node pool of the engine and dind pods; scales between zero and two.</summary>
    public const string BuildsPool = "builds";

    /// <summary>The conformance fixture app.</summary>
    public const string Sandbox = "sandbox";

    /// <summary>CI pipeline of the fixture; posts <see cref="CiStatus"/>.</summary>
    public const string SandboxCi = "sandbox/ci";

    /// <summary>Release pipeline of the fixture.</summary>
    public const string SandboxRelease = "sandbox/release";

    /// <summary>The conformance pipeline itself.</summary>
    public const string ConformancePipeline = "platform-env/conformance";

    /// <summary>The required status of CI on app repositories.</summary>
    public const string CiStatus = "codefresh/ci";

    /// <summary>Project of the platform runbooks.</summary>
    public const string InfrastructureProject = "platform-infrastructure";

    /// <summary>Environment of the nonprod runbooks.</summary>
    public const string InfraNonProd = "infra-nonprod";

    /// <summary>Issuer of the keyless signatures (Codefresh OIDC through Fulcio).</summary>
    public const string SignatureIssuer = "https://oidc.codefresh.io";

    /// <summary>The unsigned admission fixture, never pinned and always kept.</summary>
    public const string UnsignedFixture = "apps/sandbox/unsigned:0.0.0-fixture";

    /// <summary>Tag of the pins before an app's first release; no image carries it.</summary>
    public const string BootstrapTag = "0.0.0-bootstrap";

    /// <summary>Owner/name of the fixture repository; wins over the descriptor.</summary>
    public const string SandboxRepoVariable = "SANDBOX_APP_REPO";

    /// <summary>Commit of the failing-test branch pushed by conformance-arm.</summary>
    public const string FailingShaVariable = "CONFORMANCE_FAILING_SHA";

    /// <summary>Commit of the green branch pushed by conformance-arm.</summary>
    public const string GreenShaVariable = "CONFORMANCE_GREEN_SHA";

    /// <summary>Release commit on main pushed by conformance-arm.</summary>
    public const string ReleaseShaVariable = "CONFORMANCE_RELEASE_SHA";

    /// <summary>Build ID of the second sandbox/release build of the release commit.</summary>
    public const string RerunBuildVariable = "CONFORMANCE_RERUN_BUILD_ID";

    /// <summary>Number of a pull request of the fixture repository opened from a fork outside the org (R33).</summary>
    public const string ForkPullRequestVariable = "CONFORMANCE_FORK_PULL_REQUEST";

    /// <summary>Codefresh sets it in every build; its absence means the suite runs outside Codefresh.</summary>
    public const string BuildIdVariable = "CF_BUILD_ID";

    /// <summary>
    /// Signer identity of a release pipeline of the fixture (§7.0 signer policy, with the account name and ID as any
    /// value): <c>https://g.codefresh.io/&lt;account&gt;/sandbox/release:&lt;account id&gt;/&lt;pipeline id&gt;</c>.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^https://g\.codefresh\.io/[^/]+/sandbox(-[a-z0-9]+)?/release(-[a-z0-9-]+)?:[0-9a-f]{24}/[0-9a-f]{24}$")]
    public static partial Regex SandboxSignerIdentity();

    /// <summary>The environment repository root (the folder that holds tests/Platform.Conformance.sln).</summary>
    public static string RepositoryRoot => global::Platform.Conformance.Harness.Support.RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance);

    /// <summary>A variable of the run, or <c>null</c> when it is unset, blank or an unresolved Codefresh expression.</summary>
    /// <param name="name">Variable name.</param>
    public static string? Variable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) || value.Contains("${{", StringComparison.Ordinal) || PlatformSettings.IsPlaceholder(value) ? null : value;
    }

    /// <summary>
    /// The fixture repository as <c>owner/name</c>: <c>SANDBOX_APP_REPO</c>, else the first repository of apps/sandbox.yaml;
    /// <c>null</c> while both are placeholders.
    /// </summary>
    public static string? SandboxRepository()
    {
        if (Variable(SandboxRepoVariable) is { } configured)
        {
            return configured;
        }

        var descriptor = Path.Combine(RepositoryRoot, "apps", "sandbox.yaml");
        if (!File.Exists(descriptor))
        {
            return null;
        }

        var match = DescriptorRepository().Match(File.ReadAllText(descriptor));
        var name = match.Success ? match.Groups["name"].Value.Trim() : null;
        return string.IsNullOrEmpty(name) || PlatformSettings.IsPlaceholder(name) ? null : name;
    }

    /// <summary>Every image tag pinned under gitops/apps/*/envs/** of this checkout (Kustomize, Helm and raw references).</summary>
    public static IReadOnlyList<PinnedImage> ReadPins() => PinReader.Read(RepositoryRoot);

    /// <summary>The first seven characters of a commit SHA, as in the <c>sha-&lt;sha7&gt;</c> tags.</summary>
    /// <param name="sha">A full commit SHA.</param>
    public static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];

    [GeneratedRegex(@"^repositories:\s*\n\s*-\s*name:\s*[""']?(?<name>[^""'\s#]+)", RegexOptions.Multiline)]
    private static partial Regex DescriptorRepository();
}

/// <summary>An image tag pinned in the environment repository.</summary>
/// <param name="Repository">Registry repository, for example <c>apps/sandbox/web</c>.</param>
/// <param name="Tag">Pinned tag.</param>
/// <param name="File">Pin file, relative to the repository root.</param>
public sealed record PinnedImage(string Repository, string Tag, string File)
{
    /// <summary><c>true</c> for the bootstrap pin that precedes an app's first release.</summary>
    public bool IsBootstrap => Tag == CodefreshPlatform.BootstrapTag;

    /// <summary><c>repository:tag</c>.</summary>
    public override string ToString() => $"{Repository}:{Tag}";
}

/// <summary>
/// Reads the pins under gitops/apps/*/envs/** the way codefresh/platform/scripts/registry-retention.ps1 does: Kustomize
/// <c>images[].name</c> with <c>newTag</c>, Helm <c>repository</c> with <c>tag</c>, and raw <c>image:</c> references.
/// Only repositories under <c>apps/</c> and <c>apps-previews/</c> count.
/// </summary>
public static partial class PinReader
{
    /// <summary>Reads every pin below <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The environment repository root.</param>
    public static IReadOnlyList<PinnedImage> Read(string repositoryRoot)
    {
        var apps = Path.Combine(repositoryRoot, "gitops", "apps");
        if (!Directory.Exists(apps))
        {
            return [];
        }

        var pins = new List<PinnedImage>();
        var files = Directory.EnumerateFiles(apps, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".yaml", StringComparison.Ordinal) || file.EndsWith(".yml", StringComparison.Ordinal))
            .Where(file => file.Replace('\\', '/').Contains("/envs/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            pins.AddRange(Parse(File.ReadAllLines(file), relative));
        }

        return pins.Distinct().ToArray();
    }

    /// <summary>Reads the pins of one file.</summary>
    /// <param name="lines">File content.</param>
    /// <param name="file">File name for the result.</param>
    public static IEnumerable<PinnedImage> Parse(IEnumerable<string> lines, string file)
    {
        string? pendingName = null;
        string? pendingRepository = null;
        foreach (var raw in lines)
        {
            var line = TrailingComment().Replace(raw, string.Empty);
            if (NameLine().Match(line) is { Success: true } name)
            {
                pendingName = RepositoryPath(name.Groups["value"].Value);
                continue;
            }

            if (NewTagLine().Match(line) is { Success: true } newTag && pendingName is not null)
            {
                yield return new PinnedImage(pendingName, newTag.Groups["value"].Value, file);
                pendingName = null;
                continue;
            }

            if (RepositoryLine().Match(line) is { Success: true } repository)
            {
                pendingRepository = RepositoryPath(repository.Groups["value"].Value);
                continue;
            }

            if (TagLine().Match(line) is { Success: true } tag && pendingRepository is not null)
            {
                yield return new PinnedImage(pendingRepository, tag.Groups["value"].Value, file);
                pendingRepository = null;
                continue;
            }

            if (ImageLine().Match(line) is { Success: true } image)
            {
                var reference = DigestSuffix().Replace(image.Groups["value"].Value, string.Empty);
                var path = RepositoryPath(reference);
                var separator = reference.LastIndexOf(':');
                if (path is not null && separator > reference.LastIndexOf('/'))
                {
                    yield return new PinnedImage(path, reference[(separator + 1)..], file);
                }
            }
        }
    }

    private static string? RepositoryPath(string reference)
    {
        var match = AppsRepository().Match(reference);
        return match.Success ? match.Groups["path"].Value : null;
    }

    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex TrailingComment();

    [GeneratedRegex(@"^\s*-?\s*name:\s*[""']?(?<value>[^""'\s]+)")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^\s*newTag:\s*[""']?(?<value>[^""'\s]+)")]
    private static partial Regex NewTagLine();

    [GeneratedRegex(@"^\s*repository:\s*[""']?(?<value>[^""'\s]+)")]
    private static partial Regex RepositoryLine();

    [GeneratedRegex(@"^\s*tag:\s*[""']?(?<value>[^""'\s]+)")]
    private static partial Regex TagLine();

    [GeneratedRegex(@"^\s*-?\s*image:\s*[""']?(?<value>[^""'\s]+)")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"@sha256:[0-9a-f]+$")]
    private static partial Regex DigestSuffix();

    [GeneratedRegex(@"(?:^|/)(?<path>apps(?:-previews)?/[^:@\s""']+)")]
    private static partial Regex AppsRepository();
}
