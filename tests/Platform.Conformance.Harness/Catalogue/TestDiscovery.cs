using System.Reflection;

namespace Platform.Conformance.Harness.Catalogue;

/// <summary>An NUnit test method found by reflection, with its capability IDs and categories.</summary>
/// <param name="FullName">Fully qualified name without arguments: <c>Namespace.Class.Method</c> (nested classes use <c>+</c>).</param>
/// <param name="Assembly">Simple name of the assembly that holds it.</param>
/// <param name="CapabilityIds">IDs of its <c>[Capability]</c> attributes, sorted and distinct.</param>
/// <param name="Categories">NUnit categories from the method, its class hierarchy and its assembly, sorted and distinct.</param>
public sealed record DiscoveredTest(string FullName, string Assembly, IReadOnlyList<string> CapabilityIds, IReadOnlyList<string> Categories)
{
    /// <summary><c>true</c> when the test carries <paramref name="category"/> (case-insensitive, as NUnit filters are).</summary>
    /// <param name="category">Category name, for example <see cref="Harness.Categories.Live"/>.</param>
    public bool HasCategory(string category) => Categories.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>The full name.</summary>
    public override string ToString() => FullName;
}

/// <summary>
/// Finds NUnit test methods (<c>[Test]</c>, <c>[TestCase]</c>, <c>[TestCaseSource]</c>, <c>[Theory]</c> and any other
/// test builder) and reads their <c>[Capability]</c> and <c>[Category]</c> attributes. Attributes are matched by type
/// name, so assemblies loaded into another load context work too.
/// </summary>
public static class TestDiscovery
{
    private const string CapabilityAttributeName = "Platform.Conformance.Harness.CapabilityAttribute";
    private const string CategoryAttributeName = "NUnit.Framework.CategoryAttribute";
    private const BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly string[] TestBuilderInterfaces = ["NUnit.Framework.Interfaces.ITestBuilder", "NUnit.Framework.Interfaces.ISimpleTestBuilder"];

    /// <summary>Discovers every test method in <paramref name="assemblies"/>, sorted by full name.</summary>
    /// <param name="assemblies">Test assemblies.</param>
    /// <returns>One entry per test method; parameterized cases of one method are one entry.</returns>
    public static IReadOnlyList<DiscoveredTest> Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var tests = new Dictionary<string, DiscoveredTest>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "unknown";
            var assemblyCategories = ReadStrings(assembly.GetCustomAttributes(inherit: true), CategoryAttributeName, "Name");
            foreach (var type in LoadableTypes(assembly).Where(IsFixtureCandidate))
            {
                var typeCategories = ReadStrings(type.GetCustomAttributes(inherit: true), CategoryAttributeName, "Name");
                foreach (var method in type.GetMethods(MethodFlags))
                {
                    var attributes = method.GetCustomAttributes(inherit: true);
                    if (!attributes.Any(IsTestBuilder))
                    {
                        continue;
                    }

                    var fullName = $"{type.FullName}.{method.Name}";
                    var capabilities = ReadStrings(attributes, CapabilityAttributeName, "Id");
                    var categories = ReadStrings(attributes, CategoryAttributeName, "Name").Concat(typeCategories).Concat(assemblyCategories);
                    if (tests.TryGetValue(fullName, out var existing))
                    {
                        capabilities = capabilities.Concat(existing.CapabilityIds).ToList();
                        categories = categories.Concat(existing.Categories);
                    }

                    tests[fullName] = new DiscoveredTest(fullName, assemblyName, SortedDistinct(capabilities, StringComparer.Ordinal), SortedDistinct(categories, StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        return tests.Values.OrderBy(test => test.FullName, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static bool IsFixtureCandidate(Type type) =>
        type.IsClass
        && (!type.IsAbstract || type.IsSealed)
        && !type.ContainsGenericParameters
        && type.FullName is not null
        && !type.FullName.Contains('<', StringComparison.Ordinal);

    private static bool IsTestBuilder(object attribute) =>
        attribute.GetType().GetInterfaces().Any(contract => TestBuilderInterfaces.Contains(contract.FullName, StringComparer.Ordinal));

    private static List<string> ReadStrings(IEnumerable<object> attributes, string attributeTypeName, string propertyName)
    {
        var values = new List<string>();
        foreach (var attribute in attributes)
        {
            if (!IsOrDerivesFrom(attribute.GetType(), attributeTypeName))
            {
                continue;
            }

            if (attribute.GetType().GetProperty(propertyName)?.GetValue(attribute) is string { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool IsOrDerivesFrom(Type? type, string fullName)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SortedDistinct(IEnumerable<string> values, StringComparer comparer) =>
        values.Distinct(comparer).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}

/// <summary>The two test assemblies of the conformance suite.</summary>
public static class ConformanceAssemblies
{
    /// <summary>Simple names of the test assemblies whose tests must each prove a catalogue capability.</summary>
    public static IReadOnlyList<string> Names { get; } = ["Platform.Conformance.Offline", "Platform.Conformance.Tests"];

    /// <summary>Loads every assembly in <see cref="Names"/> into the default load context.</summary>
    /// <exception cref="FileNotFoundException">An assembly is not next to the caller (the Offline project references the Tests project for this reason).</exception>
    public static IReadOnlyList<Assembly> Load() => Names.Select(name => Assembly.Load(new AssemblyName(name))).ToArray();
}
