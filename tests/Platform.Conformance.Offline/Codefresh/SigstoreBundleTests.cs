using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Tests.Codefresh;

namespace Platform.Conformance.Offline.Codefresh;

/// <summary>
/// CAP-CF-006 (offline half): the release test reads the signer of a cosign 2.x signature stored as a Sigstore bundle
/// referrer, with a self-signed stand-in for the Fulcio certificate (SAN URI and the OIDC issuer extension).
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class SigstoreBundleTests
{
    private const string Identity = "https://g.codefresh.io/build/6ab5a1640f2777f42e5e10b3";

    [Test]
    [Capability("CAP-CF-006")]
    public void Should_ReadSignerFromBundle_CertificateOrChain_YieldIdentityAndIssuer()
    {
        var raw = Convert.ToBase64String(FulcioLikeCertificate());
        using var v3 = JsonDocument.Parse("""{"verificationMaterial":{"certificate":{"rawBytes":"RAW"}}}""".Replace("RAW", raw, StringComparison.Ordinal));
        using var chain = JsonDocument.Parse("""{"verificationMaterial":{"x509CertificateChain":{"certificates":[{"rawBytes":"RAW"}]}}}""".Replace("RAW", raw, StringComparison.Ordinal));
        using var keyOnly = JsonDocument.Parse("""{"verificationMaterial":{"publicKey":{"hint":"abc"}}}""");

        var fromCertificate = SigstoreSigner.FromBundle(v3.RootElement);
        var fromChain = SigstoreSigner.FromBundle(chain.RootElement);

        fromCertificate.ShouldNotBeNull();
        fromCertificate.Identities.ShouldContain(Identity);
        fromCertificate.Issuer.ShouldBe(CodefreshPlatform.SignatureIssuer);
        fromChain.ShouldNotBeNull();
        fromChain.Identities.ShouldBe(fromCertificate.Identities);
        SigstoreSigner.FromBundle(keyOnly.RootElement).ShouldBeNull("a key-based signature has no signer identity");
    }

    [Test]
    [Capability("CAP-CF-006")]
    public void Should_ClassifyReferrers_BundleArtifactTypes_OnlySignaturesCount()
    {
        using var index = JsonDocument.Parse($$$"""
            {"manifests":[
              {"artifactType":"{{{SigstoreSigner.BundleArtifactType}}}","annotations":{"dev.sigstore.bundle.predicateType":"{{{SigstoreSigner.SignaturePredicateType}}}"}},
              {"artifactType":"{{{SigstoreSigner.BundleArtifactType}}}","annotations":{"dev.sigstore.bundle.predicateType":"https://spdx.dev/Document"}},
              {"artifactType":"{{{SigstoreSigner.BundleArtifactType}}}"},
              {"artifactType":"application/vnd.dev.cosign.artifact.sig.v1+json"}
            ]}
            """);

        var verdicts = index.RootElement.GetProperty("manifests").EnumerateArray().Select(SigstoreSigner.IsSignatureBundle).ToArray();

        verdicts.ShouldBe([true, false, true, false]);
    }

    private static byte[] FulcioLikeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=sigstore-intermediate", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddUri(new Uri(Identity));
        request.CertificateExtensions.Add(names.Build(critical: true));
        var issuer = new AsnWriter(AsnEncodingRules.DER);
        issuer.WriteCharacterString(UniversalTagNumber.UTF8String, CodefreshPlatform.SignatureIssuer);
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.57264.1.8", issuer.Encode(), critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        return certificate.RawData;
    }
}
