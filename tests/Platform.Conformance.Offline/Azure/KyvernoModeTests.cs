using Platform.Conformance.Harness;
using Platform.Conformance.Tests.GitOps;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Offline.Azure;

/// <summary>
/// CAP-AZ-018: the mode of every Kyverno policy per tier, resolved from <c>policies/kyverno/base</c> and the patches of
/// <c>policies/kyverno/overlays/&lt;tier&gt;</c> the way kustomize applies them (target kind and optional name, then
/// <c>replace</c> of <c>/spec/validationActions</c> and <c>/spec/failurePolicy</c>), plus the per-app signer mode of the
/// tenant chart (<c>gitops/platform/tenant/values-&lt;tier&gt;.yaml</c>). Nonprod audits everything, the workload
/// baseline included (P2, ADR-D11); prod enforces the signer and registry-path rules (P1) and fails closed.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class KyvernoModeTests
{
    /// <summary>The four policies of <c>workload-baseline.yaml</c> (ADR-D12).</summary>
    private static readonly string[] WorkloadBaseline = ["disallow-latest-tag", "require-resources", "require-probes", "disallow-privileged"];

    /// <summary>Base policies that report only, in every tier.</summary>
    private static readonly string[] AuditEverywhere = ["audit-app-release-provenance"];

    [Test]
    [Capability("CAP-AZ-018")]
    public void Should_ResolveNonprodOverlay_EveryPolicy_AuditsAndIgnoresEngineFailures()
    {
        var modes = Resolve("nonprod");

        modes.Keys.ShouldContain("verify-app-release-signatures");
        modes.Keys.ShouldContain("restrict-app-image-paths");
        modes.Keys.ShouldContain("require-mssql-express");
        foreach (var name in WorkloadBaseline)
        {
            modes.Keys.ShouldContain(name, $"{name} is part of the workload baseline");
        }

        var denying = modes.Where(pair => !pair.Value.IsAudit).Select(pair => $"{pair.Key}: [{string.Join(", ", pair.Value.Actions)}] {pair.Value.FailurePolicy}").ToList();
        denying.ShouldBeEmpty("nonprod reports and never blocks (overlays/nonprod, ADR-D11)");
    }

    [Test]
    [Capability("CAP-AZ-018")]
    public void Should_ResolveProdOverlay_SignerRegistryPathEditionAndBaseline_DenyAndFailClosed()
    {
        var modes = Resolve("prod");

        var open = modes
            .Where(pair => !AuditEverywhere.Contains(pair.Key, StringComparer.Ordinal) && !pair.Value.IsEnforce)
            .Select(pair => $"{pair.Key}: [{string.Join(", ", pair.Value.Actions)}] {pair.Value.FailurePolicy}")
            .ToList();

        open.ShouldBeEmpty("prod enforces from P1 (overlays/prod, CAP-AZ-001 to CAP-AZ-003)");
        modes["verify-app-release-signatures"].IsEnforce.ShouldBeTrue();
        modes["restrict-app-image-paths"].IsEnforce.ShouldBeTrue();
        modes["audit-app-release-provenance"].IsAudit.ShouldBeTrue("provenance stays in Audit in every tier");
    }

    [Test]
    [Capability("CAP-AZ-018")]
    public void Should_ReadOverlayLabels_ModeLabel_MatchesTheResolvedMode()
    {
        OverlayModeLabel("nonprod").ShouldBe("Audit");
        OverlayModeLabel("prod").ShouldBe("Enforce");
    }

    [Test]
    [Capability("CAP-AZ-018")]
    public void Should_ReadTenantValues_PerAppSignerPolicy_AuditsInNonprodAndDeniesInProd()
    {
        SignerActions("nonprod").ShouldBe(["Audit"]);
        SignerActions("prod").ShouldBe(["Deny"]);
    }

    [Test]
    [Capability("CAP-AZ-018")]
    public void Should_ReadBasePolicies_WorkloadBaseline_MatchesAppNamespacesOnly()
    {
        var policies = BasePolicies().Where(policy => WorkloadBaseline.Contains(policy.Name, StringComparer.Ordinal)).ToList();

        policies.Select(policy => policy.Name).ShouldBe(WorkloadBaseline, ignoreOrder: true);
        foreach (var policy in policies)
        {
            var selector = Path(policy.Document, "spec", "matchConstraints", "namespaceSelector", "matchLabels") as YamlMappingNode;
            selector.ShouldNotBeNull($"{policy.Name} selects namespaces by label");
            selector.Children.Count.ShouldBe(1, $"{policy.Name} selects tier=app only");
            Scalar(selector, "tier").ShouldBe("app", $"{policy.Name} never matches platform namespaces");
        }
    }

    /// <summary>Effective mode of every policy of the base, after the overlay patches of one tier.</summary>
    private static Dictionary<string, PolicyMode> Resolve(string tier)
    {
        var modes = BasePolicies().ToDictionary(policy => policy.Name, policy => new PolicyMode(policy.Kind, Actions(policy.Document), Scalar(Path(policy.Document, "spec"), "failurePolicy") ?? string.Empty), StringComparer.Ordinal);
        var overlay = LoadDocuments(System.IO.Path.Combine(Root, "policies", "kyverno", "overlays", tier, "kustomization.yaml")).Single();
        if (Path(overlay, "patches") is not YamlSequenceNode patches)
        {
            return modes;
        }

        foreach (var patch in patches.Children.OfType<YamlMappingNode>())
        {
            var target = Path(patch, "target");
            var kind = Scalar(target, "kind");
            var name = Scalar(target, "name");
            var operations = ParseOperations(Scalar(patch, "patch") ?? string.Empty);
            foreach (var key in modes.Keys.ToList())
            {
                var mode = modes[key];
                if ((kind is not null && kind != mode.Kind) || (name is not null && name != key))
                {
                    continue;
                }

                foreach (var operation in operations.Where(candidate => Scalar(candidate, "op") == "replace"))
                {
                    var value = Path(operation, "value");
                    mode = Scalar(operation, "path") switch
                    {
                        "/spec/validationActions" => mode with { Actions = Strings(value) },
                        "/spec/failurePolicy" => mode with { FailurePolicy = (value as YamlScalarNode)?.Value ?? string.Empty },
                        _ => mode,
                    };
                }

                modes[key] = mode;
            }
        }

        return modes;
    }

    private static IEnumerable<(string Kind, string Name, YamlMappingNode Document)> BasePolicies()
    {
        var baseFolder = System.IO.Path.Combine(Root, "policies", "kyverno", "base");
        var kustomization = LoadDocuments(System.IO.Path.Combine(baseFolder, "kustomization.yaml")).Single();
        foreach (var resource in Strings(Path(kustomization, "resources")))
        {
            foreach (var document in LoadDocuments(System.IO.Path.Combine(baseFolder, resource)))
            {
                var kind = Scalar(document, "kind");
                if (kind is "ValidatingPolicy" or "ImageValidatingPolicy")
                {
                    yield return (kind, Scalar(Path(document, "metadata"), "name") ?? string.Empty, document);
                }
            }
        }
    }

    private static string? OverlayModeLabel(string tier)
    {
        var overlay = LoadDocuments(System.IO.Path.Combine(Root, "policies", "kyverno", "overlays", tier, "kustomization.yaml")).Single();
        return (Path(overlay, "labels") as YamlSequenceNode)?.Children
            .Select(label => Scalar(Path(label, "pairs"), "policies.platform/mode"))
            .FirstOrDefault(value => value is not null);
    }

    private static IReadOnlyList<string> SignerActions(string tier)
    {
        var values = LoadDocuments(System.IO.Path.Combine(GitOpsRepository.TenantChart, $"values-{tier}.yaml")).Single();
        return Strings(Path(values, "platform", "signer", "validationActions"));
    }

    private static IReadOnlyList<string> Actions(YamlNode document) => Strings(Path(document, "spec", "validationActions"));

    private static List<YamlMappingNode> ParseOperations(string patch)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(patch));
        return stream.Documents.Count == 0 ? [] : ((YamlSequenceNode)stream.Documents[0].RootNode).Children.OfType<YamlMappingNode>().ToList();
    }

    private static List<YamlMappingNode> LoadDocuments(string path)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(path);
        stream.Load(reader);
        return stream.Documents.Select(document => document.RootNode).OfType<YamlMappingNode>().ToList();
    }

    private static YamlNode? Path(YamlNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (node is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode(key), out node))
            {
                return null;
            }
        }

        return node;
    }

    private static string? Scalar(YamlNode? node, string key) => (Path(node, key) as YamlScalarNode)?.Value;

    private static List<string> Strings(YamlNode? node) =>
        node is YamlSequenceNode sequence ? sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? string.Empty).ToList() : [];

    private static string Root => GitOpsRepository.Root;

    /// <summary>Effective admission mode of one policy.</summary>
    private sealed record PolicyMode(string Kind, IReadOnlyList<string> Actions, string FailurePolicy)
    {
        public bool IsAudit => Actions.SequenceEqual(["Audit"]) && FailurePolicy == "Ignore";

        public bool IsEnforce => Actions.Contains("Deny") && FailurePolicy == "Fail";
    }
}
