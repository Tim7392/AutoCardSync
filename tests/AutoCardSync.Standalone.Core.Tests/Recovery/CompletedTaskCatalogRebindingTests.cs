using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class CompletedTaskCatalogRebindingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-CatalogRebinding", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Find_by_card_returns_completed_candidates_across_old_source_identities()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Guid cardInstanceId = Guid.NewGuid();
        StandaloneCompletedTaskCandidate older = Candidate(
            cardInstanceId,
            "source-at-old-endpoint",
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"));
        StandaloneCompletedTaskCandidate newer = Candidate(
            cardInstanceId,
            "source-at-newer-endpoint",
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        StandaloneCompletedTaskCandidate otherCard = Candidate(
            Guid.NewGuid(),
            "source-at-newer-endpoint",
            DateTimeOffset.Parse("2026-09-10T00:00:00Z"));
        await PersistAsync(paths, older);
        await PersistAsync(paths, newer);
        await PersistAsync(paths, otherCard);
        var catalog = new StandaloneCompletedTaskCatalog(paths);

        IReadOnlyList<StandaloneCompletedTaskCandidate> candidates =
            await catalog.FindCompletedCandidatesByCardInstanceIdAsync(
                cardInstanceId,
                CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(newer.Journal.TaskId, candidates[0].Journal.TaskId);
        Assert.Equal(older.Journal.TaskId, candidates[1].Journal.TaskId);
        Assert.Contains(candidates, candidate =>
            candidate.Journal.SourceIdentity == "source-at-old-endpoint");
    }

    [Fact]
    public async Task Find_by_card_rejects_empty_card_identity()
    {
        var catalog = new StandaloneCompletedTaskCatalog(new StandaloneDataPaths(_root));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.FindCompletedCandidatesByCardInstanceIdAsync(
                Guid.Empty,
                CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task PersistAsync(
        StandaloneDataPaths paths,
        StandaloneCompletedTaskCandidate candidate)
    {
        await new AtomicJsonFileStore<StandaloneTaskJournal>(
            paths.GetTaskJournalPath(candidate.Journal.TaskId)).SaveAsync(
                candidate.Journal,
                CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneCompletionReceipt>(
            paths.GetCompletionReceiptPath(candidate.Journal.TaskId)).SaveAsync(
                candidate.Receipt,
                CancellationToken.None);
    }

    private static StandaloneCompletedTaskCandidate Candidate(
        Guid cardInstanceId,
        string sourceIdentity,
        DateTimeOffset completedAtUtc)
    {
        Guid taskId = Guid.NewGuid();
        var journal = new StandaloneTaskJournal
        {
            TaskId = taskId,
            CardInstanceId = cardInstanceId,
            SourceIdentity = sourceIdentity,
            TargetMode = StandaloneTargetMode.LocalOnly,
            ManifestHash = $"manifest-{taskId:N}",
            LocalTargetIdentity = "local",
            LocalTargetRoot = @"D:\Local",
            NasTargetIdentity = string.Empty,
            NasTargetRoot = string.Empty,
            Files = [],
            UpdatedAtUtc = completedAtUtc,
            LocalCompletionReceiptPersisted = true,
        };
        var receipt = new StandaloneCompletionReceipt
        {
            TaskId = taskId,
            CardInstanceId = cardInstanceId,
            SourceIdentity = sourceIdentity,
            TargetMode = StandaloneTargetMode.LocalOnly,
            ManifestHash = journal.ManifestHash,
            LocalTargetIdentity = journal.LocalTargetIdentity,
            NasTargetIdentity = string.Empty,
            CompletedAtUtc = completedAtUtc,
            Files = [],
            SafeToRemoveCard = true,
        };
        return new(journal, receipt);
    }
}
