using System.Diagnostics;
using System.Runtime.Versioning;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Application.Copying;

public sealed record FreshTargetPlan(
    Guid TargetId,
    string Role,
    string TargetRoot,
    string FaultDomainId,
    string StorageIdentity,
    string? NasIdentity,
    IProgress<TargetCopyStatus>? Progress = null);

public sealed record SourceReadStatus
{
    public required Guid TaskId { get; init; }
    public long BytesRead { get; init; }
    public long TotalBytes { get; init; }
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
    public string? CurrentFile { get; init; }
    public CopyPhase Phase { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record FreshTransferIoMetrics
{
    public required long PayloadBytes { get; init; }
    public long SourceContentBytesRead { get; init; }
    public long RelayBytesRead { get; init; }
    public long TargetBytesWritten { get; init; }
    public long TargetTemporaryBytesRead { get; init; }
    public long TargetFinalBytesRead { get; init; }
    public long RecoveryPrefixBytesRead { get; init; }
    public long PeakReadAheadBeyondDurableCheckpointBytes { get; init; }
    public long EndOfTaskBatchFullRehashBytes { get; init; }
    public long JournalBytesWritten { get; init; }
    public int JournalAtomicWrites { get; init; }
    public int PeakIdentityLeaseCount { get; init; }
    public int PeakProcessHandleCount { get; init; }
    public long PeakManagedMemoryBytes { get; init; }
    public long PeakProcessWorkingSetBytes { get; init; }
    public required IReadOnlyDictionary<string, TimeSpan> StageElapsed { get; init; }

    public long FreshPayloadIoBytes =>
        SourceContentBytesRead + TargetBytesWritten +
        TargetTemporaryBytesRead + TargetFinalBytesRead;

    public double FreshIoAmplification => PayloadBytes == 0
        ? 0
        : FreshPayloadIoBytes / (double)PayloadBytes;
}

[SupportedOSPlatform("windows")]
public sealed class FreshTransferExecution : IAsyncDisposable
{
    private readonly IReadOnlyList<SourceReadContinuityLease> _sourceLeases;
    private readonly IReadOnlyList<TargetDirectoryContinuityLease> _targetLeases;
    private readonly IReadOnlyList<PublishResult> _publishedLeases;
    private bool _disposed;

    internal FreshTransferExecution(
        TaskManifest contentManifest,
        IReadOnlyList<FileCopyResult> results,
        FreshTransferIoMetrics metrics,
        IReadOnlyList<SourceReadContinuityLease> sourceLeases,
        IReadOnlyList<TargetDirectoryContinuityLease> targetLeases,
        IReadOnlyList<PublishResult> publishedLeases)
    {
        ContentManifest = contentManifest;
        Results = results;
        Metrics = metrics;
        _sourceLeases = sourceLeases;
        _targetLeases = targetLeases;
        _publishedLeases = publishedLeases;
    }

    public TaskManifest ContentManifest { get; }
    public IReadOnlyList<FileCopyResult> Results { get; }
    public FreshTransferIoMetrics Metrics { get; }

    public void RevalidateContinuity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (SourceReadContinuityLease source in _sourceLeases)
        {
            SourceContinuityResult continuity = source.VerifyHandleContinuity(source.Identity);
            if (!continuity.IsContinuous)
                throw new IOException($"Source identity lease changed: {continuity.MismatchDetail}");
        }
        foreach (TargetDirectoryContinuityLease target in _targetLeases)
            target.EnsureContinuous();
        foreach (PublishResult published in _publishedLeases)
            published.EnsureHandleContinuous();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int index = _publishedLeases.Count - 1; index >= 0; index--)
            await _publishedLeases[index].DisposeAsync();
        for (int index = _targetLeases.Count - 1; index >= 0; index--)
            _targetLeases[index].Dispose();
        for (int index = _sourceLeases.Count - 1; index >= 0; index--)
            await _sourceLeases[index].DisposeAsync();
    }
}

[SupportedOSPlatform("windows")]
public sealed class FreshTransferCoordinator
{
    private readonly ChunkedFileCopier _copier = new();
    private readonly AtomicFilePublisher _publisher = new();

    public async Task<FreshTransferExecution> ExecuteAsync(
        TaskManifest inventoryManifest,
        string sourceRoot,
        IReadOnlyList<FreshTargetPlan> targets,
        StandaloneTaskJournalStore journalStore,
        bool resumeExistingTemps,
        CancellationToken cancellationToken,
        IProgress<SourceReadStatus>? sourceProgress = null)
    {
        ArgumentNullException.ThrowIfNull(inventoryManifest);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(journalStore);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Fresh transfer identity binding requires Windows APIs.");
        if (inventoryManifest.FrozenAt is null || string.IsNullOrWhiteSpace(inventoryManifest.ManifestHash))
            throw new InvalidDataException("The inventory manifest must be frozen before staging.");
        if (inventoryManifest.Entries.Where(entry => !entry.Excluded).Any(entry => !string.IsNullOrWhiteSpace(entry.SourceHash)))
            throw new InvalidDataException("Fresh staging requires a metadata-only inventory manifest.");
        if (targets.Count is < 1 or > 2 || targets.Select(target => target.Role).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new InvalidDataException("Fresh transfer requires one or two distinct selected targets.");

        var stageTimes = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var totalStopwatch = Stopwatch.StartNew();
        var stagingStopwatch = Stopwatch.StartNew();
        var faultDomainResolver = new FaultDomainResolver();
        var continuityGuard = new SourceHandleContinuityGuard();
        var sourceLeases = new List<SourceReadContinuityLease>(
            checked(inventoryManifest.TotalFiles * targets.Count));
        var targetLeases = new List<TargetDirectoryContinuityLease>(checked(inventoryManifest.TotalFiles * targets.Count));
        var publishLeases = new List<PublishResult>(checked(inventoryManifest.TotalFiles * targets.Count));
        var stagedFiles = new List<StagedFile>(inventoryManifest.TotalFiles);
        var results = new List<FileCopyResult>(checked(inventoryManifest.TotalFiles * targets.Count));
        var sourceHashes = new Dictionary<Guid, string>();
        long sourceBytesRead = 0;
        long relayBytesRead = 0;
        long completedSourceBytes = 0;
        int completedSourceFiles = 0;
        long targetBytesWritten = 0;
        long targetTempBytesRead = 0;
        long targetFinalBytesRead = 0;
        long recoveryPrefixBytesRead = 0;
        long peakReadAheadBeyondDurableCheckpointBytes = 0;
        int peakLeases = 0;
        int peakProcessHandles = Process.GetCurrentProcess().HandleCount;
        long peakManaged = GC.GetTotalMemory(false);
        var targetStatus = targets.ToDictionary(
            target => target.TargetId,
            target => new MutableTargetStatus(target));

        try
        {
            StandaloneTaskJournal journal = await journalStore.LoadAsync(cancellationToken) ??
                throw new InvalidOperationException("The Fresh task journal must be initialized before staging.");
            EnsureFreshJournalBinding(inventoryManifest, journal, targets);

            foreach (FreshTargetPlan target in targets)
            {
                FaultDomainInfo openedTarget = DualTargetCopyCoordinator.ResolveTargetDomain(
                    faultDomainResolver, target.TargetRoot, target.NasIdentity);
                if (!string.Equals(openedTarget.FaultDomainId, target.FaultDomainId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(openedTarget.StorageIdentity, target.StorageIdentity, StringComparison.Ordinal))
                {
                    throw new IOException($"Selected target identity changed before staging '{target.TargetRoot}'.");
                }
            }

            ManifestEntry[] selectedEntries = inventoryManifest.Entries
                .Where(entry => !entry.Excluded)
                .ToArray();
            var sourceStage = new Dictionary<Guid, SourceStagedFile>();
            var stagedTargetsByEntry = selectedEntries.ToDictionary(
                entry => entry.Id,
                _ => new Dictionary<Guid, StagedTarget>());
            FreshTargetPlan? localTarget = targets.SingleOrDefault(target =>
                string.Equals(target.Role, "local", StringComparison.Ordinal));
            FreshTargetPlan? nasTarget = targets.SingleOrDefault(target =>
                string.Equals(target.Role, "nas", StringComparison.Ordinal));
            FreshTargetPlan directSourceTarget = localTarget ?? nasTarget ??
                throw new InvalidDataException("A selected Fresh target is required.");

            var directStageStopwatch = Stopwatch.StartNew();
            await StageFromSourceAsync(directSourceTarget);
            directStageStopwatch.Stop();
            stageTimes[localTarget is null ? "nas-staging" : "local-staging"] =
                directStageStopwatch.Elapsed;

            if (localTarget is not null && nasTarget is not null)
            {
                var relayStopwatch = Stopwatch.StartNew();
                await StageNasFromLocalAsync(nasTarget);
                relayStopwatch.Stop();
                stageTimes["nas-relay-from-local"] = relayStopwatch.Elapsed;
            }

            foreach (ManifestEntry entry in selectedEntries)
            {
                SourceStagedFile source = sourceStage[entry.Id];
                StagedTarget[] orderedTargets = targets
                    .Select(target => stagedTargetsByEntry[entry.Id][target.TargetId])
                    .ToArray();
                stagedFiles.Add(new StagedFile(entry, source.SourceLease, orderedTargets));
            }

            async Task StageFromSourceAsync(FreshTargetPlan target)
            {
                MutableTargetStatus state = targetStatus[target.TargetId];
                var targetFaultDomainResolver = new FaultDomainResolver();
                foreach (ManifestEntry entry in selectedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sourcePath = SafePathResolver.ResolveSafePath(sourceRoot, entry.RelativePath);
                    SourceReadContinuityLease sourceLease =
                        continuityGuard.AcquireMetadataReadLease(sourcePath);
                    sourceLeases.Add(sourceLease);
                    EnsureInventoryIdentity(entry, sourceLease.Identity);
                    peakLeases = Math.Max(
                        peakLeases,
                        sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(
                        peakProcessHandles,
                        Process.GetCurrentProcess().HandleCount);

                    StandaloneFileJournal journalFile = journal.Files.Single(file => file.FileId == entry.Id);
                    StagedTarget staged = OpenTargetForStaging(
                        inventoryManifest,
                        entry,
                        target,
                        GetTargetJournal(journalFile, target.Role),
                        targetFaultDomainResolver,
                        resumeExistingTemps,
                        journalStore,
                        cancellationToken);
                    targetLeases.Add(staged.DirectoryLease);
                    peakLeases = Math.Max(
                        peakLeases,
                        sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(
                        peakProcessHandles,
                        Process.GetCurrentProcess().HandleCount);

                    try
                    {
                        await journalStore.OnFileStartedAsync(new CopyJournalFileStarted(
                            inventoryManifest.TaskId,
                            entry.Id,
                            target.TargetId,
                            entry.RelativePath,
                            sourceLease.Identity.FileId,
                            staged.OpenedTempPath,
                            staged.OpenedFinalPath,
                            entry.FileSize,
                            ExpectedSha256: null,
                            TemporaryObjectId: SourceHandleContinuityGuard.FormatFileId(staged.TempIdentity)),
                            cancellationToken);
                        long completedBeforeFile = state.CompletedStagingBytes;
                        var targetProgress = new InlineProgress<CopyProgress>(value =>
                        {
                            state.CurrentFileBytes = value.BytesCopied;
                            state.Report(
                                inventoryManifest,
                                CopyPhase.Copying,
                                entry.RelativePath,
                                completed: false);
                            ReportSourceProgress(sourceProgress, new SourceReadStatus
                            {
                                TaskId = inventoryManifest.TaskId,
                                BytesRead = Math.Min(
                                    inventoryManifest.TotalBytes,
                                    completedSourceBytes + value.BytesCopied),
                                TotalBytes = inventoryManifest.TotalBytes,
                                CompletedFiles = completedSourceFiles,
                                TotalFiles = inventoryManifest.TotalFiles,
                                CurrentFile = entry.RelativePath,
                                Phase = CopyPhase.Copying,
                                IsComplete = false,
                            });
                        });
                        OpenedSourceCopyResult copied = await _copier.CopyOpenedSourceAsync(
                            sourceLease,
                            [staged.CopyTarget],
                            targetProgress,
                            cancellationToken);
                        sourceBytesRead += copied.SourceBytesRead;
                        completedSourceBytes += entry.FileSize;
                        completedSourceFiles++;
                        targetBytesWritten += staged.CopyTarget.BytesWritten;
                        recoveryPrefixBytesRead += staged.CopyTarget.PrefixBytesRead;
                        peakReadAheadBeyondDurableCheckpointBytes = Math.Max(
                            peakReadAheadBeyondDurableCheckpointBytes,
                            copied.PeakReadAheadBeyondDurableCheckpointBytes);
                        sourceHashes.Add(entry.Id, copied.SourceSha256);
                        await journalStore.RecordSourceContentAsync(
                            inventoryManifest.TaskId,
                            entry.Id,
                            sourceLease.Identity.FileId,
                            copied.SourceSha256,
                            cancellationToken);
                        state.CompletedStagingBytes = Math.Min(
                            inventoryManifest.TotalBytes,
                            completedBeforeFile + entry.FileSize);
                        state.CurrentFileBytes = 0;
                        state.Report(
                            inventoryManifest,
                            CopyPhase.Copying,
                            entry.RelativePath,
                            completed: false);
                        ReportSourceProgress(sourceProgress, new SourceReadStatus
                        {
                            TaskId = inventoryManifest.TaskId,
                            BytesRead = Math.Min(inventoryManifest.TotalBytes, completedSourceBytes),
                            TotalBytes = inventoryManifest.TotalBytes,
                            CompletedFiles = completedSourceFiles,
                            TotalFiles = inventoryManifest.TotalFiles,
                            CurrentFile = entry.RelativePath,
                            Phase = CopyPhase.Copying,
                            IsComplete = false,
                        });
                        sourceStage.Add(entry.Id, new SourceStagedFile(
                            sourceLease,
                            copied.SourceSha256,
                            staged));
                        stagedTargetsByEntry[entry.Id].Add(target.TargetId, staged);
                        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await journalStore.OnFileFailedAsync(new CopyJournalFileFailed(
                                inventoryManifest.TaskId,
                                entry.Id,
                                target.TargetId,
                                entry.RelativePath,
                                exception.Message), CancellationToken.None);
                        }
                        catch
                        {
                            // Preserve the original copy failure; the outer task still fails closed.
                        }
                        throw;
                    }
                    finally
                    {
                        await staged.DisposeStagingStreamAsync();
                    }
                }
            }

            async Task StageNasFromLocalAsync(FreshTargetPlan target)
            {
                MutableTargetStatus state = targetStatus[target.TargetId];
                var targetFaultDomainResolver = new FaultDomainResolver();
                foreach (ManifestEntry entry in selectedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SourceStagedFile source = sourceStage[entry.Id];
                    StandaloneFileJournal journalFile = journal.Files.Single(file => file.FileId == entry.Id);
                    StagedTarget staged = OpenTargetForStaging(
                        inventoryManifest,
                        entry,
                        target,
                        GetTargetJournal(journalFile, target.Role),
                        targetFaultDomainResolver,
                        resumeExistingTemps,
                        journalStore,
                        cancellationToken);
                    targetLeases.Add(staged.DirectoryLease);
                    peakLeases = Math.Max(
                        peakLeases,
                        sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(
                        peakProcessHandles,
                        Process.GetCurrentProcess().HandleCount);

                    try
                    {
                        await journalStore.OnFileStartedAsync(new CopyJournalFileStarted(
                            inventoryManifest.TaskId,
                            entry.Id,
                            target.TargetId,
                            entry.RelativePath,
                            source.SourceLease.Identity.FileId,
                            staged.OpenedTempPath,
                            staged.OpenedFinalPath,
                            entry.FileSize,
                            ExpectedSha256: source.SourceHash,
                            TemporaryObjectId: SourceHandleContinuityGuard.FormatFileId(staged.TempIdentity)),
                            cancellationToken);

                        await using SourceReadContinuityLease relayLease =
                            continuityGuard.AcquireMetadataReadLease(source.Target.OpenedTempPath);
                        string expectedRelayIdentity =
                            SourceHandleContinuityGuard.FormatFileId(source.Target.TempIdentity);
                        if (!string.Equals(
                                relayLease.Identity.FileId,
                                expectedRelayIdentity,
                                StringComparison.Ordinal) ||
                            relayLease.Identity.FileSize != entry.FileSize)
                        {
                            throw new IOException(
                                "The completed local temporary object changed before NAS relay.");
                        }

                        long completedBeforeFile = state.CompletedStagingBytes;
                        var targetProgress = new InlineProgress<CopyProgress>(value =>
                        {
                            state.CurrentFileBytes = value.BytesCopied;
                            state.Report(
                                inventoryManifest,
                                CopyPhase.Copying,
                                entry.RelativePath,
                                completed: false);
                        });
                        OpenedSourceCopyResult relayed = await _copier.CopyOpenedSourceAsync(
                            relayLease,
                            [staged.CopyTarget],
                            targetProgress,
                            cancellationToken);
                        if (!string.Equals(
                                relayed.SourceSha256,
                                source.SourceHash,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException(
                                "The local relay content hash no longer matches the source card hash.");
                        }

                        relayBytesRead += relayed.SourceBytesRead;
                        targetBytesWritten += staged.CopyTarget.BytesWritten;
                        recoveryPrefixBytesRead += staged.CopyTarget.PrefixBytesRead;
                        peakReadAheadBeyondDurableCheckpointBytes = Math.Max(
                            peakReadAheadBeyondDurableCheckpointBytes,
                            relayed.PeakReadAheadBeyondDurableCheckpointBytes);
                        state.CompletedStagingBytes = Math.Min(
                            inventoryManifest.TotalBytes,
                            completedBeforeFile + entry.FileSize);
                        state.CurrentFileBytes = 0;
                        state.Report(
                            inventoryManifest,
                            CopyPhase.Copying,
                            entry.RelativePath,
                            completed: false);
                        stagedTargetsByEntry[entry.Id].Add(target.TargetId, staged);
                        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await journalStore.OnFileFailedAsync(new CopyJournalFileFailed(
                                inventoryManifest.TaskId,
                                entry.Id,
                                target.TargetId,
                                entry.RelativePath,
                                exception.Message), CancellationToken.None);
                        }
                        catch
                        {
                            // Preserve the original copy failure; the outer task still fails closed.
                        }
                        throw;
                    }
                    finally
                    {
                        await staged.DisposeStagingStreamAsync();
                    }
                }
            }
            stagingStopwatch.Stop();
            stageTimes["staging"] = stagingStopwatch.Elapsed;
            ReportSourceProgress(sourceProgress, new SourceReadStatus
            {
                TaskId = inventoryManifest.TaskId,
                BytesRead = Math.Min(inventoryManifest.TotalBytes, completedSourceBytes),
                TotalBytes = inventoryManifest.TotalBytes,
                CompletedFiles = completedSourceFiles,
                TotalFiles = inventoryManifest.TotalFiles,
                CurrentFile = null,
                Phase = CopyPhase.TemporaryVerifying,
                IsComplete = false,
            });

            TaskManifest contentManifest = ManifestBuilder.CreateContentManifest(inventoryManifest, sourceHashes);
            await journalStore.FreezeContentManifestAsync(contentManifest.ManifestHash, cancellationToken);
            StandaloneTaskJournal frozenJournal = await journalStore.LoadAsync(cancellationToken) ??
                throw new IOException("The frozen content manifest could not be reread from the task journal.");
            ManifestJournalBindingValidator.EnsureValid(contentManifest, frozenJournal);

            var tempVerificationStopwatch = Stopwatch.StartNew();
            foreach (StagedFile stagedFile in stagedFiles)
            {
                foreach (StagedTarget stagedTarget in stagedFile.Targets)
                {
                    MutableTargetStatus state = targetStatus[stagedTarget.Plan.TargetId];
                    long completedBeforeVerification = state.TemporaryBytesVerified;
                    var verificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                    {
                        state.TemporaryBytesVerified = Math.Min(
                            contentManifest.TotalBytes,
                            completedBeforeVerification + value.BytesVerified);
                        state.Report(
                            contentManifest, CopyPhase.TemporaryVerifying,
                            stagedFile.Entry.RelativePath, completed: false);
                    });
                    state.Report(contentManifest, CopyPhase.TemporaryVerifying, stagedFile.Entry.RelativePath, completed: false);
                    PublishResult published = await _publisher.PublishAsync(
                        stagedTarget.OpenedTempPath,
                        stagedTarget.OpenedFinalPath,
                        sourceHashes[stagedFile.Entry.Id],
                        stagedFile.Entry.FileSize,
                        cancellationToken,
                        stagedTarget.DirectoryLease.EnsureContinuous,
                        stagedTarget.HandleBinding.EnsureOpenedHandle,
                        stagedTarget.TempIdentity,
                        // Re-reading an already-published object is safe and keeps FAT/exFAT
                        // FileId churn from trapping users in permanent name conflicts. Different
                        // content still fails closed and is never overwritten.
                        allowVerifiedExisting: true,
                        temporaryVerificationProgress: verificationProgress);
                    if (!published.Success)
                    {
                        await published.DisposeAsync();
                        throw new IOException(published.Error ?? "Temporary object verification failed.");
                    }
                    state.TemporaryBytesVerified = Math.Min(
                        contentManifest.TotalBytes,
                        completedBeforeVerification + stagedFile.Entry.FileSize);
                    state.Report(
                        contentManifest, CopyPhase.Publishing,
                        stagedFile.Entry.RelativePath, completed: false);
                    targetTempBytesRead += stagedFile.Entry.FileSize;
                    publishLeases.Add(published);
                    stagedTarget.Published = published;
                    peakLeases = Math.Max(peakLeases, sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(peakProcessHandles, Process.GetCurrentProcess().HandleCount);
                }
            }
            tempVerificationStopwatch.Stop();
            stageTimes["temporary-reread-and-publish"] = tempVerificationStopwatch.Elapsed;

            var finalVerificationStopwatch = Stopwatch.StartNew();
            foreach (StagedFile stagedFile in stagedFiles)
            {
                foreach (StagedTarget stagedTarget in stagedFile.Targets)
                {
                    PublishResult published = stagedTarget.Published ??
                        throw new InvalidOperationException("Published target lease is missing.");
                    MutableTargetStatus state = targetStatus[stagedTarget.Plan.TargetId];
                    long completedBeforeVerification = state.FinalBytesVerified;
                    var verificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                    {
                        state.FinalBytesVerified = Math.Min(
                            contentManifest.TotalBytes,
                            completedBeforeVerification + value.BytesVerified);
                        state.Report(
                            contentManifest, CopyPhase.FinalVerifying,
                            stagedFile.Entry.RelativePath, completed: false);
                    });
                    state.Report(contentManifest, CopyPhase.FinalVerifying, stagedFile.Entry.RelativePath, completed: false);
                    await published.EnsureContinuousAsync(cancellationToken, verificationProgress);
                    state.FinalBytesVerified = Math.Min(
                        contentManifest.TotalBytes,
                        completedBeforeVerification + stagedFile.Entry.FileSize);
                    targetFinalBytesRead += stagedFile.Entry.FileSize;
                    await journalStore.OnFileVerifiedAsync(new CopyJournalFileVerified(
                        contentManifest.TaskId,
                        stagedFile.Entry.Id,
                        stagedTarget.Plan.TargetId,
                        stagedFile.Entry.RelativePath,
                        stagedTarget.OpenedFinalPath,
                        published.FileId ?? throw new IOException("Final object identity is missing."),
                        stagedFile.Entry.FileSize,
                        published.Hash ?? sourceHashes[stagedFile.Entry.Id],
                        ReusedExisting: published.ReusedExisting), cancellationToken);
                    state.FilesVerified++;
                    results.Add(new FileCopyResult
                    {
                        FileId = stagedFile.Entry.Id,
                        TargetId = stagedTarget.Plan.TargetId,
                        RelativePath = stagedFile.Entry.RelativePath,
                        Verified = true,
                        Hash = published.Hash,
                        FinalPath = stagedTarget.OpenedFinalPath,
                        TargetFileId = published.FileId,
                    });
                }
            }
            finalVerificationStopwatch.Stop();
            stageTimes["final-reread"] = finalVerificationStopwatch.Elapsed;

            foreach (MutableTargetStatus state in targetStatus.Values)
                state.Report(contentManifest, CopyPhase.Completed, currentFile: null, completed: true);
            ReportSourceProgress(sourceProgress, new SourceReadStatus
            {
                TaskId = contentManifest.TaskId,
                BytesRead = contentManifest.TotalBytes,
                TotalBytes = contentManifest.TotalBytes,
                CompletedFiles = contentManifest.TotalFiles,
                TotalFiles = contentManifest.TotalFiles,
                CurrentFile = null,
                Phase = CopyPhase.Completed,
                IsComplete = true,
            });

            totalStopwatch.Stop();
            stageTimes["total"] = totalStopwatch.Elapsed;
            JournalPersistenceMetrics journalMetrics = journalStore.PersistenceMetrics;
            var metrics = new FreshTransferIoMetrics
            {
                PayloadBytes = contentManifest.TotalBytes,
                SourceContentBytesRead = sourceBytesRead,
                RelayBytesRead = relayBytesRead,
                TargetBytesWritten = targetBytesWritten,
                TargetTemporaryBytesRead = targetTempBytesRead,
                TargetFinalBytesRead = targetFinalBytesRead,
                RecoveryPrefixBytesRead = recoveryPrefixBytesRead,
                PeakReadAheadBeyondDurableCheckpointBytes = peakReadAheadBeyondDurableCheckpointBytes,
                EndOfTaskBatchFullRehashBytes = 0,
                JournalBytesWritten = journalMetrics.BytesWritten,
                JournalAtomicWrites = journalMetrics.AtomicWrites,
                PeakIdentityLeaseCount = peakLeases,
                PeakProcessHandleCount = peakProcessHandles,
                PeakManagedMemoryBytes = peakManaged,
                PeakProcessWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                StageElapsed = stageTimes,
            };
            var execution = new FreshTransferExecution(
                contentManifest,
                results,
                metrics,
                sourceLeases.ToArray(),
                targetLeases.ToArray(),
                publishLeases.ToArray());
            execution.RevalidateContinuity();
            return execution;
        }
        catch
        {
            for (int index = publishLeases.Count - 1; index >= 0; index--)
                await publishLeases[index].DisposeAsync();
            for (int index = targetLeases.Count - 1; index >= 0; index--)
                targetLeases[index].Dispose();
            for (int index = sourceLeases.Count - 1; index >= 0; index--)
                await sourceLeases[index].DisposeAsync();
            throw;
        }
    }

    private static StagedTarget OpenTargetForStaging(
        TaskManifest inventoryManifest,
        ManifestEntry entry,
        FreshTargetPlan plan,
        StandaloneTargetFileJournal journal,
        FaultDomainResolver faultDomainResolver,
        bool resumeExistingTemps,
        StandaloneTaskJournalStore journalStore,
        CancellationToken cancellationToken)
    {
        TargetDirectoryContinuityLease? directoryLease = null;
        FileStream? stream = null;
        try
        {
            DualTargetCopyCoordinator.EnsureTargetStorageIdentity(
                faultDomainResolver,
                plan.TargetRoot,
                plan.FaultDomainId,
                plan.StorageIdentity,
                plan.NasIdentity);
            FaultDomainInfo openedTarget = DualTargetCopyCoordinator.ResolveTargetDomain(
                faultDomainResolver, plan.TargetRoot, plan.NasIdentity);
            DualTargetCopyCoordinator.OpenedTargetStorageBinding? storageBinding =
                DualTargetCopyCoordinator.CreateOpenedTargetStorageBinding(
                    plan.TargetRoot, openedTarget, plan.StorageIdentity);

            string logicalFinalPath = CopyPathConvention.GetFinalPath(plan.TargetRoot, entry.RelativePath);
            string logicalTempPath = CopyPathConvention.GetTempPath(
                plan.TargetRoot,
                entry.RelativePath,
                inventoryManifest.TaskId,
                entry.Id,
                plan.TargetId);
            string openedRoot = storageBinding?.PhysicalRoot ?? plan.TargetRoot;
            string openedFinalPath = storageBinding?.MapPath(logicalFinalPath) ?? logicalFinalPath;
            string openedTempPath = storageBinding?.MapPath(logicalTempPath) ?? logicalTempPath;
            Action<SafeFileHandle, string>? openedRootHandleCheck = storageBinding is null
                ? null
                : storageBinding.EnsureRootHandle;
            directoryLease = TargetDirectoryContinuityLease.Acquire(
                openedRoot, openedFinalPath, openedRootHandleCheck);
            storageBinding?.EnsureRoot(directoryLease);
            var handleBinding = new DualTargetCopyCoordinator.TemporaryFileHandleBinding(
                openedTempPath, directoryLease, storageBinding);

            long resumeOffset;
            IReadOnlyList<BlockCheckpoint> persistedCheckpoints;
            if (File.Exists(openedTempPath))
            {
                if (!resumeExistingTemps)
                    throw new IOException($"Fresh transfer refused a pre-existing temporary object: '{openedTempPath}'.");
                if (string.IsNullOrWhiteSpace(journal.TemporaryObjectIdentity))
                    throw new InvalidDataException("A resume temporary object has no persisted identity.");
                resumeOffset = GetPersistedOffset(journal, entry.FileSize);
                stream = new FileStream(openedTempPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    BufferSize = 1024 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                });
                handleBinding.EnsureOpenedHandle(stream.SafeFileHandle, openedTempPath);
                string actualIdentity = SourceHandleContinuityGuard.FormatFileId(
                    FileIdentity.GetFileIdentity(stream.SafeFileHandle, openedTempPath));
                if (!string.Equals(actualIdentity, journal.TemporaryObjectIdentity, StringComparison.Ordinal))
                    throw new IOException("The resume temporary object identity changed.");
                if (stream.Length < resumeOffset || stream.Length > entry.FileSize)
                    throw new InvalidDataException("The resume temporary length is outside the persisted checkpoint and frozen source bounds.");
                if (stream.Length > resumeOffset)
                {
                    stream.SetLength(resumeOffset);
                    stream.Flush(flushToDisk: true);
                }
                persistedCheckpoints = journal.Checkpoints.Select(value => new BlockCheckpoint(
                    value.BlockIndex,
                    value.Offset,
                    value.Length,
                    value.Sha256,
                    value.PersistedAtUtc)).ToArray();
            }
            else
            {
                if (resumeExistingTemps && journal.Checkpoints.Count > 0)
                    throw new FileNotFoundException("The persisted resume temporary object is missing.", openedTempPath);
                Directory.CreateDirectory(Path.GetDirectoryName(openedTempPath)!);
                stream = new FileStream(openedTempPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    BufferSize = 1024 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                });
                handleBinding.EnsureOpenedHandle(stream.SafeFileHandle, openedTempPath);
                resumeOffset = 0;
                persistedCheckpoints = [];
            }

            FileIdentity tempIdentity = handleBinding.TempFileIdentity ??
                throw new IOException("The temporary object identity could not be frozen.");
            string tempIdentityText = SourceHandleContinuityGuard.FormatFileId(tempIdentity);
            var copyTarget = new OpenedCopyTarget(
                plan.Role,
                stream,
                resumeOffset,
                persistedCheckpoints,
                async (checkpoint, token) =>
                {
                    await journalStore.OnCheckpointCompletedAsync(new CopyJournalCheckpointCompleted(
                        inventoryManifest.TaskId,
                        entry.Id,
                        plan.TargetId,
                        entry.RelativePath,
                        tempIdentityText,
                        checkpoint), token);
                });
            return new StagedTarget(
                plan,
                openedTempPath,
                openedFinalPath,
                directoryLease,
                handleBinding,
                tempIdentity,
                copyTarget,
                stream);
        }
        catch
        {
            stream?.Dispose();
            directoryLease?.Dispose();
            throw;
        }
    }

    private static long GetPersistedOffset(StandaloneTargetFileJournal journal, long fileLength)
    {
        StandaloneBlockCheckpoint[] ordered = journal.Checkpoints.OrderBy(value => value.BlockIndex).ToArray();
        long expectedOffset = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            StandaloneBlockCheckpoint checkpoint = ordered[index];
            if (checkpoint.BlockIndex != index || checkpoint.Offset != expectedOffset || checkpoint.Length <= 0)
                throw new InvalidDataException("Resume checkpoints are not a contiguous persisted prefix.");
            if (checkpoint.Length > ChunkedFileCopier.BlockSizeBytes)
                throw new InvalidDataException("A resume checkpoint exceeds the fixed 64 MiB block size.");
            expectedOffset += checkpoint.Length;
        }
        if (expectedOffset > fileLength)
            throw new InvalidDataException("Resume checkpoints exceed the frozen source length.");
        if (expectedOffset != fileLength && expectedOffset % ChunkedFileCopier.BlockSizeBytes != 0)
            throw new InvalidDataException("The last persisted resume checkpoint is not on a 64 MiB boundary.");
        return expectedOffset;
    }

    private static void EnsureFreshJournalBinding(
        TaskManifest inventoryManifest,
        StandaloneTaskJournal journal,
        IReadOnlyList<FreshTargetPlan> targets)
    {
        if (journal.SchemaVersion < 2 || journal.TaskId != inventoryManifest.TaskId)
            throw new InvalidDataException("The Fresh journal task identity or schema is invalid.");
        if (journal.ContentManifestFrozen || !string.IsNullOrWhiteSpace(journal.ManifestHash))
            throw new InvalidDataException("A content-frozen journal cannot enter the Fresh staging path.");
        if (!string.Equals(
                journal.InventoryManifestHash,
                inventoryManifest.ManifestHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Fresh journal inventory manifest binding changed.");
        }

        string[] expectedRoles =
        [
            .. journal.TargetMode.RequiresLocal() ? ["local"] : Array.Empty<string>(),
            .. journal.TargetMode.RequiresNas() ? ["nas"] : Array.Empty<string>(),
        ];
        string[] actualRoles = targets.Select(target => target.Role)
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();
        if (!actualRoles.SequenceEqual(expectedRoles.OrderBy(role => role, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("The selected Fresh target plans do not match the journal target mode.");

        Dictionary<Guid, ManifestEntry> inventoryFiles = inventoryManifest.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.Id);
        if (inventoryFiles.Count == 0 || inventoryFiles.Count != journal.Files.Count)
            throw new InvalidDataException("The Fresh journal file set does not match the inventory manifest.");

        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (!inventoryFiles.TryGetValue(file.FileId, out ManifestEntry? entry) ||
                file.Length != entry.FileSize ||
                !string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Fresh journal file facts changed from the inventory manifest.");
            }

            if (!journal.TargetMode.RequiresLocal() && file.LocalTarget.State != StandaloneTargetState.NotRequired)
                throw new InvalidDataException("An unselected local target is not marked NotRequired.");
            if (!journal.TargetMode.RequiresNas() && file.NasTarget.State != StandaloneTargetState.NotRequired)
                throw new InvalidDataException("An unselected NAS target is not marked NotRequired.");
        }

        foreach (FreshTargetPlan target in targets)
        {
            string expectedIdentity = target.Role == "local"
                ? journal.LocalTargetIdentity
                : journal.NasTargetIdentity;
            string expectedRoot = target.Role == "local"
                ? journal.LocalTargetRoot
                : journal.NasTargetRoot;
            if (!string.Equals(expectedIdentity, target.StorageIdentity, StringComparison.Ordinal) ||
                !PathsEqual(expectedRoot, target.TargetRoot))
            {
                throw new InvalidDataException($"The Fresh {target.Role} target plan changed from the journal binding.");
            }

            foreach (StandaloneFileJournal file in journal.Files)
            {
                StandaloneTargetFileJournal targetJournal = GetTargetJournal(file, target.Role);
                if (targetJournal.State == StandaloneTargetState.NotRequired ||
                    targetJournal.TargetId != target.TargetId ||
                    !string.Equals(targetJournal.TargetRole, target.Role, StringComparison.Ordinal) ||
                    !string.Equals(targetJournal.TargetIdentity, target.StorageIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The Fresh {target.Role} file target binding is inconsistent for '{file.RelativePath}'.");
                }
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
    private static StandaloneTargetFileJournal GetTargetJournal(
        StandaloneFileJournal file,
        string role) => role switch
        {
            "local" => file.LocalTarget,
            "nas" => file.NasTarget,
            _ => throw new InvalidDataException($"Unsupported target role '{role}'."),
        };

    private static void EnsureInventoryIdentity(ManifestEntry entry, SourceObjectIdentity current)
    {
        if (entry.FileSize != current.FileSize ||
            entry.LastModifiedUtc.ToUniversalTime() != current.LastModifiedUtc.ToUniversalTime() ||
            !string.Equals(entry.SourceFileIdType, current.FileIdType, StringComparison.Ordinal) ||
            !string.Equals(entry.SourceFileId, current.FileId, StringComparison.Ordinal))
        {
            throw new IOException($"Source inventory identity changed for '{entry.RelativePath}'.");
        }
    }

    private sealed class MutableTargetStatus(FreshTargetPlan plan)
    {
        public long CompletedStagingBytes { get; set; }
        public long CurrentFileBytes { get; set; }
        public long TemporaryBytesVerified { get; set; }
        public long FinalBytesVerified { get; set; }
        public int FilesVerified { get; set; }

        public void Report(
            TaskManifest manifest,
            CopyPhase phase,
            string? currentFile,
            bool completed)
        {
            try
            {
                plan.Progress?.Report(new TargetCopyStatus
                {
                    TaskId = manifest.TaskId,
                    TargetId = plan.TargetId,
                    FaultDomainId = plan.FaultDomainId,
                    BytesCopied = Math.Min(manifest.TotalBytes, CompletedStagingBytes + CurrentFileBytes),
                    BytesTransferred = Math.Min(manifest.TotalBytes, CompletedStagingBytes + CurrentFileBytes),
                    TemporaryBytesVerified = Math.Min(manifest.TotalBytes, TemporaryBytesVerified),
                    FinalBytesVerified = Math.Min(manifest.TotalBytes, FinalBytesVerified),
                    TotalBytes = manifest.TotalBytes,
                    TotalFiles = manifest.TotalFiles,
                    CurrentFile = currentFile,
                    Phase = phase,
                    FilesVerified = FilesVerified,
                    FilesFailed = 0,
                    IsComplete = completed,
                });
            }
            catch
            {
                // Progress is observational and must never alter transfer correctness.
            }
        }
    }

    private sealed record SourceStagedFile(
        SourceReadContinuityLease SourceLease,
        string SourceHash,
        StagedTarget Target);

    private sealed record StagedFile(
        ManifestEntry Entry,
        SourceReadContinuityLease SourceLease,
        IReadOnlyList<StagedTarget> Targets);

    private sealed class StagedTarget(
        FreshTargetPlan plan,
        string openedTempPath,
        string openedFinalPath,
        TargetDirectoryContinuityLease directoryLease,
        DualTargetCopyCoordinator.TemporaryFileHandleBinding handleBinding,
        FileIdentity tempIdentity,
        OpenedCopyTarget copyTarget,
        FileStream stagingStream)
    {
        private FileStream? _stagingStream = stagingStream;

        public FreshTargetPlan Plan { get; } = plan;
        public string OpenedTempPath { get; } = openedTempPath;
        public string OpenedFinalPath { get; } = openedFinalPath;
        public TargetDirectoryContinuityLease DirectoryLease { get; } = directoryLease;
        public DualTargetCopyCoordinator.TemporaryFileHandleBinding HandleBinding { get; } = handleBinding;
        public FileIdentity TempIdentity { get; } = tempIdentity;
        public OpenedCopyTarget CopyTarget { get; } = copyTarget;
        public PublishResult? Published { get; set; }

        public async ValueTask DisposeStagingStreamAsync()
        {
            if (_stagingStream is null)
                return;
            await _stagingStream.DisposeAsync();
            _stagingStream = null;
        }
    }

    private static void ReportSourceProgress(
        IProgress<SourceReadStatus>? progress,
        SourceReadStatus status)
    {
        try
        {
            progress?.Report(status);
        }
        catch
        {
            // Progress is observational and cannot alter transfer correctness.
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
