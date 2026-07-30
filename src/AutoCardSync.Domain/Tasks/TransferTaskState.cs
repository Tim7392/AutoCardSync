namespace AutoCardSync.Domain.Tasks;

/// <summary>
/// Represents the lifecycle state of a transfer task.
/// </summary>
public enum TransferTaskState
{
    /// <summary>
    /// The file has been found on the source card but no action has been taken yet.
    /// </summary>
    Discovered,

    /// <summary>
    /// The file needs human confirmation that its identity (hash/size/name) matches an expected record before proceeding.
    /// </summary>
    NeedsIdentityConfirmation,

    /// <summary>
    /// Pre-copy checks are running (destination space, path validation, lock acquisition).
    /// </summary>
    Preflight,

    /// <summary>
    /// The source file is being read and hashed.
    /// </summary>
    Scanning,

    /// <summary>
    /// Bytes are actively being copied from source to destination.
    /// </summary>
    Copying,

    /// <summary>
    /// Post-copy integrity verification is in progress (hash comparison).
    /// </summary>
    Verifying,

    /// <summary>
    /// The task cannot proceed due to an external condition (missing destination, hardware error) and is waiting for resolution.
    /// </summary>
    Blocked,

    /// <summary>
    /// The task was interrupted mid-operation (process crash, power loss) and needs recovery on next run.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The task has failed after exhausting retries or encountering an unrecoverable error.
    /// </summary>
    Failed,

    /// <summary>
    /// The file has been successfully transferred and verified, but the backup copy has not yet been confirmed safe.
    /// </summary>
    TransferredNotBackedUp,

    /// <summary>
    /// The source file may be cleared from the card — both primary transfer and backup are confirmed.
    /// </summary>
    SafeToClear,

    /// <summary>
    /// The safety guarantee was revoked (e.g. backup was deleted or became unreadable); the source must be retained.
    /// </summary>
    SafetyRevoked
}
