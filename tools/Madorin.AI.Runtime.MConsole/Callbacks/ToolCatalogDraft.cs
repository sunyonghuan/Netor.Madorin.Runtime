using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed record ToolCatalogDraft(
    string CatalogVersion,
    ToolCatalogItem[] Tools);
