using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCardSync.Application.Cards;

public record CardIdentityEvidence
{
    public Guid? VolumeGuid { get; init; }
    public uint? VolumeSerialNumber { get; init; }
    public long? PartitionStartLba { get; init; }
    public long? PartitionLength { get; init; }
    public string? FileSystem { get; init; }
    public long? Capacity { get; init; }
    public string? PnpDeviceInstanceId { get; init; }
    public string? DeviceSerialNumber { get; init; }
    public string? RootDirectoryHash { get; init; }
    public string? SampleFingerprint { get; init; }
    public string? HistoricalCardInstanceId { get; init; }
}

public record CardIdentityResult
{
    public string? MatchedCardInstanceId { get; init; }
    public bool NeedsConfirmation { get; init; }
    public required string Decision { get; init; }
    public string AlgorithmVersion { get; init; } = "CardIdentity-v1";
    public required Dictionary<string, string> EvidenceSummary { get; init; }
}

/// <summary>
/// Implements the CardIdentity-v1 decision table (design baseline section 6.2).
///
/// Evidence families:
///   V = Volume/Partition: VolumeGuid, VolumeSerialNumber, PartitionStartLba, PartitionLength, FileSystem, Capacity
///   D = Device:           PnpDeviceInstanceId, DeviceSerialNumber
///   C = Content/History:  RootDirectoryHash, SampleFingerprint, HistoricalCardInstanceId
///
/// Decision order: strong conflicts are checked first and override matches.
/// </summary>
public class CardIdentityEvaluator
{
    /// <summary>
    /// Evaluate a single observed evidence set against zero or more historical candidates.
    /// </summary>
    /// <param name="observed">Evidence from the currently-inserted card.</param>
    /// <param name="candidates">
    /// Historical candidates to compare against. Each tuple is
    /// (CardInstanceId, Evidence from that historical binding).
    /// Empty when no prior binding exists.
    /// </param>
    public CardIdentityResult Evaluate(
        CardIdentityEvidence observed,
        IReadOnlyList<(string CardInstanceId, CardIdentityEvidence Evidence)> candidates)
    {
        var summary = new Dictionary<string, string>();

        if (candidates.Count == 0)
        {
            summary["V"] = "no_candidate";
            summary["D"] = "no_candidate";
            summary["C"] = "no_candidate";
            return new CardIdentityResult
            {
                Decision = "NewCard",
                NeedsConfirmation = false,
                EvidenceSummary = summary
            };
        }

        // Score each candidate across the three families and retain raw evidence.
        var scored = candidates.Select(c => new ScoredCandidate(
            c.CardInstanceId,
            c.Evidence,
            EvaluateV(observed, c.Evidence),
            EvaluateD(observed, c.Evidence),
            EvaluateC(observed, c.Evidence)
        )).ToList();

        // Check strong conflicts first -- they override everything.
        var conflicts = FindStrongConflicts(observed, scored);
        if (conflicts.Count > 0)
        {
            foreach (var kv in conflicts)
                summary[kv.Key] = kv.Value;

            return new CardIdentityResult
            {
                Decision = "NeedsIdentityConfirmation",
                NeedsConfirmation = true,
                EvidenceSummary = summary
            };
        }

        // Rule 1: No candidate shares V or C evidence -> NewCard.
        bool anyVOrCMatch = scored.Any(s => s.VMatch || s.CMatch);
        if (!anyVOrCMatch)
        {
            summary["V"] = "no_match";
            summary["D"] = scored.Any(s => s.DMatch) ? "d_only_match" : "no_match";
            summary["C"] = "no_match";
            return new CardIdentityResult
            {
                Decision = "NewCard",
                NeedsConfirmation = false,
                EvidenceSummary = summary
            };
        }

        // Rule 2: Unique candidate with V match AND (D or C also matches) -> AutoAssociate.
        var vMatches = scored.Where(s => s.VMatch).ToList();
        if (vMatches.Count == 1)
        {
            var best = vMatches[0];
            if (best.DMatch || best.CMatch)
            {
                summary["V"] = "match";
                summary["D"] = best.DMatch ? "match" : "no_match";
                summary["C"] = best.CMatch ? "match" : "no_match";
                return new CardIdentityResult
                {
                    MatchedCardInstanceId = best.CardInstanceId,
                    Decision = "AutoAssociate",
                    NeedsConfirmation = false,
                    EvidenceSummary = summary
                };
            }
        }

        // Rule 3 / 4: Multiple V matches (tie), or only weak evidence -> NeedsIdentityConfirmation.
        foreach (var s in scored)
        {
            summary[$"candidate:{s.CardInstanceId}:V"] = s.VMatch ? "match" : "no_match";
            summary[$"candidate:{s.CardInstanceId}:D"] = s.DMatch ? "match" : "no_match";
            summary[$"candidate:{s.CardInstanceId}:C"] = s.CMatch ? "match" : "no_match";
        }

        if (vMatches.Count > 1)
        {
            summary["tie"] = $"{vMatches.Count} candidates share V evidence";
        }
        else if (vMatches.Count == 1 && !vMatches[0].DMatch && !vMatches[0].CMatch)
        {
            summary["weak"] = "V match only, no D or C corroboration";
        }

        return new CardIdentityResult
        {
            Decision = "NeedsIdentityConfirmation",
            NeedsConfirmation = true,
            EvidenceSummary = summary
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-family evaluation helpers                                      //
    // ------------------------------------------------------------------ //

    private static bool EvaluateV(CardIdentityEvidence observed, CardIdentityEvidence candidate)
    {
        // File-system type and capacity are common across unrelated cards. They may
        // corroborate a primary identity, but they can never establish V continuity alone.
        bool volumeGuidMatches = CanUseVolumeGuidAgreement(observed, candidate);
        bool volumeSerialMatches = observed.VolumeSerialNumber is > 0 &&
            candidate.VolumeSerialNumber is > 0 &&
            observed.VolumeSerialNumber.Value == candidate.VolumeSerialNumber.Value;
        bool partitionStartMatches = observed.PartitionStartLba.HasValue && candidate.PartitionStartLba.HasValue &&
            observed.PartitionStartLba.Value == candidate.PartitionStartLba.Value;
        bool partitionLengthMatches = observed.PartitionLength.HasValue && candidate.PartitionLength.HasValue &&
            observed.PartitionLength.Value == candidate.PartitionLength.Value;
        bool fileSystemMatches = !string.IsNullOrEmpty(observed.FileSystem) &&
            !string.IsNullOrEmpty(candidate.FileSystem) &&
            string.Equals(observed.FileSystem, candidate.FileSystem, StringComparison.OrdinalIgnoreCase);
        bool capacityMatches = observed.Capacity.HasValue && candidate.Capacity.HasValue &&
            observed.Capacity.Value == candidate.Capacity.Value;

        int agreements = new[]
        {
            volumeGuidMatches,
            volumeSerialMatches,
            partitionStartMatches,
            partitionLengthMatches,
            fileSystemMatches,
            capacityMatches,
        }.Count(value => value);
        bool primaryIdentityMatches = volumeGuidMatches || volumeSerialMatches ||
            (partitionStartMatches && partitionLengthMatches);
        return primaryIdentityMatches && agreements >= 2;
    }

    private static bool EvaluateD(CardIdentityEvidence observed, CardIdentityEvidence candidate)
    {
        if (!string.IsNullOrEmpty(observed.PnpDeviceInstanceId) &&
            !string.IsNullOrEmpty(candidate.PnpDeviceInstanceId) &&
            string.Equals(observed.PnpDeviceInstanceId, candidate.PnpDeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(observed.DeviceSerialNumber) &&
            !string.IsNullOrEmpty(candidate.DeviceSerialNumber) &&
            string.Equals(observed.DeviceSerialNumber, candidate.DeviceSerialNumber, StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool EvaluateC(CardIdentityEvidence observed, CardIdentityEvidence candidate)
    {
        if (!string.IsNullOrEmpty(observed.HistoricalCardInstanceId) &&
            !string.IsNullOrEmpty(candidate.HistoricalCardInstanceId) &&
            string.Equals(observed.HistoricalCardInstanceId, candidate.HistoricalCardInstanceId, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(observed.RootDirectoryHash) &&
            !string.IsNullOrEmpty(candidate.RootDirectoryHash) &&
            string.Equals(observed.RootDirectoryHash, candidate.RootDirectoryHash, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(observed.SampleFingerprint) &&
            !string.IsNullOrEmpty(candidate.SampleFingerprint) &&
            string.Equals(observed.SampleFingerprint, candidate.SampleFingerprint, StringComparison.Ordinal))
            return true;

        return false;
    }

    // ------------------------------------------------------------------ //
    //  Strong conflict detection (checked first, overrides matches)       //
    // ------------------------------------------------------------------ //

    private static Dictionary<string, string> FindStrongConflicts(
        CardIdentityEvidence observed,
        List<ScoredCandidate> scored)
    {
        var conflicts = new Dictionary<string, string>();

        // --- Same Volume ID but different disk extents ---
        // Group candidates that share a volume identifier with the observed evidence.
        foreach (var s in scored)
        {
            bool volumeIdMatches = HasMatchingVolumeIdentifier(observed, s.Evidence);

            if (!volumeIdMatches)
                continue;

            bool extentConflict =
                (observed.PartitionStartLba.HasValue && s.Evidence.PartitionStartLba.HasValue &&
                 observed.PartitionStartLba.Value != s.Evidence.PartitionStartLba.Value) ||
                (observed.PartitionLength.HasValue && s.Evidence.PartitionLength.HasValue &&
                 observed.PartitionLength.Value != s.Evidence.PartitionLength.Value);

            if (extentConflict)
            {
                conflicts[$"candidate:{s.CardInstanceId}:volume_extent_conflict"] =
                    "Same volume ID but different partition extents";
            }
        }

        // --- Same history bound to another online medium ---
        // If the observed evidence carries a HistoricalCardInstanceId that points to
        // a candidate, but V or D evidence contradicts that binding.
        if (!string.IsNullOrEmpty(observed.HistoricalCardInstanceId))
        {
            var historyTarget = scored.FirstOrDefault(
                s => string.Equals(s.Evidence.HistoricalCardInstanceId,
                                   observed.HistoricalCardInstanceId,
                                   StringComparison.Ordinal));

            if (historyTarget is not null)
            {
                // History says "this card", but V evidence is present and disagrees.
                bool vDisagrees = s_VFieldsPresent(observed, historyTarget.Evidence) &&
                                  !EvaluateV(observed, historyTarget.Evidence);

                bool dDisagrees =
                    (!string.IsNullOrEmpty(observed.PnpDeviceInstanceId) &&
                     !string.IsNullOrEmpty(historyTarget.Evidence.PnpDeviceInstanceId) &&
                     !string.Equals(observed.PnpDeviceInstanceId,
                                    historyTarget.Evidence.PnpDeviceInstanceId,
                                    StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(observed.DeviceSerialNumber) &&
                     !string.IsNullOrEmpty(historyTarget.Evidence.DeviceSerialNumber) &&
                     !string.Equals(observed.DeviceSerialNumber,
                                    historyTarget.Evidence.DeviceSerialNumber,
                                    StringComparison.Ordinal));

                if (vDisagrees || dDisagrees)
                {
                    conflicts[$"candidate:{historyTarget.CardInstanceId}:history_bound_elsewhere"] =
                        "Historical binding contradicts current volume/device evidence";
                }
            }
        }

        // --- Content fingerprint conflict ---
        // SampleFingerprint present for both but differs while V or D matches.
        foreach (var s in scored)
        {
            bool directorySetChanged =
                !string.IsNullOrEmpty(observed.RootDirectoryHash) &&
                !string.IsNullOrEmpty(s.Evidence.RootDirectoryHash) &&
                !string.Equals(
                    observed.RootDirectoryHash,
                    s.Evidence.RootDirectoryHash,
                    StringComparison.Ordinal);
            if (!directorySetChanged &&
                !string.IsNullOrEmpty(observed.SampleFingerprint) &&
                !string.IsNullOrEmpty(s.Evidence.SampleFingerprint) &&
                !string.Equals(observed.SampleFingerprint, s.Evidence.SampleFingerprint, StringComparison.Ordinal))
            {
                if (EvaluateV(observed, s.Evidence) || EvaluateD(observed, s.Evidence))
                {
                    conflicts[$"candidate:{s.CardInstanceId}:fingerprint_conflict"] =
                        "Content fingerprint differs despite unchanged directory evidence";
                }
            }
        }

        // --- Unapproved reformat ---
        // FileSystem changed from a candidate that otherwise matches on volume ID.
        foreach (var s in scored)
        {
            bool volumeIdMatches = HasMatchingVolumeIdentifier(observed, s.Evidence);

            if (!volumeIdMatches)
                continue;

            if (!string.IsNullOrEmpty(observed.FileSystem) &&
                !string.IsNullOrEmpty(s.Evidence.FileSystem) &&
                !string.Equals(observed.FileSystem, s.Evidence.FileSystem, StringComparison.OrdinalIgnoreCase))
            {
                conflicts[$"candidate:{s.CardInstanceId}:reformat"] =
                    "FileSystem changed on same volume -- possible unapproved reformat";
            }
        }

        // --- Multiple candidates tie on V ---
        var vMatches = scored.Where(s => s.VMatch).ToList();
        if (vMatches.Count > 1)
        {
            var tiedIds = string.Join(", ", vMatches.Select(m => m.CardInstanceId));
            conflicts["v_tie"] = $"Multiple candidates match V evidence: {tiedIds}";
        }

        return conflicts;
    }

    /// <summary>
    /// Returns true when at least two V-family fields are present on both sides,
    /// indicating the caller has enough data to detect a meaningful disagreement.
    /// </summary>
    private static bool CanUseVolumeGuidAgreement(
        CardIdentityEvidence observed,
        CardIdentityEvidence candidate)
    {
        if (!observed.VolumeGuid.HasValue || !candidate.VolumeGuid.HasValue ||
            observed.VolumeGuid.Value != candidate.VolumeGuid.Value)
        {
            return false;
        }

        if (observed.VolumeSerialNumber.HasValue != candidate.VolumeSerialNumber.HasValue)
            return false;
        return !observed.VolumeSerialNumber.HasValue ||
            observed.VolumeSerialNumber.Value == candidate.VolumeSerialNumber!.Value;
    }

    private static bool HasMatchingVolumeIdentifier(
        CardIdentityEvidence observed,
        CardIdentityEvidence candidate)
    {
        if (observed.VolumeSerialNumber.HasValue || candidate.VolumeSerialNumber.HasValue)
        {
            return observed.VolumeSerialNumber.HasValue &&
                candidate.VolumeSerialNumber.HasValue &&
                observed.VolumeSerialNumber.Value == candidate.VolumeSerialNumber.Value;
        }

        return observed.VolumeGuid.HasValue && candidate.VolumeGuid.HasValue &&
            observed.VolumeGuid.Value == candidate.VolumeGuid.Value;
    }

    private static bool s_VFieldsPresent(CardIdentityEvidence a, CardIdentityEvidence b)
    {
        int present = 0;
        if (a.VolumeGuid.HasValue && b.VolumeGuid.HasValue)
            present++;
        if (a.VolumeSerialNumber.HasValue && b.VolumeSerialNumber.HasValue)
            present++;
        if (a.PartitionStartLba.HasValue && b.PartitionStartLba.HasValue)
            present++;
        if (a.PartitionLength.HasValue && b.PartitionLength.HasValue)
            present++;
        if (!string.IsNullOrEmpty(a.FileSystem) && !string.IsNullOrEmpty(b.FileSystem))
            present++;
        if (a.Capacity.HasValue && b.Capacity.HasValue)
            present++;
        return present >= 2;
    }

    // ------------------------------------------------------------------ //
    //  Internal scored-candidate record                                   //
    // ------------------------------------------------------------------ //

    private sealed record ScoredCandidate(
        string CardInstanceId,
        CardIdentityEvidence Evidence,
        bool VMatch,
        bool DMatch,
        bool CMatch);
}
