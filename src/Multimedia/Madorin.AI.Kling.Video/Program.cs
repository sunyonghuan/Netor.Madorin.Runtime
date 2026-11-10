using System.Text;
using Madorin.AI.Kling.Video.Cli;

namespace Madorin.AI.Kling.Video;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new KlingVideoApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}
