using System.Text;
using Madorin.AI.Zhipu.Image.Cli;

namespace Madorin.AI.Zhipu.Image;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new ZhipuImageApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}
