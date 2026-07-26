using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public enum SkillSourceKind
{
    Directory = 0,
    Mcp = 1
}

/// <summary>Describes an explicitly supplied Skills source.</summary>
public sealed record SkillSourceDescriptor(
    string SourceId,
    SkillSourceKind Kind,
    string Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayName = null);

public sealed record SkillSourceResult(
    string SourceId,
    SkillSourceKind Kind,
    string Location,
    string Status,
    int SkillCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DiagnosticCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Message = null);

public sealed record SkillCatalogDiagnostic(
    string Code,
    string Severity,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SkillName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path = null);

public sealed record SkillCatalogSkill(
    string Name,
    string Description,
    string SourceId,
    string ContentHash,
    string MaterializedPath,
    string[] Resources,
    string[] Scripts);

public sealed record SkillCatalogSummary(
    string CatalogVersion,
    int SkillCount,
    SkillCatalogSkill[] Skills,
    SkillSourceResult[] Sources,
    SkillCatalogDiagnostic[] Diagnostics);

public sealed record SkillCatalogQueryRequest;

public sealed record SkillCatalogRefreshRequest;

public sealed record SkillCatalogChangedEvent(
    string RuntimeInstanceId,
    string PreviousVersion,
    string CatalogVersion,
    int SkillCount,
    string[] ChangedSkillNames,
    DateTimeOffset ChangedAt);
