namespace Madorin.AI.Runtime.MConsole.Tests.TestSupport;

internal sealed class OwnedTestDirectory : IDisposable
{
    private const string SentinelName = ".mconsole-owned";

    public OwnedTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"mconsole-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, SentinelName), string.Empty);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (File.Exists(System.IO.Path.Combine(Path, SentinelName)))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
