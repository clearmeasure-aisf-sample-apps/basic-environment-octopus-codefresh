using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Platform.Conformance.Harness.Clients;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>An Octopus release as the handoff test reads it.</summary>
/// <param name="Id">Release ID.</param>
/// <param name="Version">Release version.</param>
/// <param name="Assembled">When it was created.</param>
/// <param name="ReleaseNotes">Release notes; the first line of a Codefresh handoff is <c>app-commit: &lt;sha&gt;</c>.</param>
/// <param name="PackageVersions">Versions of the selected images (the platform-wake release of step 0 is left out).</param>
public sealed record OctopusReleaseRecord(string Id, string Version, DateTimeOffset? Assembled, string ReleaseNotes, IReadOnlyList<string> PackageVersions)
{
    /// <summary><c>true</c> when the release notes name <paramref name="sha"/> as the app commit.</summary>
    /// <param name="sha">Commit SHA.</param>
    public bool IsFor(string sha) =>
        ReleaseNotes.Split('\n').Any(line => line.Trim().Equals($"app-commit: {sha}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Compact text for messages.</summary>
    public override string ToString() => $"{Version} ({Id}, assembled {Assembled:u})";
}

/// <summary>Raw Octopus reads of the handoff test: the releases of a project.</summary>
public sealed class OctopusReleases : IDisposable
{
    private readonly JsonRest rest;
    private readonly string spaceId;

    /// <summary>Creates the reader; the caller has checked the Octopus prerequisites.</summary>
    /// <param name="serverUrl">Octopus server URL.</param>
    /// <param name="spaceId">Space ID.</param>
    /// <param name="apiKey">API key; sent only as the <c>X-Octopus-ApiKey</c> header.</param>
    /// <param name="timeout">Timeout of one request.</param>
    public OctopusReleases(string serverUrl, string spaceId, string apiKey, TimeSpan timeout)
    {
        this.spaceId = spaceId;
        rest = new JsonRest(new Uri(serverUrl.TrimEnd('/') + "/api/"), "Octopus Deploy", timeout, headers => headers.Add(OctopusApi.ApiKeyHeader, apiKey));
    }

    /// <summary>
    /// The newest releases of a project, newest first. <see cref="OctopusReleaseRecord.PackageVersions"/> holds the image
    /// versions only: the platform-wake release that step 0 ("Wake environment", Deploy a Release) selects is left out.
    /// </summary>
    /// <param name="projectId">Project ID.</param>
    /// <param name="take">How many releases to read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<OctopusReleaseRecord>> ListAsync(string projectId, int take, CancellationToken cancellationToken)
    {
        var page = await rest.GetAsync($"{spaceId}/projects/{projectId}/releases?take={take}", cancellationToken).ConfigureAwait(false);
        return page is { } body
            ? JsonRead.Items(body, "Items").Select(item => new OctopusReleaseRecord(
                JsonRead.Text(item, "Id") ?? string.Empty,
                JsonRead.Text(item, "Version") ?? string.Empty,
                JsonRead.Date(item, "Assembled"),
                JsonRead.Text(item, "ReleaseNotes") ?? string.Empty,
                JsonRead.Items(item, "SelectedPackages")
                    .Where(package => !IsWakeStep(JsonRead.Text(package, "ActionName"), JsonRead.Text(package, "PackageReferenceName")))
                    .Select(package => JsonRead.Text(package, "Version") ?? string.Empty)
                    .ToArray())).ToArray()
            : [];
    }

    /// <summary>Disposes the HTTP client.</summary>
    public void Dispose() => rest.Dispose();

    private static bool IsWakeStep(string? actionName, string? packageReferenceName) =>
        string.Equals(actionName, "Wake environment", StringComparison.OrdinalIgnoreCase)
        || string.Equals(packageReferenceName, "platform-wake", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The signer of a cosign signature: the Fulcio certificate's identity and OIDC issuer.</summary>
/// <param name="Identities">Subject alternative names (URIs and e-mail addresses).</param>
/// <param name="Issuer">OIDC issuer (Fulcio extension 1.3.6.1.4.1.57264.1.8, else the older .1.1).</param>
public sealed record SigstoreSigner(IReadOnlyList<string> Identities, string? Issuer)
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string IssuerV2Oid = "1.3.6.1.4.1.57264.1.8";
    private const string IssuerV1Oid = "1.3.6.1.4.1.57264.1.1";

    /// <summary>Reads the signer from a PEM certificate.</summary>
    /// <param name="pem">The certificate of a keyless signature (layer annotation <c>dev.sigstore.cosign/certificate</c>).</param>
    public static SigstoreSigner FromPem(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        var identities = new List<string>();
        if (certificate.Extensions[SubjectAlternativeNameOid] is { } names)
        {
            var sequence = new AsnReader(names.RawData, AsnEncodingRules.DER).ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue is 1 or 6)
                {
                    identities.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }

        string? issuer = null;
        if (certificate.Extensions[IssuerV2Oid] is { } v2)
        {
            issuer = new AsnReader(v2.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.UTF8String);
        }
        else if (certificate.Extensions[IssuerV1Oid] is { } v1)
        {
            issuer = Encoding.UTF8.GetString(v1.RawData);
        }

        return new SigstoreSigner(identities, issuer);
    }

    /// <summary>The certificates in the layers of a cosign signature manifest.</summary>
    /// <param name="manifest">The manifest of tag <c>sha256-&lt;digest&gt;.sig</c>.</param>
    public static IReadOnlyList<string> Certificates(JsonElement manifest) =>
        JsonRead.Items(manifest, "layers")
            .Select(layer => JsonRead.Text(JsonRead.Path(layer, "annotations"), "dev.sigstore.cosign/certificate"))
            .OfType<string>()
            .ToArray();

    /// <summary>The predicate types in the layers of a cosign attestation manifest.</summary>
    /// <param name="manifest">The manifest of tag <c>sha256-&lt;digest&gt;.att</c>.</param>
    public static IReadOnlyList<string> PredicateTypes(JsonElement manifest) =>
        JsonRead.Items(manifest, "layers")
            .Select(layer => JsonRead.Text(JsonRead.Path(layer, "annotations"), "predicateType"))
            .OfType<string>()
            .ToArray();
}
