using System.Runtime.Versioning;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record CompletedTaskSourceRebindingAuthorization(
    Guid CardInstanceId,
    string CurrentSourceIdentity)
{
    internal void EnsureMatches(Guid currentCardInstanceId, string currentSourceIdentity)
    {
        if (CardInstanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(CurrentSourceIdentity) ||
            CardInstanceId != currentCardInstanceId ||
            !string.Equals(CurrentSourceIdentity, currentSourceIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source rebinding authorization does not match the current card binding.");
        }
    }
}

[SupportedOSPlatform("windows")]
public static class CompletedTaskEvidenceVerifier
{
    public static async Task<FinalPublishedObjectLease> AcquireVerifiedLeaseAsync(
        string sourceRoot,
        StandaloneCompletedTaskCandidate candidate,
        RecoveryIdentitySnapshot current,
        string currentLocalTargetRoot,
        string currentNasTargetRoot,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? includedRelativePaths = null,
        CompletedTaskSourceRebindingAuthorization? sourceRebindingAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Journal);
        ArgumentNullException.ThrowIfNull(candidate.Receipt);
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        sourceRebindingAuthorization?.EnsureMatches(
            current.CardInstanceId,
            current.SourceIdentity);

        CompletedTaskReuseEligibility eligibility = CompletedTaskReuseGuard.EvaluatePersisted(
            candidate.Journal,
            candidate.Receipt,
            current,
            currentLocalTargetRoot,
            currentNasTargetRoot);
        if (!eligibility.CanReuse &&
            sourceRebindingAuthorization is not null &&
            candidate.Journal.CardInstanceId == current.CardInstanceId &&
            !string.Equals(
                candidate.Journal.SourceIdentity,
                current.SourceIdentity,
                StringComparison.Ordinal))
        {
            RecoveryIdentitySnapshot persistedIdentityCurrent = current with
            {
                SourceIdentity = candidate.Journal.SourceIdentity,
            };
            eligibility = CompletedTaskReuseGuard.EvaluatePersisted(
                candidate.Journal,
                candidate.Receipt,
                persistedIdentityCurrent,
                currentLocalTargetRoot,
                currentNasTargetRoot);
        }
        if (!eligibility.CanReuse)
        {
            throw new InvalidDataException(
                $"Persisted completion evidence cannot be reused: {string.Join(", ", eligibility.Reasons)}.");
        }

        StandaloneTaskJournal journal = includedRelativePaths is null
            ? candidate.Journal
            : CreateSubsetProjection(candidate.Journal, includedRelativePaths);

        return await FinalPublishedObjectVerifier.AcquireVerifiedLeaseAsync(
            sourceRoot,
            journal,
            cancellationToken).ConfigureAwait(false);
    }

    private static StandaloneTaskJournal CreateSubsetProjection(
        StandaloneTaskJournal journal,
        IReadOnlySet<string> includedRelativePaths)
    {
        if (includedRelativePaths.Count == 0)
            throw new InvalidDataException("The completed-task evidence subset cannot be empty.");

        var requestedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? relativePath in includedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException("The completed-task evidence subset contains an empty path.");
            if (!requestedPaths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"The completed-task evidence subset contains a duplicate path: '{relativePath}'.");
            }
        }

        var journalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StandaloneFileJournal? file in journal.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.RelativePath))
                throw new InvalidDataException("The completed task journal contains an invalid file path.");
            if (!journalPaths.Add(file.RelativePath))
            {
                throw new InvalidDataException(
                    $"The completed task journal contains a duplicate file path: '{file.RelativePath}'.");
            }
        }

        foreach (string relativePath in requestedPaths)
        {
            if (!journalPaths.Contains(relativePath))
            {
                throw new InvalidDataException(
                    $"The completed-task evidence subset contains an unknown path: '{relativePath}'.");
            }
        }

        StandaloneFileJournal[] selectedFiles = journal.Files
            .Where(file => requestedPaths.Contains(file.RelativePath))
            .ToArray();
        return journal with { Files = selectedFiles };
    }
}
