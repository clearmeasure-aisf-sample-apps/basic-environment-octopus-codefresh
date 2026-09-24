using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Platform.Onboarding.Descriptors;

/// <summary>One schema violation: where in the descriptor, which keyword, and a readable message.</summary>
/// <param name="Pointer">JSON pointer of the offending value (<c>""</c> is the root).</param>
/// <param name="Keyword">The failing schema keyword, for example <c>pattern</c>.</param>
/// <param name="Message">Readable message.</param>
internal sealed record SchemaViolation(string Pointer, string Keyword, string Message);

/// <summary>
/// The descriptor schema <c>apps/schema.json</c> (JSON Schema 2020-12). It is the single source of the shape rules;
/// the tool never restates them in code.
/// </summary>
internal sealed class DescriptorSchema
{
    private readonly JsonSchema schema;

    private DescriptorSchema(JsonSchema schema)
    {
        this.schema = schema;
    }

    /// <summary>Loads the schema file.</summary>
    /// <param name="path">Path of <c>apps/schema.json</c>.</param>
    /// <exception cref="InvalidOperationException">The file is missing or is not a valid schema.</exception>
    public static DescriptorSchema Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"descriptor schema {path} does not exist");
        }

        try
        {
            // A private registry per load: the schema's $id may be built more than once in one process.
            var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
            return new DescriptorSchema(JsonSchema.FromText(File.ReadAllText(path), options));
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"descriptor schema {path} is not a valid JSON Schema: {ex.Message}", ex);
        }
    }

    /// <summary>Evaluates a descriptor and returns every violation, sorted by location.</summary>
    /// <param name="instance">The descriptor as JSON.</param>
    public IReadOnlyList<SchemaViolation> Evaluate(JsonNode? instance)
    {
        var element = JsonSerializer.SerializeToElement(instance);
        var results = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid)
        {
            return [];
        }

        var violations = new List<SchemaViolation>();
        foreach (var unit in FailingPath(results))
        {
            if (unit.Errors is null)
            {
                continue;
            }

            var pointer = unit.InstanceLocation.ToString();
            var evaluationPath = unit.EvaluationPath.ToString();
            foreach (var (keyword, message) in unit.Errors)
            {
                if (SummaryKeywords.Contains(keyword, StringComparer.Ordinal))
                {
                    continue;
                }

                violations.Add(new SchemaViolation(pointer, keyword, Describe(pointer, evaluationPath, keyword, message)));
            }
        }

        if (violations.Count == 0)
        {
            violations.Add(new SchemaViolation(string.Empty, "schema", "the descriptor does not match apps/schema.json"));
        }

        return violations
            .DistinctBy(violation => (violation.Pointer, violation.Message))
            .OrderBy(violation => violation.Pointer, StringComparer.Ordinal)
            .ToArray();
    }

    // Keywords whose error only summarizes failures that their child results name precisely.
    private static readonly string[] SummaryKeywords =
        ["properties", "additionalProperties", "patternProperties", "items", "prefixItems", "allOf", "$ref", "then", "else", "dependentSchemas"];

    /// <summary>
    /// The invalid results only, top-down: a valid node's children (for example the inner schema of a satisfied
    /// <c>not</c>, or the non-matching items of <c>contains</c>) never make the descriptor invalid, and an <c>if</c>
    /// condition only selects <c>then</c> or <c>else</c>.
    /// </summary>
    private static IEnumerable<EvaluationResults> FailingPath(EvaluationResults results)
    {
        if (results.IsValid || results.EvaluationPath.ToString().EndsWith("/if", StringComparison.Ordinal))
        {
            yield break;
        }

        yield return results;
        foreach (var detail in results.Details ?? [])
        {
            foreach (var nested in FailingPath(detail))
            {
                yield return nested;
            }
        }
    }

    private static string Describe(string pointer, string evaluationPath, string keyword, string message)
    {
        var key = pointer.Length == 0 ? "descriptor" : pointer;
        if (evaluationPath.EndsWith("/additionalProperties", StringComparison.Ordinal) || keyword == "false")
        {
            var name = pointer[(pointer.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            return $"unknown key '{name}' (apps/schema.json allows no other keys here)";
        }

        if (keyword == "not")
        {
            return $"{key}: value is not allowed here (a reserved word or a reserved name)";
        }

        return $"{key}: {message}";
    }
}
