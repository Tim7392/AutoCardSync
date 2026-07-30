using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record CompletedTaskReuseEligibility(
    bool CanReuse,
    IReadOnlyList<string> Reasons);

public static class CompletedTaskReuseGuard
{
    public static CompletedTaskReuseEligibility Evaluate(
        TaskManifest manifest,
        StandaloneTaskJournal journal,
        StandaloneCompletionReceipt receipt,
        RecoveryIdentitySnapshot current,
        string currentLocalTargetRoot,
        string currentNasTargetRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(current);

        var reasons = new List<string>();
        reasons.AddRange(ManifestJournalBindingValidator.Validate(manifest, journal).Reasons);
        reasons.AddRange(RecoveryGuard.Evaluate(journal, current).Reasons);
        reasons.AddRange(StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).Reasons);
        if (journal.TargetMode.RequiresLocal())
            AddPathMismatch(reasons, journal.LocalTargetRoot, currentLocalTargetRoot, "local_target_root_changed");
        if (journal.TargetMode.RequiresNas())
            AddPathMismatch(reasons, journal.NasTargetRoot, currentNasTargetRoot, "nas_target_root_changed");
        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static CompletedTaskReuseEligibility EvaluatePersisted(
        StandaloneTaskJournal journal,
        StandaloneCompletionReceipt receipt,
        RecoveryIdentitySnapshot current,
        string currentLocalTargetRoot,
        string currentNasTargetRoot)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(current);

        var reasons = new List<string>();
        if (!journal.LocalCompletionReceiptPersisted)
            reasons.Add("journal_completion_receipt_not_persisted");
        reasons.AddRange(RecoveryGuard.Evaluate(journal, current).Reasons);
        reasons.AddRange(StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).Reasons);
        if (journal.TargetMode.RequiresLocal())
            AddPathMismatch(reasons, journal.LocalTargetRoot, currentLocalTargetRoot, "local_target_root_changed");
        if (journal.TargetMode.RequiresNas())
            AddPathMismatch(reasons, journal.NasTargetRoot, currentNasTargetRoot, "nas_target_root_changed");
        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void AddPathMismatch(
        ICollection<string> reasons,
        string expected,
        string actual,
        string reason)
    {
        try
        {
            string normalizedExpected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected));
            string normalizedActual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual));
            if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase))
                reasons.Add(reason);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reasons.Add(reason);
        }
    }
}
