using System.Text;
using Madorin.AI.OpenAI.Image.Cli;

namespace Madorin.AI.OpenAI.Image;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new OpenAiImageApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}
