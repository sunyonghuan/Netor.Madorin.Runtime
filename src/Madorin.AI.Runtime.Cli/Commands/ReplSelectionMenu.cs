using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplSelectionMenu
{
    public static async ValueTask<string?> PromptAsync(
        ICliTerminal terminal,
        string title,
        string itemKind,
        IReadOnlyList<(string Id, string? Description)> items,
        string currentId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(items);

        terminal.WriteLine(title, CliTextStyle.Title);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var isCurrent = item.Id.Equals(currentId, StringComparison.Ordinal);
            terminal.Write($"  {index + 1}. ", CliTextStyle.Parameter);
            terminal.Write(
                item.Id,
                isCurrent ? CliTextStyle.Selected : CliTextStyle.Default);
            if (!string.IsNullOrWhiteSpace(item.Description)
                && !item.Description.Equals(item.Id, StringComparison.Ordinal))
            {
                terminal.Write($" - {item.Description}", CliTextStyle.Muted);
            }

            if (isCurrent)
            {
                terminal.Write(" (current)", CliTextStyle.Muted);
            }

            terminal.WriteLine();
        }

        terminal.Write($"Select {itemKind} by ", CliTextStyle.Prompt);
        terminal.Write("number", CliTextStyle.Parameter);
        terminal.Write(" or ", CliTextStyle.Prompt);
        terminal.Write("ID", CliTextStyle.Parameter);
        terminal.Write(" (Enter to cancel): ", CliTextStyle.Prompt);

        var input = (await terminal.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (int.TryParse(input, out var number)
            && number >= 1
            && number <= items.Count)
        {
            return items[number - 1].Id;
        }

        return input;
    }
}
