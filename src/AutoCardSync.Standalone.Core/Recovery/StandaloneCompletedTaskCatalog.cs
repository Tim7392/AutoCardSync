using System.Text.Json;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record StandaloneCompletedTaskCandidate(
    StandaloneTaskJournal Journal,
    StandaloneCompletionReceipt Receipt);

public sealed class StandaloneCompletedTaskCatalog(StandaloneDataPaths paths)
{
    private readonly StandaloneDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<IReadOnlyList<StandaloneCompletedTaskCandidate>> FindCompletedCandidatesAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (!Directory.Exists(_paths.TasksDirectory))
            return [];

        IReadOnlySet<Guid> abandonedTaskIds = await new StandaloneAbandonedTaskStore(
            _paths.AbandonedTasksFile).GetTaskIdsAsync(cancellationToken);
        var candidates = new List<StandaloneCompletedTaskCandidate>();
        foreach (string path in EnumerateJournalLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandaloneTaskJournal? journal = await TryLoadJournalAsync(path, cancellationToken);
            if (journal is null ||
                abandonedTaskIds.Contains(journal.TaskId) ||
                !string.Equals(journal.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            {
                continue;
            }

            string receiptPath = _paths.GetCompletionReceiptPath(journal.TaskId);
            if (!File.Exists(receiptPath))
                continue;

            StandaloneCompletionReceipt receipt =
                await new AtomicJsonFileStore<StandaloneCompletionReceipt>(receiptPath).LoadAsync(cancellationToken) ??
                throw new InvalidDataException($"Completion receipt '{receiptPath}' could not be loaded.");
            candidates.Add(new(journal, receipt));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Receipt.CompletedAtUtc)
            .ThenByDescending(candidate => candidate.Journal.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<StandaloneCompletedTaskCandidate?> FindCompletedCandidateAsync(
        string sourceIdentity,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        if (!Directory.Exists(_paths.TasksDirectory))
            return null;

        IReadOnlySet<Guid> abandonedTaskIds = await new StandaloneAbandonedTaskStore(
            _paths.AbandonedTasksFile).GetTaskIdsAsync(cancellationToken);
        foreach (string path in EnumerateJournalLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandaloneTaskJournal? journal = await TryLoadJournalAsync(path, cancellationToken);
            if (journal is null ||
                abandonedTaskIds.Contains(journal.TaskId) ||
                journal.TaskId != taskId ||
                !string.Equals(journal.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            {
                continue;
            }

            string receiptPath = _paths.GetCompletionReceiptPath(taskId);
            if (!File.Exists(receiptPath))
                return null;
            StandaloneCompletionReceipt receipt =
                await new AtomicJsonFileStore<StandaloneCompletionReceipt>(receiptPath).LoadAsync(cancellationToken) ??
                throw new InvalidDataException($"Completion receipt '{receiptPath}' could not be loaded.");
            return new(journal, receipt);
        }

        return null;
    }
    public async Task<StandaloneTaskJournal?> FindResumeCandidateAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        return await FindResumeCandidateCoreAsync(
            journal => string.Equals(journal.SourceIdentity, sourceIdentity, StringComparison.Ordinal),
            cancellationToken);
    }

    public async Task<StandaloneTaskJournal?> FindResumeCandidateByCardInstanceIdAsync(
        Guid cardInstanceId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        return await FindResumeCandidateCoreAsync(
            journal => journal.CardInstanceId == cardInstanceId,
            cancellationToken);
    }

    private async Task<StandaloneTaskJournal?> FindResumeCandidateCoreAsync(
        Func<StandaloneTaskJournal, bool> matches,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.TasksDirectory))
            return null;

        IReadOnlySet<Guid> abandonedTaskIds = await new StandaloneAbandonedTaskStore(
            _paths.AbandonedTasksFile).GetTaskIdsAsync(cancellationToken);
        StandaloneTaskJournal? newest = null;
        foreach (string path in EnumerateJournalLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandaloneTaskJournal? journal = await TryLoadJournalAsync(path, cancellationToken);
            if (journal is null ||
                abandonedTaskIds.Contains(journal.TaskId) ||
                !matches(journal))
            {
                continue;
            }

            // Only a structurally and semantically valid receipt suppresses resume. A corrupt or
            // mismatched receipt cannot prove completion; preserve it for audit and resume from the
            // journal so the normal copy/verify path can reconstruct trustworthy evidence.
            if (await HasValidCompletionReceiptAsync(journal, cancellationToken))
                continue;

            if (newest is null || journal.UpdatedAtUtc > newest.UpdatedAtUtc)
                newest = journal;
        }

        return newest;
    }

    private async Task<bool> HasValidCompletionReceiptAsync(
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        string receiptPath = _paths.GetCompletionReceiptPath(journal.TaskId);
        if (!File.Exists(receiptPath))
            return false;

        StandaloneCompletionReceipt? receipt;
        try
        {
            receipt = await new AtomicJsonFileStore<StandaloneCompletionReceipt>(receiptPath)
                .LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _ = exception;
            CorruptStateFileRecovery.Preserve(receiptPath);
            return false;
        }

        if (receipt is not null && StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).IsValid)
            return true;

        CorruptStateFileRecovery.Preserve(receiptPath);
        return false;
    }

    private static async Task<StandaloneTaskJournal?> TryLoadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await new StandaloneTaskJournalStore(path).LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or pre-commit journal is isolated. It is never reused or deleted,
            // and cannot turn into a successful completion fact for another task.
            return null;
        }
    }
    private IEnumerable<string> EnumerateJournalLocations()
    {
        foreach (string path in Directory.EnumerateFiles(_paths.TasksDirectory, "*.json"))
            yield return path;
        foreach (string path in Directory.EnumerateDirectories(_paths.TasksDirectory))
        {
            if (File.Exists(Path.Combine(path, "task.json")))
                yield return path;
        }
    }
}
