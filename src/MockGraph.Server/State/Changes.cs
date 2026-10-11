namespace MockGraph.Server.State;

public enum ChangeKind
{
    ItemUpserted,
    ItemDeleted,
    ItemSharingChanged,
    GroupMemberAdded,
    GroupMemberRemoved,
}

/// <summary>A change before it gets its sequence number.</summary>
public sealed record PendingChange(
    ChangeKind Kind, string? DriveKey = null, string? ItemId = null, string? ItemPath = null, string? GroupKey = null, PrincipalRef? Member = null);

/// <summary>One entry of the append-only change log. <see cref="Seq"/> is strictly increasing within an epoch.</summary>
public sealed record ChangeEntry(
    long Seq, ChangeKind Kind, string? DriveKey, string? ItemId, string? ItemPath, string? GroupKey, PrincipalRef? Member);

/// <summary>Result of an admin mutation; <see cref="Changed"/> is false for idempotent no-ops.</summary>
public sealed record AdminOutcome(
    bool Changed, long Sequence, string? DriveId = null, string? ItemId = null, string? Path = null, bool Created = false);