namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Complete user-anchored history page used by chat display replay.</summary>
public sealed record ConversationRoundsPage(
    IReadOnlyList<ConversationRecordV1> Records,
    long? OldestUserSequence,
    bool HasOlder);
