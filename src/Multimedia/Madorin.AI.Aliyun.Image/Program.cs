using System.Text;
using Madorin.AI.Aliyun.Image.Cli;

namespace Madorin.AI.Aliyun.Image;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var input = Console.IsInputRedirected ? Console.OpenStandardInput() : null;
        return await new AliyunImageApplication(client).RunAsync(args, Console.Out, Environment.CurrentDirectory, input);
    }
}

