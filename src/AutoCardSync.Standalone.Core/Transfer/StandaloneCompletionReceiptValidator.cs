using AutoCardSync.Standalone.Core.Recovery;

using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Transfer;

public sealed record StandaloneCompletionReceiptValidation(
    bool IsValid,
    IReadOnlyList<string> Reasons);

public static class StandaloneCompletionReceiptValidator
{
    public static StandaloneCompletionReceiptValidation Evaluate(
        StandaloneCompletionReceipt receipt,
        StandaloneTaskJournal journal)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(journal);

        var reasons = new List<string>();
        if (receipt.SchemaVersion is not (1 or 2))
            reasons.Add("receipt_schema_invalid");
        if (receipt.TargetMode != journal.TargetMode)
            reasons.Add("receipt_target_mode_mismatch");
        if (!receipt.SafeToRemoveCard)
            reasons.Add("receipt_not_committed");
        if (receipt.CompletedAtUtc == default)
            reasons.Add("receipt_completion_time_missing");
        AddMismatch(reasons, receipt.TaskId, journal.TaskId, "receipt_task_mismatch");
        AddMismatch(reasons, receipt.CardInstanceId, journal.CardInstanceId, "receipt_card_mismatch");
        AddMismatch(reasons, receipt.ManifestHash, journal.ManifestHash, "receipt_manifest_mismatch");
        AddMismatch(reasons, receipt.SourceIdentity, journal.SourceIdentity, "receipt_source_mismatch");
        if (journal.TargetMode.RequiresLocal())
            AddMismatch(reasons, receipt.LocalTargetIdentity, journal.LocalTargetIdentity, "receipt_local_target_mismatch");
        if (journal.TargetMode.RequiresNas())
            AddMismatch(reasons, receipt.NasTargetIdentity, journal.NasTargetIdentity, "receipt_nas_target_mismatch");

        if (receipt.Files is null || receipt.Files.Any(file => file is null))
        {
            reasons.Add("receipt_file_collection_invalid");
            return new(false, reasons.AsReadOnly());
        }

        Dictionary<Guid, StandaloneCompletionFileFact> receiptFiles;
        try
        {
            receiptFiles = receipt.Files.ToDictionary(file => file.FileId);
        }
        catch (ArgumentException)
        {
            reasons.Add("receipt_duplicate_file_identity");
            return new(false, reasons.AsReadOnly());
        }

        if (receiptFiles.Count != journal.Files.Count)
            reasons.Add("receipt_file_count_mismatch");
        if (receiptFiles.Count == 0 || journal.Files.Count == 0)
            reasons.Add("receipt_file_count_zero");

        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (!receiptFiles.TryGetValue(file.FileId, out StandaloneCompletionFileFact? fact))
            {
                reasons.Add("receipt_file_missing");
                continue;
            }

            bool requiredTargetsVerified =
                (!journal.TargetMode.RequiresLocal() || IsVerified(file.LocalTarget)) &&
                (!journal.TargetMode.RequiresNas() || IsVerified(file.NasTarget));
            if (file.State != StandaloneFileState.Verified || !requiredTargetsVerified)
                reasons.Add("receipt_file_not_verified");

            AddMismatch(reasons, fact.RelativePath, file.RelativePath, "receipt_relative_path_mismatch", path: true);
            AddMismatch(reasons, fact.Length, file.Length, "receipt_length_mismatch");
            AddMismatch(reasons, fact.SourceSha256, file.SourceSha256, "receipt_source_hash_mismatch", hash: true);
            if (journal.TargetMode.RequiresLocal())
            {
                AddMismatch(reasons, fact.LocalFinalObjectId, file.LocalTarget.FinalObjectIdentity, "receipt_local_object_mismatch");
                AddMismatch(reasons, fact.LocalFinalSha256, file.LocalTarget.FinalSha256, "receipt_local_hash_mismatch", hash: true);
                AddMismatch(reasons, fact.LocalFinalSha256, fact.SourceSha256, "receipt_local_hash_not_source", hash: true);
            }
            if (journal.TargetMode.RequiresNas())
            {
                AddMismatch(reasons, fact.NasFinalObjectId, file.NasTarget.FinalObjectIdentity, "receipt_nas_object_mismatch");
                AddMismatch(reasons, fact.NasFinalSha256, file.NasTarget.FinalSha256, "receipt_nas_hash_mismatch", hash: true);
                AddMismatch(reasons, fact.NasFinalSha256, fact.SourceSha256, "receipt_nas_hash_not_source", hash: true);
            }
        }

        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsVerified(StandaloneTargetFileJournal target) =>
        target.State == StandaloneTargetState.Verified &&
        (target.AtomicallyPublished || target.ReusedExisting) &&
        target.FullRereadSha256Passed &&
        !string.IsNullOrWhiteSpace(target.FinalObjectIdentity) &&
        !string.IsNullOrWhiteSpace(target.FinalSha256);

    private static void AddMismatch<T>(
        ICollection<string> reasons,
        T actual,
        T expected,
        string reason)
        where T : struct, IEquatable<T>
    {
        if (!actual.Equals(expected))
            reasons.Add(reason);
    }

    private static void AddMismatch(
        ICollection<string> reasons,
        string? actual,
        string? expected,
        string reason,
        bool path = false,
        bool hash = false)
    {
        StringComparison comparison = path || hash
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(actual) ||
            string.IsNullOrWhiteSpace(expected) ||
            !string.Equals(actual, expected, comparison))
        {
            reasons.Add(reason);
        }
    }
}
