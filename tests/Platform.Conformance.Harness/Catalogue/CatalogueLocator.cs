using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness.Catalogue;

/// <summary>Finds and loads the catalogue that the consistency check and the report tool use.</summary>
public static class CatalogueLocator
{
    /// <summary>
    /// Loads the file named by <c>PLATFORM_CATALOGUE_FILE</c> when it is set; otherwise merges the catalogue folder
    /// (<see cref="CapabilityCatalogue.LoadDirectory"/>) of the repository root found above <paramref name="startDirectory"/>.
    /// </summary>
    /// <param name="environment">Environment variables; the process environment when omitted.</param>
    /// <param name="startDirectory">Where to start looking for the repository root; the test assembly folder when omitted.</param>
    public static CapabilityCatalogue Load(IEnvironmentVariables? environment = null, string? startDirectory = null)
    {
        var variables = environment ?? ProcessEnvironmentVariables.Instance;
        var file = variables.Get(EnvironmentVariableNames.CatalogueFile);
        if (!string.IsNullOrWhiteSpace(file))
        {
            return CapabilityCatalogue.Load(Path.GetFullPath(file.Trim()));
        }

        return CapabilityCatalogue.LoadDirectory(RepositoryRoot.Find(startDirectory ?? AppContext.BaseDirectory, variables));
    }
}
