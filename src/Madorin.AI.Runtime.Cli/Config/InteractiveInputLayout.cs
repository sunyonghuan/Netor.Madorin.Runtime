using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>输入区里的一行屏幕内容。提示符和正文分开，方便上色。</summary>
internal readonly record struct InteractiveVisualRow(string PromptText, string UserText);

/// <summary>把逻辑行折成屏幕行，并给出光标的屏幕位置。</summary>
internal readonly record struct InteractiveInputLayout(
    IReadOnlyList<InteractiveVisualRow> Rows,
    int CursorRow,
    int CursorColumn)
{
    /// <summary>计算重画结果。最后一列留空，避免写满后控制台自动换行。</summary>
    public static InteractiveInputLayout Build(
        IReadOnlyList<string> lines,
        int cursorRow,
        int cursorColumn,
        string marker,
        int bufferWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        marker ??= string.Empty;
        var usable = Math.Max(1, bufferWidth - 1);
        var source = lines.Count == 0 ? (IReadOnlyList<string>)[string.Empty] : lines;
        var activeRow = Math.Clamp(cursorRow, 0, source.Count - 1);
        var rows = new List<InteractiveVisualRow>();
        var located = false;
        var foundRow = 0;
        var foundColumn = 0;

        for (var index = 0; index < source.Count; index++)
        {
            var text = source[index] ?? string.Empty;
            var column = index == activeRow ? Math.Clamp(cursorColumn, 0, text.Length) : 0;
            var target = index == activeRow
                ? InteractiveConsoleInput.DisplayWidth(marker) + InteractiveConsoleInput.DisplayWidth(text[..column])
                : -1;
            AppendLine(rows, marker, text, usable, target, ref located, ref foundRow, ref foundColumn);
        }

        if (rows.Count == 0)
        {
            rows.Add(new InteractiveVisualRow(string.Empty, string.Empty));
        }

        if (!located)
        {
            var last = rows[^1];
            foundRow = rows.Count - 1;
            foundColumn = InteractiveConsoleInput.DisplayWidth(last.PromptText)
                + InteractiveConsoleInput.DisplayWidth(last.UserText);
        }

        return new InteractiveInputLayout(rows, foundRow, foundColumn);
    }

    private static void AppendLine(
        List<InteractiveVisualRow> rows,
        string marker,
        string text,
        int usable,
        int target,
        ref bool located,
        ref int foundRow,
        ref int foundColumn)
    {
        var prompt = new StringBuilder();
        var user = new StringBuilder();
        var visualColumn = 0;
        var displayIndex = 0;
        var locatedHere = located;
        var foundRowHere = foundRow;
        var foundColumnHere = foundColumn;

        void NoteCursor()
        {
            if (locatedHere || target < 0 || displayIndex != target)
            {
                return;
            }

            foundRowHere = rows.Count;
            foundColumnHere = visualColumn;
            locatedHere = true;
        }

        void Flush()
        {
            rows.Add(new InteractiveVisualRow(prompt.ToString(), user.ToString()));
            prompt.Clear();
            user.Clear();
            visualColumn = 0;
        }

        void Append(char value, bool isPrompt)
        {
            NoteCursor();
            var width = InteractiveConsoleInput.DisplayWidth(value);
            if (visualColumn > 0 && visualColumn + width > usable)
            {
                Flush();
                NoteCursor();
            }

            if (isPrompt)
            {
                prompt.Append(value);
            }
            else
            {
                user.Append(value);
            }

            visualColumn += width;
            displayIndex += width;
        }

        foreach (var value in marker)
        {
            Append(value, isPrompt: true);
        }

        foreach (var value in text)
        {
            Append(value, isPrompt: false);
        }

        NoteCursor();
        Flush();
        located = locatedHere;
        foundRow = foundRowHere;
        foundColumn = foundColumnHere;
    }
}
