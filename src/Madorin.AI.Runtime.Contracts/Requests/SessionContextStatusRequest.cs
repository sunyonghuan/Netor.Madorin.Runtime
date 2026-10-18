namespace Madorin.AI.Runtime.Contracts;

/// <summary>Queries context metadata without returning history or invoking a model.</summary>
public sealed record SessionContextStatusRequest(string SessionId, string? OperationId = null);
