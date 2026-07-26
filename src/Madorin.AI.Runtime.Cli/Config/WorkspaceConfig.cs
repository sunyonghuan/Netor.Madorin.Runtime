namespace Madorin.AI.Runtime.Cli.Config;

internal sealed class WorkspaceConfig
{
    public int? SchemaVersion { get; set; } = WorkspaceConfigLoader.CurrentSchemaVersion;

    public WorkspaceSelectionConfig? Selection { get; set; }
}
