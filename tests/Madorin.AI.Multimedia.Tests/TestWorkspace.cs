namespace Madorin.AI.Multimedia.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "multimedia-tests-" + Guid.NewGuid().ToString("N"));

    public TestWorkspace() => Directory.CreateDirectory(Root);

    public void Dispose() => Directory.Delete(Root, true);
}
