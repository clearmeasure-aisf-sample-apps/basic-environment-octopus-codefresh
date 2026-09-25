using Platform.Conformance.Harness;

namespace Platform.Conformance.Tests.Codefresh;

/// <summary>
/// CAP-CF-006: release images land under <c>apps/&lt;app&gt;/</c> with a keyless signature and an SBOM. Observed on the
/// images of the sandbox release commit that platform-env/conformance-arm pushed (tag <c>sha-&lt;sha7&gt;</c>; by hand,
/// the newest successful sandbox/release build): a cosign signature carries a Fulcio certificate whose identity is a
/// sandbox release pipeline and whose issuer is the Codefresh OIDC provider, and an attestation holds an SPDX SBOM. Both
/// storage schemes count: the tags <c>sha256-&lt;digest&gt;.sig</c> and <c>.att</c>, and OCI 1.1 referrers. The build
/// step's cosign 2.x writes the signature as a Sigstore bundle referrer
/// (<c>application/vnd.dev.sigstore.bundle.v0.3+json</c>, predicate <c>https://sigstore.dev/cosign/sign/v1</c>) and the
/// SBOM attestation as an <c>.att</c> tag (verified on the registry, 2026-09-25).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class ReleasePublishTests : CodefreshCapabilityTestBase
{
    private const string SignatureType = "application/vnd.dev.cosign.artifact.sig.v1+json";
    private static readonly string[] Images = ["web", "migrator"];

    /// <summary>Both sandbox images are signed by a sandbox release pipeline and carry an SBOM.</summary>
    [Test]
    [Capability("CAP-CF-006")]
    [Category(Categories.Build)]
    [CancelAfter(75 * 60 * 1000)]
    public async Task Should_GetManifestAsync_ReleaseImages_SignedWithSbomUnderTheAppPath()
    {
        var registry = RequireRegistry("the release publish test");
        var sha = await ReleaseCommitAsync();
        var problems = new List<string>();

        foreach (var image in Images)
        {
            problems.AddRange(await CheckImageAsync(registry, $"apps/{CodefreshPlatform.Sandbox}/{image}", $"sha-{CodefreshPlatform.Short(sha)}"));
        }

        problems.ShouldBeEmpty($"release images of {sha}: {string.Join("; ", problems)}");
    }

    private async Task<string> ReleaseCommitAsync()
    {
        if (CodefreshPlatform.Variable(CodefreshPlatform.ReleaseShaVariable) is { } sha)
        {
            var build = await FinishedBuildForAsync(CodefreshPlatform.SandboxRelease, sha);
            build.IsTerminal.ShouldBeTrue($"sandbox/release build {build}");
            return sha;
        }

        var newest = (await RequireCodefresh("the release publish test").ListBuildsAsync(CodefreshPlatform.SandboxRelease, 20, Token))
            .FirstOrDefault(build => build.Status == "success" && build.Revision is not null);
        return newest?.Revision ?? throw new PlatformPrerequisiteException("Prerequisites missing for the release publish test: no successful sandbox/release build yet (P1-11).");
    }

    private static async Task<IReadOnlyList<string>> CheckImageAsync(RegistryReader registry, string repository, string tag)
    {
        var tags = await registry.ListTagsAsync(repository, Token);
        var image = tags.FirstOrDefault(candidate => candidate.Name == tag);
        if (image is null)
        {
            return [$"{repository}:{tag} does not exist"];
        }

        var problems = new List<string>();
        var subject = image.Digest.Replace("sha256:", "sha256-", StringComparison.Ordinal);
        var referrers = await registry.ListReferrersAsync(repository, image.Digest, Token);
        var signature = tags.Any(candidate => candidate.Name == $"{subject}.sig")
            ? await registry.GetManifestAsync(repository, $"{subject}.sig", Token)
            : null;
        var signers = (signature is { } manifest ? SigstoreSigner.Certificates(manifest) : []).Select(SigstoreSigner.FromPem).ToList();
        signers.AddRange(await BundleSignersAsync(registry, repository, referrers));
        var legacyReferrer = referrers.Any(referrer => JsonRead.Text(referrer, "artifactType") == SignatureType);
        if (signers.Count == 0 && !legacyReferrer)
        {
            problems.Add($"{repository}:{tag} ({image.Digest}) has no cosign signature");
        }

        foreach (var signer in signers)
        {
            if (signer.Issuer != CodefreshPlatform.SignatureIssuer || !signer.Identities.Any(identity => CodefreshPlatform.SandboxSignerIdentity().IsMatch(identity)))
            {
                problems.Add($"{repository}:{tag} is signed by {string.Join(", ", signer.Identities)} (issuer {signer.Issuer}), not a sandbox release pipeline");
            }
        }

        var attestation = tags.Any(candidate => candidate.Name == $"{subject}.att")
            ? await registry.GetManifestAsync(repository, $"{subject}.att", Token)
            : null;
        var predicates = attestation is { } attestations ? SigstoreSigner.PredicateTypes(attestations) : [];
        var referredSbom = referrers.Any(referrer => (JsonRead.Text(referrer, "artifactType") ?? string.Empty).Contains("spdx", StringComparison.OrdinalIgnoreCase));
        if (!predicates.Any(predicate => predicate.Contains("spdx", StringComparison.OrdinalIgnoreCase)) && !referredSbom)
        {
            problems.Add($"{repository}:{tag} ({image.Digest}) has no SPDX SBOM attestation; predicates: {string.Join(", ", predicates)}");
        }

        return problems;
    }

    private static async Task<IReadOnlyList<SigstoreSigner>> BundleSignersAsync(RegistryReader registry, string repository, IReadOnlyList<System.Text.Json.JsonElement> referrers)
    {
        var signers = new List<SigstoreSigner>();
        foreach (var referrer in referrers.Where(SigstoreSigner.IsSignatureBundle))
        {
            var digest = JsonRead.Text(referrer, "digest");
            if (digest is null || await registry.GetManifestAsync(repository, digest, Token) is not { } manifest)
            {
                continue;
            }

            foreach (var layer in JsonRead.Items(manifest, "layers"))
            {
                if (JsonRead.Text(layer, "digest") is { } blob
                    && await registry.GetJsonBlobAsync(repository, blob, Token) is { } bundle
                    && SigstoreSigner.FromBundle(bundle) is { } signer)
                {
                    signers.Add(signer);
                }
            }
        }

        return signers;
    }
}
