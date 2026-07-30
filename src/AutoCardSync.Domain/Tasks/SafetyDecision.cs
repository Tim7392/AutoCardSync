namespace AutoCardSync.Domain.Tasks;

/// <summary>
/// Status of a single file copy on a specific target.
/// </summary>
public record FileCopyStatus(
    FileCopyState State, Guid TargetId, bool IsRequired, Guid FileId = default);

/// <summary>
/// Explicit durability qualification for one target. Contracts are target-bound
/// so a qualification cannot be reused for a different destination.
/// </summary>
public sealed record TargetDurabilityContract(
    Guid TargetId, string ContractId, bool IsQualified)
{
    internal bool Qualifies(Guid targetId)
        => IsQualified
           && TargetId == targetId
           && !string.IsNullOrWhiteSpace(ContractId);
}

/// <summary>
/// Evidence that is external to file-copy facts but required before source media
/// can be declared safe to clear. Empty or partial evidence always fails closed.
/// </summary>
public sealed record SafetyEvidence(
    bool ManifestFrozen,
    string ManifestHash,
    string FilterRuleVersion,
    bool DatabaseAuditPersisted,
    string ProtectedAuditAnchorReceipt,
    string TaskSummaryHash)
{
    public static SafetyEvidence Unproven { get; } = new(
        false, string.Empty, string.Empty, false, string.Empty, string.Empty);

    internal bool IsComplete
        => ManifestFrozen
           && IsSha256(ManifestHash)
           && !string.IsNullOrWhiteSpace(FilterRuleVersion)
           && DatabaseAuditPersisted
           && !string.IsNullOrWhiteSpace(ProtectedAuditAnchorReceipt)
           && IsSha256(TaskSummaryHash);

    internal static SafetyEvidence DatabaseAuditOnly(bool persisted)
        => Unproven with { DatabaseAuditPersisted = persisted };

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Information about a storage target, its real fault domain, and its durability qualification.
/// </summary>
public record TargetInfo(
    Guid Id, string FaultDomainId, bool IsRequired,
    TargetDurabilityContract? DurabilityContract = null);

/// <summary>
/// Computes the safe task state from copy facts and explicit safety evidence.
/// Missing durability, manifest, audit-anchor, or task-summary evidence fails closed.
/// </summary>
public static class SafetyDecision
{
    private static readonly Guid CompatibilityFileId = new(
        1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    internal static TransferTaskState ComputeTaskState(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        bool auditPersisted)
    {
        FileCopyStatus[] normalized = fileCopies.Select(copy =>
            copy.FileId == Guid.Empty ? copy with { FileId = CompatibilityFileId } : copy).ToArray();
        return ComputeTaskState(
            normalized, requiredTargets, [CompatibilityFileId],
            SafetyEvidence.DatabaseAuditOnly(auditPersisted));
    }

    internal static TransferTaskState ComputeTaskState(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        SafetyEvidence safetyEvidence)
    {
        FileCopyStatus[] normalized = fileCopies.Select(copy =>
            copy.FileId == Guid.Empty ? copy with { FileId = CompatibilityFileId } : copy).ToArray();
        return ComputeTaskState(
            normalized, requiredTargets, [CompatibilityFileId], safetyEvidence);
    }

    /// <summary>
    /// Compatibility overload retained for existing callers. A database-audit boolean
    /// alone cannot prove durability, frozen-manifest, protected-anchor, or summary evidence.
    /// </summary>
    public static TransferTaskState ComputeTaskState(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        IReadOnlyCollection<Guid> expectedFileIds,
        bool auditPersisted)
        => ComputeTaskState(
            fileCopies, requiredTargets, expectedFileIds,
            SafetyEvidence.DatabaseAuditOnly(auditPersisted));

    public static TransferTaskState ComputeTaskState(
        IReadOnlyList<FileCopyStatus> fileCopies,
        IReadOnlyList<TargetInfo> requiredTargets,
        IReadOnlyCollection<Guid> expectedFileIds,
        SafetyEvidence safetyEvidence)
    {
        if (fileCopies == null)
            throw new ArgumentNullException(nameof(fileCopies));
        if (requiredTargets == null)
            throw new ArgumentNullException(nameof(requiredTargets));
        if (expectedFileIds == null)
            throw new ArgumentNullException(nameof(expectedFileIds));
        if (safetyEvidence == null)
            throw new ArgumentNullException(nameof(safetyEvidence));

        var safetyTargets = requiredTargets
            .Where(target => target.IsRequired)
            .ToList();
        var safetyTargetIds = safetyTargets
            .Select(target => target.Id)
            .ToHashSet();
        bool invalidTargetSet = safetyTargets.Any(target => target.Id == Guid.Empty)
            || safetyTargetIds.Count != safetyTargets.Count;
        var expectedIds = expectedFileIds.ToHashSet();
        bool invalidExpectedSet = expectedIds.Count == 0
            || expectedIds.Count != expectedFileIds.Count
            || expectedIds.Contains(Guid.Empty);
        bool unknownRequiredTarget = fileCopies.Any(copy =>
            copy.IsRequired && !safetyTargetIds.Contains(copy.TargetId));
        var safetyCopies = fileCopies
            .Where(copy => copy.IsRequired && safetyTargetIds.Contains(copy.TargetId))
            .ToList();

        var copiesByTarget = safetyCopies
            .GroupBy(c => c.TargetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var completeTargetIds = new HashSet<Guid>();
        foreach (var target in safetyTargets)
        {
            if (!copiesByTarget.TryGetValue(target.Id, out var copies))
                continue;
            if (invalidExpectedSet || copies.Count != expectedIds.Count)
                continue;
            bool exactIdentityCoverage = copies.All(copy => expectedIds.Contains(copy.FileId))
                && copies.GroupBy(copy => copy.FileId).All(group => group.Count() == 1);
            bool allVerified = copies.All(c =>
                c.State == FileCopyState.Verified ||
                c.State == FileCopyState.ReusedVerified);
            if (exactIdentityCoverage && allVerified)
                completeTargetIds.Add(target.Id);
        }

        var completeTargets = safetyTargets
            .Where(t => completeTargetIds.Contains(t.Id))
            .ToList();
        var durableCompleteTargets = completeTargets
            .Where(target => target.DurabilityContract?.Qualifies(target.Id) == true)
            .ToList();

        var distinctFaultDomains = durableCompleteTargets
            .Select(t => t.FaultDomainId)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        bool hasPending = safetyCopies.Any(c =>
            c.State == FileCopyState.Pending ||
            c.State == FileCopyState.Copying ||
            c.State == FileCopyState.Partial ||
            c.State == FileCopyState.Verifying);

        bool hasFailed = invalidExpectedSet || invalidTargetSet || unknownRequiredTarget || safetyCopies.Any(c =>
            c.State == FileCopyState.Failed ||
            c.State == FileCopyState.Cancelled);

        bool allRequiredTargetsComplete = safetyTargets.Count >= 2
            && completeTargetIds.Count == safetyTargets.Count;
        bool allRequiredTargetsDurable = durableCompleteTargets.Count == safetyTargets.Count;

        if (distinctFaultDomains >= 2
            && safetyEvidence.IsComplete
            && !hasPending
            && !hasFailed
            && allRequiredTargetsComplete
            && allRequiredTargetsDurable)
        {
            return TransferTaskState.SafeToClear;
        }

        if (completeTargetIds.Count >= 1)
            return TransferTaskState.TransferredNotBackedUp;

        if (completeTargetIds.Count == 0 && hasPending)
            return TransferTaskState.Interrupted;

        if (completeTargetIds.Count == 0 && hasFailed)
            return TransferTaskState.Failed;

        return TransferTaskState.Interrupted;
    }
}
