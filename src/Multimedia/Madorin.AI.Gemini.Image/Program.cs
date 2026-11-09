using System.Text;
using Madorin.AI.Gemini.Image.Cli;

namespace Madorin.AI.Gemini.Image;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new GeminiImageApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}
