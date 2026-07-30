using System.Text.Json;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record StandaloneAbandonedTaskRecord
{
    public required Guid TaskId { get; init; }
    public required string SourceIdentity { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset AbandonedAtUtc { get; init; }
}

public sealed record StandaloneAbandonedTaskDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<StandaloneAbandonedTaskRecord> Records { get; init; } = [];
}

public sealed class StandaloneAbandonedTaskStore
{
    private readonly AtomicJsonFileStore<StandaloneAbandonedTaskDocument> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StandaloneAbandonedTaskStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _store = new AtomicJsonFileStore<StandaloneAbandonedTaskDocument>(path);
    }

    public async Task<IReadOnlySet<Guid>> GetTaskIdsAsync(CancellationToken cancellationToken)
    {
        StandaloneAbandonedTaskDocument document = await LoadRecoveringCorruptionAsync(cancellationToken);
        return document.Records.Select(record => record.TaskId).ToHashSet();
    }

    public async Task AbandonAsync(
        Guid taskId,
        string sourceIdentity,
        string reason,
        CancellationToken cancellationToken)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneAbandonedTaskDocument document = await LoadRecoveringCorruptionAsync(cancellationToken);
            StandaloneAbandonedTaskRecord? existing = document.Records.SingleOrDefault(
                record => record.TaskId == taskId);
            if (existing is not null)
            {
                if (!string.Equals(existing.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException("The abandoned task identity is bound to another source.");
                return;
            }

            StandaloneAbandonedTaskDocument updated = document with
            {
                Records = document.Records.Append(new StandaloneAbandonedTaskRecord
                {
                    TaskId = taskId,
                    SourceIdentity = sourceIdentity,
                    Reason = reason.Trim(),
                    AbandonedAtUtc = DateTimeOffset.UtcNow,
                }).ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StandaloneAbandonedTaskDocument> LoadRecoveringCorruptionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            StandaloneAbandonedTaskDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneAbandonedTaskDocument();
            Validate(document);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _ = exception;
            if (File.Exists(_store.FilePath))
                CorruptStateFileRecovery.Preserve(_store.FilePath);
            var reset = new StandaloneAbandonedTaskDocument();
            await _store.SaveAsync(reset, cancellationToken);
            return reset;
        }
    }

    private static void Validate(StandaloneAbandonedTaskDocument document)
    {
        if (document.SchemaVersion != 1 || document.Records is null ||
            document.Records.Any(record =>
                record is null ||
                record.TaskId == Guid.Empty ||
                string.IsNullOrWhiteSpace(record.SourceIdentity) ||
                string.IsNullOrWhiteSpace(record.Reason)) ||
            document.Records.Select(record => record.TaskId).Distinct().Count() != document.Records.Count)
        {
            throw new InvalidDataException("The abandoned task document is invalid.");
        }
    }
}
