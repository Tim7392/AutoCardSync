using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Recovery;

public enum StandaloneFileState
{
    Pending,
    Copying,
    Verified,
    Failed,
}

public enum StandaloneTargetState
{
    Pending = 0,
    Copying = 1,
    Published = 2,
    Verified = 3,
    Failed = 4,
    NotRequired = 5,
}

public sealed record StandaloneBlockCheckpoint
{
    public required int BlockIndex { get; init; }
    public required long Offset { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public required DateTimeOffset PersistedAtUtc { get; init; }
}

public sealed record StandaloneTargetFileJournal
{
    public required Guid TargetId { get; init; }
    public required string TargetRole { get; init; }
    public required string TargetIdentity { get; init; }
    public required string TemporaryPath { get; init; }
    public string? TemporaryObjectIdentity { get; init; }
    public string? FinalPath { get; init; }
    public string? FinalObjectIdentity { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? FinalSha256 { get; init; }
    public StandaloneTargetState State { get; init; }
    public bool AtomicallyPublished { get; init; }
    public bool FullRereadSha256Passed { get; init; }
    public bool ReusedExisting { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StandaloneBlockCheckpoint> Checkpoints { get; init; } = [];
}

public sealed record StandaloneFileJournal
{
    public required Guid FileId { get; init; }
    public required string RelativePath { get; init; }
    /// <summary>Gets the frozen single-level target file name for schema-v3 tasks.</summary>
    public string DestinationRelativePath { get; init; } = string.Empty;
    public required long Length { get; init; }
    public required string SourceSha256 { get; init; }
    public string? SourceFileIdentity { get; init; }
    public StandaloneFileState State { get; init; }
    public required StandaloneTargetFileJournal LocalTarget { get; init; }
    public required StandaloneTargetFileJournal NasTarget { get; init; }
}

public sealed record StandaloneTaskJournal
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid TaskId { get; init; }
    public required string SourceIdentity { get; init; }
    public required Guid CardInstanceId { get; init; }
    public StandaloneTargetMode TargetMode { get; init; } = StandaloneTargetMode.LocalAndNas;
    public string InventoryManifestHash { get; init; } = string.Empty;
    public required string ManifestHash { get; init; }
    public bool ContentManifestFrozen { get; init; } = true;
    public required string LocalTargetIdentity { get; init; }
    public required string LocalTargetRoot { get; init; }
    public required string NasTargetIdentity { get; init; }
    public required string NasTargetRoot { get; init; }
    public required IReadOnlyList<StandaloneFileJournal> Files { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public bool LocalCompletionReceiptPersisted { get; init; }
}

public sealed record RecoveryIdentitySnapshot(
    string SourceIdentity,
    Guid CardInstanceId,
    string ManifestHash,
    string LocalTargetIdentity,
    string NasTargetIdentity,
    StandaloneTargetMode TargetMode = StandaloneTargetMode.LocalAndNas,
    string InventoryManifestHash = "");

public sealed record RecoveryEligibility(bool CanResume, IReadOnlyList<string> Reasons);

/// <summary>
/// Evaluates whether current source, card, manifest, target mode, and required target identities match a task journal.
/// </summary>
public static class RecoveryGuard
{
    /// <summary>
    /// Returns resumable only when every identity and manifest fact required by the journal remains unchanged.
    /// </summary>
    /// <remarks>
    /// This is an eligibility check only. A caller must still validate the manifest-to-journal binding and staged
    /// objects before publishing final objects or writing a completion receipt.
    /// </remarks>
    public static RecoveryEligibility Evaluate(
        StandaloneTaskJournal journal,
        RecoveryIdentitySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(current);

        var reasons = new List<string>();
        AddMismatch(reasons, journal.SourceIdentity, current.SourceIdentity, "source_identity_changed");
        if (journal.CardInstanceId != current.CardInstanceId)
            reasons.Add("card_identity_changed");
        if (journal.TargetMode != current.TargetMode)
            reasons.Add("target_mode_changed");
        if (!string.IsNullOrWhiteSpace(journal.InventoryManifestHash) ||
            !string.IsNullOrWhiteSpace(current.InventoryManifestHash))
        {
            AddMismatch(reasons, journal.InventoryManifestHash, current.InventoryManifestHash, "inventory_manifest_changed");
        }
        if (!string.IsNullOrWhiteSpace(journal.ManifestHash) ||
            !string.IsNullOrWhiteSpace(current.ManifestHash))
        {
            AddMismatch(reasons, journal.ManifestHash, current.ManifestHash, "manifest_changed");
        }
        if (journal.TargetMode.RequiresLocal())
            AddMismatch(reasons, journal.LocalTargetIdentity, current.LocalTargetIdentity, "local_target_identity_changed");
        if (journal.TargetMode.RequiresNas())
            AddMismatch(reasons, journal.NasTargetIdentity, current.NasTargetIdentity, "nas_target_identity_changed");
        return new(reasons.Count == 0, reasons.AsReadOnly());
    }

    private static void AddMismatch(
        ICollection<string> reasons,
        string expected,
        string actual,
        string reason)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            reasons.Add(reason);
    }
}
