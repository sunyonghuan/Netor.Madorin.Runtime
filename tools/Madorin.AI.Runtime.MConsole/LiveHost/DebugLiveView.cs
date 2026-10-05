namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>
/// 在一块固定区域里同时刷新输出和事件。区域位置在一轮开始时确定，后续只覆盖这块区域。
/// </summary>
public static class DebugLiveView
{
    public static int Begin(int blockHeight)
    {
        if (Console.IsOutputRedirected)
        {
            return -1;
        }

        var height = Math.Max(1, blockHeight);
        for (var index = 0; index < height; index++)
        {
            Console.WriteLine();
        }

        var origin = Console.CursorTop - height;
        return origin < 0 ? 0 : origin;
    }

    public static void Render(int origin, DebugFrame frame)
    {
        if (origin < 0 || Console.IsOutputRedirected)
        {
            return;
        }

        var width = Math.Max(20, Console.WindowWidth - 1);
        var top = origin;
        try
        {
            WriteAt(top++, width, "════════ 输出 ════════");
            foreach (var line in frame.OutputLines)
            {
                WriteAt(top++, width, line);
            }

            WriteAt(top++, width, "════════ 事件 ════════");
            foreach (var line in frame.EventLines)
            {
                WriteAt(top++, width, line);
            }

            WriteAt(top, width, frame.Status);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 窗口在刷新过程中变小，下一帧再画。
        }
    }

    public static int OutputRows(int windowHeight) => Math.Max(6, (windowHeight - 8) * 2 / 3);

    public static int EventRows(int windowHeight) => Math.Max(4, windowHeight - 8 - OutputRows(windowHeight));

    private static void WriteAt(int row, int width, string text)
    {
        Console.SetCursorPosition(0, row);
        var value = text.Length > width ? text[..width] : text;
        Console.Write(value.PadRight(width));
    }
}
