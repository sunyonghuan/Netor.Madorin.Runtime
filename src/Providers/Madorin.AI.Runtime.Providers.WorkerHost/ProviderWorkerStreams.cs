namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>Binds stdin/stdout for IPC and silences Console.Out so SDKs cannot corrupt frames.</summary>
public static class ProviderWorkerStreams
{
    public static (Stream Stdin, Stream Stdout, TextWriter Stderr) Bind()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        return (stdin, stdout, Console.Error);
    }
}
