namespace AutoCardSync.Domain.Tasks;

/// <summary>
/// Represents the copy lifecycle state of a single file during a sync task.
/// </summary>
public enum FileCopyState
{
    /// <summary>File is queued and waiting to be copied.</summary>
    Pending,

    /// <summary>File is actively being copied.</summary>
    Copying,

    /// <summary>File was partially copied (e.g., interrupted or incomplete).</summary>
    Partial,

    /// <summary>File copy completed; verification is in progress.</summary>
    Verifying,

    /// <summary>File copy and verification succeeded.</summary>
    Verified,

    /// <summary>File already existed at destination and passed verification without a new copy.</summary>
    ReusedVerified,

    /// <summary>File was excluded from the sync task (e.g., filtered out).</summary>
    Excluded,

    /// <summary>File copy or verification failed.</summary>
    Failed,

    /// <summary>File copy was cancelled by the user or system.</summary>
    Cancelled
}
