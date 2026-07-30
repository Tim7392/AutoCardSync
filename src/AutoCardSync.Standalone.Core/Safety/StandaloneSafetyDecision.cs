using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Safety;

public sealed class StandaloneSafetyFacts
{
    public StandaloneTargetMode TargetMode { get; set; } = StandaloneTargetMode.LocalAndNas;
    public bool ManifestFrozen { get; set; }
    public bool SourceReadOnly { get; set; }
    public bool AllIncludedFilesAccountedFor { get; set; }
    public bool LocalTargetFullRereadSha256Passed { get; set; }
    public bool NasTargetFullRereadSha256Passed { get; set; }
    public bool FinalObjectsSafelyPublishedOrReused { get; set; }
    public bool SourceIdentityUnchanged { get; set; }
    public bool TargetIdentitiesUnchanged { get; set; }
    public int FailedIncludedFiles { get; set; }
    public int PendingIncludedFiles { get; set; }
    public bool LocalCompletionReceiptPersisted { get; set; }
}

public sealed record StandaloneSafetyResult(
    bool SafeToRemoveCard,
    IReadOnlyList<string> UnmetConditions);

public static class StandaloneSafetyDecision
{
    public static StandaloneSafetyResult Evaluate(StandaloneSafetyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var unmet = new List<string>();
        Require(unmet, facts.ManifestFrozen, "MANIFEST_FROZEN");
        Require(unmet, facts.SourceReadOnly, "SOURCE_READ_ONLY");
        Require(unmet, facts.AllIncludedFilesAccountedFor, "ALL_INCLUDED_FILES_ACCOUNTED_FOR");
        if (facts.TargetMode.RequiresLocal())
            Require(unmet, facts.LocalTargetFullRereadSha256Passed, "LOCAL_TARGET_FULL_REREAD_SHA256");
        if (facts.TargetMode.RequiresNas())
            Require(unmet, facts.NasTargetFullRereadSha256Passed, "NAS_TARGET_FULL_REREAD_SHA256");
        Require(unmet, facts.FinalObjectsSafelyPublishedOrReused, "FINAL_OBJECTS_SAFELY_PUBLISHED_OR_REUSED");
        Require(unmet, facts.SourceIdentityUnchanged, "SOURCE_IDENTITY_UNCHANGED");
        Require(unmet, facts.TargetIdentitiesUnchanged, "TARGET_IDENTITIES_UNCHANGED");
        Require(unmet, facts.FailedIncludedFiles == 0, "FAILED_INCLUDED_FILES");
        Require(unmet, facts.PendingIncludedFiles == 0, "PENDING_INCLUDED_FILES");
        Require(unmet, facts.LocalCompletionReceiptPersisted, "LOCAL_COMPLETION_RECEIPT_PERSISTED");
        return new(unmet.Count == 0, unmet.AsReadOnly());
    }

    private static void Require(ICollection<string> unmet, bool satisfied, string name)
    {
        if (!satisfied)
            unmet.Add(name);
    }
}
