namespace AutoCardSync.Domain.Tasks;

public record StateTransition(TransferTaskState From, TransferTaskState To, DateTimeOffset At, string? Reason);

public class TransferTask
{
    public Guid Id { get; }
    public Guid ActivityId { get; }
    public Guid CardInstanceId { get; }
    public TransferTaskState State { get; private set; } = TransferTaskState.Discovered;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public long TotalBytes { get; private set; }
    public long CopiedBytes { get; private set; }
    public int TotalFiles { get; private set; }
    public int VerifiedFiles { get; private set; }
    public int FailedFiles { get; private set; }

    // These are used by SafetyDecision; stored per-file-per-target externally,
    // but the task tracks aggregate progress.
    public int ExcludedFiles { get; private set; }

    private readonly List<StateTransition> _history = new();
    public IReadOnlyList<StateTransition> History => _history;

    public TransferTask(Guid id, Guid activityId, Guid cardInstanceId, DateTimeOffset createdAt)
    {
        Id = id;
        ActivityId = activityId;
        CardInstanceId = cardInstanceId;
        CreatedAt = createdAt;
        _history.Add(new StateTransition(
            TransferTaskState.Discovered, TransferTaskState.Discovered, createdAt, null));
    }

    public static TransferTask Rehydrate(
        Guid id, Guid activityId, Guid cardInstanceId, DateTimeOffset createdAt,
        TransferTaskState state, long totalBytes, int totalFiles,
        string? failureReason, DateTimeOffset? completedAt)
    {
        if (totalBytes < 0 || totalFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        var task = new TransferTask(id, activityId, cardInstanceId, createdAt)
        {
            State = state,
            TotalBytes = totalBytes,
            TotalFiles = totalFiles,
            FailureReason = failureReason,
            CompletedAt = completedAt,
        };
        if (state != TransferTaskState.Discovered)
            task._history.Add(new StateTransition(
                TransferTaskState.Discovered, state, createdAt, nameof(Rehydrate)));
        return task;
    }

    public bool ConfirmIdentity(string reason)
    {
        return Transition(TransferTaskState.NeedsIdentityConfirmation,
            reason ?? "Identity confirmed");
    }

    public bool StartPreflight()
    {
        // Can start preflight from Discovered (auto-identified) or NeedsIdentityConfirmation
        if (State == TransferTaskState.Discovered)
            return Transition(TransferTaskState.Preflight, "Preflight started from Discovered");
        if (State == TransferTaskState.NeedsIdentityConfirmation)
            return Transition(TransferTaskState.Preflight, "Preflight started after identity confirmation");
        throw new InvalidOperationException(
            $"Cannot start preflight from state {State}.");
    }

    public bool PassPreflight(long totalBytes, int totalFiles)
    {
        if (totalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes), "Total bytes cannot be negative.");
        if (totalFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(totalFiles), "Total files cannot be negative.");

        EnsureValidTransition(TransferTaskState.Scanning);
        TotalBytes = totalBytes;
        TotalFiles = totalFiles;
        return Transition(TransferTaskState.Scanning, "Preflight passed, scanning started");
    }

    public bool FailPreflight(string reason)
    {
        EnsureValidTransition(TransferTaskState.Blocked);
        FailureReason = reason;
        return Transition(TransferTaskState.Blocked, $"Preflight failed: {reason}");
    }

    public bool RetryPreflight()
    {
        EnsureValidTransition(TransferTaskState.Preflight);
        FailureReason = null;
        return Transition(TransferTaskState.Preflight, "Retrying preflight from Blocked");
    }

    public bool SetBlocked(string reason)
    {
        EnsureValidTransition(TransferTaskState.Blocked);
        FailureReason = reason;
        return Transition(TransferTaskState.Blocked, reason);
    }

    public bool StartCopying()
    {
        return Transition(TransferTaskState.Copying, "Copying started");
    }

    public bool StartVerifying()
    {
        return Transition(TransferTaskState.Verifying, "Verification started");
    }

    /// <summary>
    /// Evaluate safety from copy facts plus explicit durability, manifest, audit-anchor,
    /// and task-summary evidence. Missing evidence fails closed.
    /// </summary>
    public bool EvaluateSafety(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        IReadOnlyCollection<Guid> expectedFileIds,
        SafetyEvidence safetyEvidence)
    {
        var computed = SafetyDecision.ComputeTaskState(
            fileCopies, requiredTargets, expectedFileIds, safetyEvidence);
        return Transition(computed, $"Safety evaluated: {computed}");
    }

    /// <summary>
    /// Compatibility overload. A database-audit boolean alone is intentionally
    /// insufficient to produce SafeToClear.
    /// </summary>
    public bool EvaluateSafety(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        IReadOnlyCollection<Guid> expectedFileIds,
        bool auditPersisted)
        => EvaluateSafety(
            fileCopies, requiredTargets, expectedFileIds,
            SafetyEvidence.DatabaseAuditOnly(auditPersisted));

    internal bool EvaluateSafety(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        bool auditPersisted)
    {
        var computed = SafetyDecision.ComputeTaskState(
            fileCopies, requiredTargets, auditPersisted);
        return Transition(computed, $"Safety evaluated: {computed}");
    }

    public bool SetInterrupted(string reason)
    {
        EnsureValidTransition(TransferTaskState.Interrupted);
        FailureReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        return Transition(TransferTaskState.Interrupted, $"Interrupted: {reason}");
    }

    public bool SetFailed(string reason)
    {
        EnsureValidTransition(TransferTaskState.Failed);
        FailureReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        return Transition(TransferTaskState.Failed, $"Failed: {reason}");
    }

    public bool Resume()
    {
        if (State != TransferTaskState.Interrupted)
            throw new InvalidOperationException(
                $"Cannot resume from state {State}; only Interrupted is resumable.");

        FailureReason = null;
        CompletedAt = null;
        return Transition(TransferTaskState.Copying, "Resumed from interruption");
    }

    public bool RevokeSafety()
    {
        return Transition(TransferTaskState.SafetyRevoked, "Safety revoked");
    }

    private bool Transition(TransferTaskState to, string? reason)
    {
        var from = State;
        EnsureValidTransition(to);

        State = to;
        _history.Add(new StateTransition(from, to, DateTimeOffset.UtcNow, reason));
        return true;
    }

    private void EnsureValidTransition(TransferTaskState to)
    {
        if (!IsValidTransition(State, to))
            throw new InvalidOperationException(
                $"Invalid state transition from {State} to {to}.");
    }

    private static bool IsValidTransition(TransferTaskState from, TransferTaskState to) => (from, to) switch
    {
        // Normal flow
        (TransferTaskState.Discovered, TransferTaskState.NeedsIdentityConfirmation) => true,
        (TransferTaskState.Discovered, TransferTaskState.Preflight) => true,
        (TransferTaskState.NeedsIdentityConfirmation, TransferTaskState.Preflight) => true,
        (TransferTaskState.Preflight, TransferTaskState.Scanning) => true,
        (TransferTaskState.Preflight, TransferTaskState.Blocked) => true,
        (TransferTaskState.Scanning, TransferTaskState.Copying) => true,
        (TransferTaskState.Scanning, TransferTaskState.Failed) => true,
        (TransferTaskState.Copying, TransferTaskState.Verifying) => true,
        (TransferTaskState.Copying, TransferTaskState.Interrupted) => true,
        (TransferTaskState.Copying, TransferTaskState.Blocked) => true,
        (TransferTaskState.Copying, TransferTaskState.Failed) => true,
        (TransferTaskState.Verifying, TransferTaskState.SafeToClear) => true,
        (TransferTaskState.Verifying, TransferTaskState.TransferredNotBackedUp) => true,
        (TransferTaskState.Verifying, TransferTaskState.Interrupted) => true,
        (TransferTaskState.Verifying, TransferTaskState.Failed) => true,

        // Recovery
        (TransferTaskState.Interrupted, TransferTaskState.Copying) => true,
        (TransferTaskState.Interrupted, TransferTaskState.Blocked) => true,
        (TransferTaskState.Preflight, TransferTaskState.Interrupted) => true,
        (TransferTaskState.Blocked, TransferTaskState.Preflight) => true,
        (TransferTaskState.Blocked, TransferTaskState.Failed) => true,

        // Safety revocation
        (TransferTaskState.SafeToClear, TransferTaskState.SafetyRevoked) => true,

        // TransferredNotBackedUp -> Verifying (backup target recovered)
        (TransferTaskState.TransferredNotBackedUp, TransferTaskState.Verifying) => true,

        // TransferredNotBackedUp -> Failed (unique copy lost)
        (TransferTaskState.TransferredNotBackedUp, TransferTaskState.Failed) => true,

        // Catch-all: any non-terminal state can transition to Failed.
        // Explicitly excludes SafeToClear and SafetyRevoked (terminal safety states)
        // and Failed itself (already terminal). This is intentional — any other
        // in-progress state (e.g. Scanning, Copying, Verifying, Interrupted, Blocked)
        // should be fail-able via SetFailed().
        (_, TransferTaskState.Failed) => from is not (
            TransferTaskState.SafeToClear or
            TransferTaskState.SafetyRevoked or
            TransferTaskState.Failed),

        _ => false,
    };
}
