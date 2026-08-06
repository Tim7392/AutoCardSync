using System.IO;
using System.Globalization;
using System.Text.Json;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace AutoCardSync.Standalone.Services;

internal sealed class StandaloneImportHistoryReader(
    StandaloneDataPaths paths,
    ILogger logger)
{
    private readonly StandaloneDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IReadOnlyList<StandaloneImportHistoryItemDto>> ReadAsync(
        IReadOnlyDictionary<Guid, string> configuredCardNames,
        IReadOnlySet<Guid> baselineCardIds,
        CancellationToken cancellationToken)
    {
        string[] receiptPaths;
        try
        {
            if (!Directory.Exists(_paths.ReceiptsDirectory))
                return [];
            receiptPaths = Directory.EnumerateFiles(
                    _paths.ReceiptsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "history.receipts_unavailable ErrorType={ErrorType}.", exception.GetType().Name);
            return [];
        }

        var items = new List<StandaloneImportHistoryItemDto>();
        foreach (string receiptPath in receiptPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                StandaloneCompletionReceipt? receipt = await new AtomicJsonFileStore<StandaloneCompletionReceipt>(
                    receiptPath).LoadAsync(cancellationToken);
                if (receipt is null)
                {
                    WarnSkipped("receipt_missing");
                    continue;
                }

                string? journalLocation = FindJournalLocation(receipt.TaskId);
                if (journalLocation is null)
                {
                    WarnSkipped("journal_missing");
                    continue;
                }

                StandaloneTaskJournal? journal = await new StandaloneTaskJournalStore(journalLocation)
                    .LoadAsync(cancellationToken);
                if (journal is null || !journal.LocalCompletionReceiptPersisted ||
                    !StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).IsValid)
                {
                    WarnSkipped("receipt_binding_invalid");
                    continue;
                }

                items.Add(new StandaloneImportHistoryItemDto
                {
                    TaskId = receipt.TaskId.ToString("D"),
                    CardInstanceId = receipt.CardInstanceId.ToString("D"),
                    CardDisplayName = ResolveCardDisplayName(
                        receipt.CardInstanceId, configuredCardNames, baselineCardIds),
                    CompletedAtUtc = receipt.CompletedAtUtc.ToUniversalTime().ToString(
                        "O", CultureInfo.InvariantCulture),
                    FileCount = receipt.Files.Count,
                    TotalBytes = TotalBytes(receipt),
                    TargetMode = TargetModeKey(receipt.TargetMode),
                    SafeToRemoveCard = true,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or ArgumentException or OverflowException)
            {
                _logger.LogWarning(exception, "history.receipt_skipped ErrorType={ErrorType}.", exception.GetType().Name);
            }
        }

        return items
            .OrderByDescending(item => item.CompletedAtUtc, StringComparer.Ordinal)
            .ThenByDescending(item => item.TaskId, StringComparer.Ordinal)
            .Take(100)
            .ToArray();
    }

    private string? FindJournalLocation(Guid taskId)
    {
        if (taskId == Guid.Empty)
            return null;

        string sharded = _paths.GetTaskJournalDirectory(taskId);
        if (Directory.Exists(sharded) && File.Exists(Path.Combine(sharded, "task.json")))
            return sharded;

        string legacy = _paths.GetTaskJournalPath(taskId);
        return File.Exists(legacy) ? legacy : null;
    }

    private static string ResolveCardDisplayName(
        Guid cardInstanceId,
        IReadOnlyDictionary<Guid, string> configuredCardNames,
        IReadOnlySet<Guid> baselineCardIds)
    {
        if (configuredCardNames.TryGetValue(cardInstanceId, out string? displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }
        return baselineCardIds.Contains(cardInstanceId) ? "已识别素材卡" : "素材卡";
    }

    private static long TotalBytes(StandaloneCompletionReceipt receipt)
    {
        long total = 0;
        foreach (StandaloneCompletionFileFact file in receipt.Files)
        {
            if (file.Length < 0)
                throw new InvalidDataException("Completion receipt contains an invalid file length.");
            total = checked(total + file.Length);
        }
        return total;
    }

    private void WarnSkipped(string reason) =>
        _logger.LogWarning("history.receipt_skipped Reason={Reason}.", reason);

    private static string TargetModeKey(StandaloneTargetMode targetMode) => targetMode switch
    {
        StandaloneTargetMode.LocalOnly => "local-only",
        StandaloneTargetMode.NasOnly => "nas-only",
        StandaloneTargetMode.LocalAndNas => "local-and-nas",
        _ => "unknown",
    };
}
