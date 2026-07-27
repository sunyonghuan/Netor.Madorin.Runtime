using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Tests.Hosting;

[TestClass]
public sealed class MadorinLocatorTests
{
    [TestMethod]
    public void GetCandidates_ExcludesNativeAotIntermediateDirectory()
    {
        var root = FindRepositoryRoot();

        var candidates = MadorinLocator.GetCandidates(null, root);

        Assert.IsFalse(candidates.Any(candidate => candidate.Path.Contains(
            $"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(candidates.Any(candidate => candidate.Source
            is ExecutableLocationSource.RepositoryBuildOutput));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Madorin.AI.Runtime.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
