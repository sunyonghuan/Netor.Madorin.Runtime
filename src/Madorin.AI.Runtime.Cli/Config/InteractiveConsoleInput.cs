namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>判断交互输入里哪些 Enter 应该发送，哪些只是换行。</summary>
internal static class InteractiveConsoleInput
{
    /// <summary>连续按键间隔短于该值时，把 Enter 视为粘贴进来的换行。</summary>
    public const int PasteBurstMilliseconds = 40;

    public static bool IsSubmitEnter(bool shift, bool moreKeysQueued, long elapsedMilliseconds) =>
        !shift && !moreKeysQueued && elapsedMilliseconds >= PasteBurstMilliseconds;

    public static int DisplayWidth(string value)
    {
        var width = 0;
        foreach (var character in value)
        {
            width += DisplayWidth(character);
        }

        return width;
    }

    public static int DisplayWidth(char value) => value < 0x1100 ? 1 : 2;
}
