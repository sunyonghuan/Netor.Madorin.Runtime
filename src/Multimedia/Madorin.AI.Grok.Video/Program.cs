using System.Text;
using Madorin.AI.Grok.Video.Cli;

namespace Madorin.AI.Grok.Video;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new GrokVideoApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}
