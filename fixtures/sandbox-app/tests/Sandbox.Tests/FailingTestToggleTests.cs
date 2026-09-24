namespace Sandbox.Tests;

/// <summary>
/// The failing-test toggle of the conformance fixture (CAP-CF-004): a commit that adds the marker file
/// <c>toggles/failing-test</c> makes this test, and with it the CI gate, fail.
/// </summary>
[TestFixture]
public sealed class FailingTestToggleTests
{
    [Test]
    public void When_FailingTestToggleIsOff_ThenTheBuildPasses()
    {
        var marker = Path.Combine(RepositoryRoot(), "toggles", "failing-test");

        File.Exists(marker).ShouldBeFalse($"The failing-test toggle is on ({marker}); this failure is intended.");
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Sandbox.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("Sandbox.sln not found above " + AppContext.BaseDirectory);
    }
}
