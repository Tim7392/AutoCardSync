using AutoCardSync.Domain.Manifests;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record CompletedInventoryEvidenceGroup(
    StandaloneCompletedTaskCandidate Candidate,
    IReadOnlySet<string> RelativePaths);

public sealed record CompletedInventoryEvidencePlan(
    IReadOnlyList<CompletedInventoryEvidenceGroup> Groups,
    int IncludedFileCount,
    int UncoveredFileCount);

public static class CompletedInventoryEvidencePlanner
{
    public static CompletedInventoryEvidencePlan Create(
        TaskManifest inventory,
        Guid cardInstanceId,
        string sourceIdentity,
        IReadOnlyList<StandaloneCompletedTaskCandidate> candidates,
        CompletedTaskSourceRebindingAuthorization? sourceRebindingAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentNullException.ThrowIfNull(candidates);
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        sourceRebindingAuthorization?.EnsureMatches(cardInstanceId, sourceIdentity);

        Dictionary<string, ManifestEntry> uncovered = BuildCurrentInventory(inventory);
        int includedFileCount = uncovered.Count;
        var validatedCandidates = new List<StandaloneCompletedTaskCandidate>(candidates.Count);
        foreach (StandaloneCompletedTaskCandidate? candidate in candidates)
        {
            if (candidate is null || candidate.Journal is null || candidate.Receipt is null)
                throw new InvalidDataException("The completed-task candidate collection contains an invalid candidate.");

            ValidateJournalPaths(candidate.Journal);
            validatedCandidates.Add(candidate);
        }

        var groups = new List<CompletedInventoryEvidenceGroup>();
        foreach (StandaloneCompletedTaskCandidate candidate in validatedCandidates)
        {
            if (uncovered.Count == 0)
                break;
            if (candidate.Journal.CardInstanceId != cardInstanceId ||
                (sourceRebindingAuthorization is null &&
                    !string.Equals(candidate.Journal.SourceIdentity, sourceIdentity, StringComparison.Ordinal)))
            {
                continue;
            }

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (StandaloneFileJournal file in candidate.Journal.Files)
            {
                if (!uncovered.TryGetValue(file.RelativePath, out ManifestEntry? entry) ||
                    entry.FileSize != file.Length ||
                    string.IsNullOrWhiteSpace(entry.SourceFileId) ||
                    string.IsNullOrWhiteSpace(file.SourceFileIdentity) ||
                    !string.Equals(entry.SourceFileId, file.SourceFileIdentity, StringComparison.Ordinal))
                {
                    continue;
                }

                relativePaths.Add(entry.RelativePath);
            }

            if (relativePaths.Count == 0)
                continue;

            foreach (string relativePath in relativePaths)
                uncovered.Remove(relativePath);
            groups.Add(new(candidate, relativePaths));
        }

        return new(groups.AsReadOnly(), includedFileCount, uncovered.Count);
    }

    private static Dictionary<string, ManifestEntry> BuildCurrentInventory(TaskManifest inventory)
    {
        var current = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestEntry? entry in inventory.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath))
                throw new InvalidDataException("The current inventory contains an invalid relative path.");
            if (entry.Excluded)
                continue;
            if (!current.TryAdd(entry.RelativePath, entry))
            {
                throw new InvalidDataException(
                    $"The current inventory contains a duplicate relative path: '{entry.RelativePath}'.");
            }
        }

        return current;
    }

    private static void ValidateJournalPaths(StandaloneTaskJournal journal)
    {
        if (journal.Files is null)
            throw new InvalidDataException("A completed task journal has no file collection.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StandaloneFileJournal? file in journal.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath))
                throw new InvalidDataException("A completed task journal contains an invalid relative path.");
            if (!paths.Add(file.RelativePath))
            {
                throw new InvalidDataException(
                    $"A completed task journal contains a duplicate relative path: '{file.RelativePath}'.");
            }
        }
    }
}
