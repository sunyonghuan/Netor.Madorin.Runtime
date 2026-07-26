namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Signals that tool preparation found a missing grant dimension without causing a side effect.</summary>
public sealed class ToolPermissionRequiredException(string message)
    : UnauthorizedAccessException(message);
