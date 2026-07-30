// LAYER VIOLATION (BUG-07): This Application-layer class depends on
// AutoCardSync.Infrastructure.Copying (ChunkedFileCopier, AtomicFilePublisher).
// This is a known architectural debt — copy abstractions should be abstracted
// via interfaces in Application/Domain. See docs/adr/ADR-002 for the decision record.
using System.Security.Cryptography;
using System.Runtime.Versioning;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Application.Copying;

public enum CopyPhase
{
    Copying = 0,
    Verifying = 1,
    Completed = 2,
    Failed = 3,
    TemporaryVerifying = 4,
    Publishing = 5,
    FinalVerifying = 6,
}

public sealed record TargetCopyStatus
{
    public Guid TaskId { get; init; }
    public Guid TargetId { get; init; }
    public string FaultDomainId { get; init; } = string.Empty;
    public long BytesCopied { get; init; }
    public long BytesTransferred { get; init; }
    public long TemporaryBytesVerified { get; init; }
    public long FinalBytesVerified { get; init; }
    public long TotalBytes { get; init; }
    public int TotalFiles { get; init; }
    public string? CurrentFile { get; init; }
    public CopyPhase Phase { get; init; }
    public int FilesVerified { get; init; }
    public int FilesFailed { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record FileCopyResult
{
    public Guid FileId { get; init; }
    public Guid TargetId { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public bool Verified { get; init; }
    public bool WasReused { get; init; }
    public string? Hash { get; init; }
    public string? FinalPath { get; init; }
    public string? TargetFileId { get; init; }
    public string? Error { get; init; }
}

public sealed class DualTargetCopyCoordinator
{
    private readonly ChunkedFileCopier _copier = new();
    private readonly AtomicFilePublisher _publisher = new();

    [SupportedOSPlatform("windows")]
    public async Task<IReadOnlyList<FileCopyResult>> CopyToTargetAsync(
        TaskManifest manifest,
        string sourceRoot,
        string targetRoot,
        Guid targetId,
        string faultDomainId,
        IProgress<TargetCopyStatus>? progress,
        CancellationToken ct,
        string? storageIdentity = null,
        string? nasIdentity = null,
        bool resumeExistingTemps = false,
        ICopyJournalSink? journalSink = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Source identity continuity requires Windows file identity APIs.");
        }

        var continuityGuard = new SourceHandleContinuityGuard();
        var faultDomainResolver = new FaultDomainResolver();
        FaultDomainInfo openedTarget = ResolveTargetDomain(
            faultDomainResolver, targetRoot, nasIdentity);
        string? boundFaultDomainId = storageIdentity is null ? null : faultDomainId;
        if (boundFaultDomainId is not null &&
            !string.Equals(openedTarget.FaultDomainId, boundFaultDomainId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Target fault domain changed: expected '{boundFaultDomainId}', current '{openedTarget.FaultDomainId}'.");
        }
        string frozenStorageIdentity = storageIdentity ?? openedTarget.StorageIdentity
            ?? throw new InvalidDataException("Opened target storage identity is missing.");
        if (!string.Equals(frozenStorageIdentity, openedTarget.StorageIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException("Opened target storage identity changed before copy.");
        }
        OpenedTargetStorageBinding? openedTargetStorageBinding =
            CreateOpenedTargetStorageBinding(
                targetRoot, openedTarget, frozenStorageIdentity);

        var results = new List<FileCopyResult>();
        long totalBytesCopied = 0;
        long totalBytesTransferred = 0;
        long totalTemporaryBytesVerified = 0;
        long totalFinalBytesVerified = 0;
        int filesVerified = 0;
        int filesFailed = 0;

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Excluded)
                continue;

            // Treat a persisted manifest as untrusted input. A corrupted or malicious
            // relative path must never escape either the source or destination root.
            string sourcePath = SafePathResolver.ResolveSafePath(sourceRoot, entry.RelativePath);
            string finalPath = CopyPathConvention.GetFinalPath(targetRoot, entry.RelativePath);
            string tempPath = CopyPathConvention.GetTempPath(
                targetRoot, entry.RelativePath, manifest.TaskId, entry.Id, targetId);
            string openedTargetRoot = openedTargetStorageBinding?.PhysicalRoot ?? targetRoot;
            string openedFinalPath = openedTargetStorageBinding?.MapPath(finalPath) ?? finalPath;
            string openedTempPath = openedTargetStorageBinding?.MapPath(tempPath) ?? tempPath;
            long completedBeforeFile = totalBytesCopied;
            long transferredBeforeFile = totalBytesTransferred;
            long currentFileBytes = 0;
            long resumeOffset = 0;

            FileCopyResult result;
            TargetDirectoryContinuityLease? targetLease = null;
            TemporaryFileHandleBinding? targetHandleBinding = null;

            try
            {
                EnsureTargetStorageIdentity(
                    faultDomainResolver, targetRoot, boundFaultDomainId,
                    frozenStorageIdentity, nasIdentity);
                Action<SafeFileHandle, string>? openedRootHandleCheck =
                    openedTargetStorageBinding is null
                        ? null
                        : openedTargetStorageBinding.EnsureRootHandle;
                targetLease = TargetDirectoryContinuityLease.Acquire(
                    openedTargetRoot, openedFinalPath, openedRootHandleCheck);
                openedTargetStorageBinding?.EnsureRoot(targetLease);
                targetHandleBinding = new TemporaryFileHandleBinding(
                    openedTempPath, targetLease, openedTargetStorageBinding);
                Action<SafeFileHandle, string> openedTargetHandleCheck =
                    targetHandleBinding.EnsureOpenedHandle;
                await using SourceReadContinuityLease sourceLease =
                    continuityGuard.AcquireReadLease(sourcePath);
                SourceObjectIdentity frozenSource = sourceLease.Identity;
                EnsureManifestIdentity(entry, frozenSource);
                string expectedHash = entry.SourceHash ?? frozenSource.ContentHash;
                if (journalSink is not null)
                {
                    await journalSink.OnFileStartedAsync(new CopyJournalFileStarted(
                        manifest.TaskId, entry.Id, targetId, entry.RelativePath,
                        frozenSource.FileId, openedTempPath, openedFinalPath,
                        entry.FileSize, expectedHash), ct);
                }

                // 1. Reuse only while the verified target object remains locked against
                // write/delete and its file identity remains bound to the returned fact.
                if (File.Exists(openedFinalPath))
                {
                    currentFileBytes = entry.FileSize;
                    long completedBeforeFirstVerification = totalTemporaryBytesVerified;
                    var firstVerificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                    {
                        totalTemporaryBytesVerified = Math.Min(
                            manifest.TotalBytes,
                            completedBeforeFirstVerification + value.BytesVerified);
                        ReportProgress(
                            completedBeforeFile + entry.FileSize, transferredBeforeFile,
                            CopyPhase.FinalVerifying, entry.RelativePath);
                    });
                    ReportProgress(
                        completedBeforeFile + entry.FileSize, transferredBeforeFile,
                        CopyPhase.FinalVerifying, entry.RelativePath);
                    await using PublishResult existing = await _publisher.VerifyExistingAsync(
                        openedFinalPath, expectedHash, entry.FileSize, ct, targetLease.EnsureContinuous,
                        openedTargetHandleCheck, firstVerificationProgress);
                    if (existing.Success)
                    {
                        totalTemporaryBytesVerified = Math.Min(
                            manifest.TotalBytes,
                            completedBeforeFirstVerification + entry.FileSize);
                        await EnsureSourceContinuityAsync(
                            sourceLease, frozenSource, sourcePath, ct);
                        long completedBeforeFinalVerification = totalFinalBytesVerified;
                        var finalVerificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                        {
                            totalFinalBytesVerified = Math.Min(
                                manifest.TotalBytes,
                                completedBeforeFinalVerification + value.BytesVerified);
                            ReportProgress(
                                completedBeforeFile + entry.FileSize, transferredBeforeFile,
                                CopyPhase.FinalVerifying, entry.RelativePath);
                        });
                        await existing.EnsureContinuousAsync(ct, finalVerificationProgress);
                        totalFinalBytesVerified = Math.Min(
                            manifest.TotalBytes,
                            completedBeforeFinalVerification + entry.FileSize);
                        if (journalSink is not null)
                        {
                            await journalSink.OnFileVerifiedAsync(new CopyJournalFileVerified(
                                manifest.TaskId, entry.Id, targetId, entry.RelativePath,
                                finalPath, existing.FileId ?? string.Empty, entry.FileSize,
                                existing.Hash ?? expectedHash, ReusedExisting: true), ct);
                        }
                        EnsureTargetStorageIdentity(
                            faultDomainResolver, targetRoot, boundFaultDomainId,
                            frozenStorageIdentity, nasIdentity);

                        result = new FileCopyResult
                        {
                            FileId = entry.Id,
                            TargetId = targetId,
                            RelativePath = entry.RelativePath,
                            Verified = true,
                            WasReused = true,
                            Hash = existing.Hash,
                            FinalPath = finalPath,
                            TargetFileId = existing.FileId,
                        };
                        totalBytesCopied = completedBeforeFile + entry.FileSize;
                        filesVerified++;
                        results.Add(result);
                        ReportProgress();
                        continue;
                    }
                }

                // 2. Copy source to temp using chunked copier
                targetLease.EnsureContinuous();
                TempPreparation tempPreparation = PrepareTemp(
                    openedTempPath, entry.FileSize, openedTargetHandleCheck,
                    resumeExistingTemps);
                resumeOffset = tempPreparation.ResumeOffset;
                currentFileBytes = resumeOffset;
                ReportProgress(completedBeforeFile + currentFileBytes,
                    transferredBeforeFile, CopyPhase.Copying, entry.RelativePath);
                var copyProgress = new InlineProgress<CopyProgress>(value =>
                {
                    currentFileBytes = value.BytesCopied;
                    ReportProgress(completedBeforeFile + currentFileBytes,
                        transferredBeforeFile + Math.Max(0, currentFileBytes - resumeOffset),
                        CopyPhase.Copying, entry.RelativePath);
                });
                Func<BlockCheckpoint, CancellationToken, ValueTask>? checkpointSink =
                    journalSink is null
                        ? null
                        : (checkpoint, token) => journalSink.OnCheckpointCompletedAsync(
                            new CopyJournalCheckpointCompleted(
                                manifest.TaskId, entry.Id, targetId,
                                entry.RelativePath,
                                targetHandleBinding.TempFileIdentity is FileIdentity tempIdentity
                                    ? SourceHandleContinuityGuard.FormatFileId(tempIdentity)
                                    : throw new IOException($"Temporary object identity is unavailable for '{openedTempPath}'."),
                                checkpoint), token);
                await _copier.CopyFileAsync(
                    sourcePath, openedTempPath, resumeOffset, copyProgress, ct,
                    openedTargetHandleCheck, tempPreparation.UseExistingObject,
                    checkpointSink);
                targetLease.EnsureContinuous();
                currentFileBytes = entry.FileSize;
                long completedBeforeTemporaryVerification = totalTemporaryBytesVerified;
                var temporaryVerificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                {
                    totalTemporaryBytesVerified = Math.Min(
                        manifest.TotalBytes,
                        completedBeforeTemporaryVerification + value.BytesVerified);
                    ReportProgress(
                        completedBeforeFile + currentFileBytes,
                        transferredBeforeFile + Math.Max(0, currentFileBytes - resumeOffset),
                        CopyPhase.TemporaryVerifying, entry.RelativePath);
                });
                ReportProgress(completedBeforeFile + currentFileBytes,
                    transferredBeforeFile + Math.Max(0, currentFileBytes - resumeOffset),
                    CopyPhase.TemporaryVerifying, entry.RelativePath);

                // 3. Publish atomically and keep the verified target object leased until
                // the source and target continuity checks have produced the final fact.
                FileIdentity verifiedTempIdentity = targetHandleBinding.TempFileIdentity
                    ?? throw new IOException(
                        $"Temporary file handle was not bound before publish for '{openedTempPath}'.");
                await using PublishResult publishResult = await _publisher.PublishAsync(
                    openedTempPath, openedFinalPath, expectedHash, entry.FileSize, ct,
                    targetLease.EnsureContinuous, openedTargetHandleCheck,
                    verifiedTempIdentity,
                    temporaryVerificationProgress: temporaryVerificationProgress);

                if (publishResult.Success)
                {
                    totalTemporaryBytesVerified = Math.Min(
                        manifest.TotalBytes,
                        completedBeforeTemporaryVerification + entry.FileSize);
                    ReportProgress(
                        completedBeforeFile + entry.FileSize,
                        transferredBeforeFile + Math.Max(0, entry.FileSize - resumeOffset),
                        CopyPhase.Publishing, entry.RelativePath);
                    await EnsureSourceContinuityAsync(
                        sourceLease, frozenSource, sourcePath, ct);
                    long completedBeforeFinalVerification = totalFinalBytesVerified;
                    var finalVerificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                    {
                        totalFinalBytesVerified = Math.Min(
                            manifest.TotalBytes,
                            completedBeforeFinalVerification + value.BytesVerified);
                        ReportProgress(
                            completedBeforeFile + entry.FileSize,
                            transferredBeforeFile + Math.Max(0, entry.FileSize - resumeOffset),
                            CopyPhase.FinalVerifying, entry.RelativePath);
                    });
                    ReportProgress(
                        completedBeforeFile + entry.FileSize,
                        transferredBeforeFile + Math.Max(0, entry.FileSize - resumeOffset),
                        CopyPhase.FinalVerifying, entry.RelativePath);
                    await publishResult.EnsureContinuousAsync(ct, finalVerificationProgress);
                    totalFinalBytesVerified = Math.Min(
                        manifest.TotalBytes,
                        completedBeforeFinalVerification + entry.FileSize);
                    EnsureTargetStorageIdentity(
                        faultDomainResolver, targetRoot, boundFaultDomainId,
                        frozenStorageIdentity, nasIdentity);
                    if (journalSink is not null)
                    {
                        await journalSink.OnFileVerifiedAsync(new CopyJournalFileVerified(
                            manifest.TaskId, entry.Id, targetId, entry.RelativePath,
                            finalPath, publishResult.FileId ?? string.Empty, entry.FileSize,
                            publishResult.Hash ?? expectedHash, ReusedExisting: false), ct);
                    }

                    result = new FileCopyResult
                    {
                        FileId = entry.Id,
                        TargetId = targetId,
                        RelativePath = entry.RelativePath,
                        Verified = true,
                        Hash = publishResult.Hash,
                        FinalPath = finalPath,
                        TargetFileId = publishResult.FileId,
                    };
                    totalBytesCopied = completedBeforeFile + entry.FileSize;
                    totalBytesTransferred = transferredBeforeFile +
                        Math.Max(0, entry.FileSize - resumeOffset);
                    filesVerified++;
                }
                else
                {
                    result = new FileCopyResult
                    {
                        FileId = entry.Id,
                        TargetId = targetId,
                        RelativePath = entry.RelativePath,
                        Verified = false,
                        Error = publishResult.Error,
                    };
                    if (journalSink is not null)
                    {
                        await journalSink.OnFileFailedAsync(new CopyJournalFileFailed(
                            manifest.TaskId, entry.Id, targetId, entry.RelativePath,
                            publishResult.Error ?? "Target verification failed."), ct);
                    }

                    filesFailed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string error = ex.Message;
                filesFailed++;

                if (targetHandleBinding?.TempFileIdentity is FileIdentity tempIdentity)
                {
                    try
                    {
                        Action? targetContinuityCheck = targetLease is null
                            ? null
                            : targetLease.EnsureContinuous;
                        await _publisher.RollbackAsync(
                            openedTempPath, tempIdentity, targetHandleBinding.EnsureOpenedHandle,
                            targetContinuityCheck);
                    }
                    catch (Exception rollbackError)
                    {
                        error += $" Temporary cleanup was refused or failed: {rollbackError.Message}";
                    }
                }

                if (journalSink is not null)
                {
                    await journalSink.OnFileFailedAsync(new CopyJournalFileFailed(
                        manifest.TaskId, entry.Id, targetId, entry.RelativePath, error), ct);
                }


                result = new FileCopyResult
                {
                    FileId = entry.Id,
                    TargetId = targetId,
                    RelativePath = entry.RelativePath,
                    Verified = false,
                    Error = error,
                };
            }
            finally
            {
                targetLease?.Dispose();
            }

            totalBytesCopied = Math.Max(totalBytesCopied,
                completedBeforeFile + Math.Min(currentFileBytes, entry.FileSize));
            totalBytesTransferred = Math.Max(totalBytesTransferred,
                transferredBeforeFile + Math.Max(0,
                    Math.Min(currentFileBytes, entry.FileSize) - resumeOffset));
            results.Add(result);
            ReportProgress();
        }

        EnsureTargetStorageIdentity(
            faultDomainResolver, targetRoot, boundFaultDomainId,
            frozenStorageIdentity, nasIdentity);
        SafeReport(new TargetCopyStatus
        {
            TaskId = manifest.TaskId,
            TargetId = targetId,
            FaultDomainId = faultDomainId,
            BytesCopied = totalBytesCopied,
            BytesTransferred = totalBytesTransferred,
            TemporaryBytesVerified = totalTemporaryBytesVerified,
            FinalBytesVerified = totalFinalBytesVerified,
            TotalBytes = manifest.TotalBytes,
            TotalFiles = manifest.TotalFiles,
            CurrentFile = null,
            Phase = filesFailed == 0 ? CopyPhase.Completed : CopyPhase.Failed,
            FilesVerified = filesVerified,
            FilesFailed = filesFailed,
            IsComplete = true,
        });

        return results;

        void ReportProgress(long? bytesCopied = null, long? bytesTransferred = null,
            CopyPhase phase = CopyPhase.Copying, string? currentFile = null)
        {
            SafeReport(new TargetCopyStatus
            {
                TaskId = manifest.TaskId,
                TargetId = targetId,
                FaultDomainId = faultDomainId,
                BytesCopied = bytesCopied ?? totalBytesCopied,
                BytesTransferred = bytesTransferred ?? totalBytesTransferred,
                TemporaryBytesVerified = totalTemporaryBytesVerified,
                FinalBytesVerified = totalFinalBytesVerified,
                TotalBytes = manifest.TotalBytes,
                TotalFiles = manifest.TotalFiles,
                CurrentFile = currentFile,
                Phase = phase,
                FilesVerified = filesVerified,
                FilesFailed = filesFailed,
                IsComplete = false,
            });
        }

        void SafeReport(TargetCopyStatus status)
        {
            try
            {
                progress?.Report(status);
            }
            catch
            {
                // Progress is observational and must never change copy correctness.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task<Dictionary<Guid, IReadOnlyList<FileCopyResult>>> CopyToBothTargetsAsync(
        TaskManifest manifest,
        string sourceRoot,
        string primaryRoot,
        string primaryDomainId,
        string backupRoot,
        string backupDomainId,
        CancellationToken ct)
    {
        Guid primaryTargetId = DeterministicGuid("primary", manifest.TaskId);
        Guid backupTargetId = DeterministicGuid("backup", manifest.TaskId);

        Task<IReadOnlyList<FileCopyResult>> primaryTask = CopyToTargetAsync(
            manifest, sourceRoot, primaryRoot, primaryTargetId, primaryDomainId,
            progress: null, ct);

        Task<IReadOnlyList<FileCopyResult>> backupTask = CopyToTargetAsync(
            manifest, sourceRoot, backupRoot, backupTargetId, backupDomainId,
            progress: null, ct);

        await Task.WhenAll(primaryTask, backupTask);

        return new Dictionary<Guid, IReadOnlyList<FileCopyResult>>
        {
            [primaryTargetId] = primaryTask.Result,
            [backupTargetId] = backupTask.Result,
        };
    }

    private static Guid DeterministicGuid(string role, Guid taskId)
    {
        byte[] bytes = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{taskId}:{role}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    [SupportedOSPlatform("windows")]
    internal static FaultDomainInfo ResolveTargetDomain(
        FaultDomainResolver resolver,
        string targetRoot,
        string? nasIdentity)
        => targetRoot.StartsWith(@"\", StringComparison.Ordinal)
            ? resolver.ResolveNasDomain(targetRoot, nasIdentity)
            : resolver.ResolveLocalDomain(targetRoot);

    [SupportedOSPlatform("windows")]
    internal static OpenedTargetStorageBinding? CreateOpenedTargetStorageBinding(
        string targetRoot,
        FaultDomainInfo openedTarget,
        string frozenStorageIdentity)
    {
        if (!string.Equals(
                openedTarget.StorageIdentity, frozenStorageIdentity, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Opened target storage identity changed before binding write handles for '{targetRoot}'.");
        }

        if (targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var resolver = new NetworkStorageIdentityResolver();
            NetworkStorageIdentity expected = resolver.Capture(targetRoot);
            if (!string.Equals(
                    expected.StorageIdentity, frozenStorageIdentity, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Opened target storage identity changed before binding write handles for '{targetRoot}'.");
            }

            return new OpenedTargetStorageBinding(
                targetRoot,
                expected.PhysicalUncPath,
                (handle, path) => resolver.EnsureOpenedRootHandleMatches(handle, expected, path),
                (handle, path) => resolver.EnsureOpenedHandleMatches(handle, expected, path));
        }

        var localResolver = new LocalStorageIdentityResolver();
        return new OpenedTargetStorageBinding(
            targetRoot,
            targetRoot,
            (handle, path) => localResolver.EnsureOpenedHandleMatches(handle, openedTarget, path),
            (handle, path) => localResolver.EnsureOpenedHandleMatches(handle, openedTarget, path));
    }

    [SupportedOSPlatform("windows")]
    internal static void EnsureTargetStorageIdentity(
        FaultDomainResolver resolver,
        string targetRoot,
        string? expectedFaultDomainId,
        string expectedStorageIdentity,
        string? nasIdentity)
    {
        FaultDomainInfo current = ResolveTargetDomain(resolver, targetRoot, nasIdentity);
        if ((expectedFaultDomainId is not null &&
             !string.Equals(current.FaultDomainId, expectedFaultDomainId,
                 StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(current.StorageIdentity, expectedStorageIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException($"Opened target storage identity changed for '{targetRoot}'.");
        }
    }

    private static TempPreparation PrepareTemp(
        string tempPath,
        long sourceSize,
        Action<SafeFileHandle, string>? openedTargetHandleCheck,
        bool resumeExistingTemps)
    {
        if (!File.Exists(tempPath))
            return new TempPreparation(0, UseExistingObject: false);
        if (!resumeExistingTemps)
        {
            throw new IOException(
                $"Fresh copy refused a pre-existing temporary object: '{tempPath}'.");
        }

        using var stream = new FileStream(
            tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        openedTargetHandleCheck?.Invoke(stream.SafeFileHandle, tempPath);
        long existingLength = stream.Length;
        long usableLength = Math.Min(existingLength, sourceSize);
        long alignedLength = usableLength / ChunkedFileCopier.BlockSizeBytes
            * ChunkedFileCopier.BlockSizeBytes;

        if (existingLength != alignedLength)
        {
            stream.SetLength(alignedLength);
            stream.Flush(flushToDisk: true);
        }

        return new TempPreparation(alignedLength, UseExistingObject: true);
    }

    private readonly record struct TempPreparation(
        long ResumeOffset, bool UseExistingObject);

    [SupportedOSPlatform("windows")]
    internal sealed class OpenedTargetStorageBinding(
        string logicalRoot,
        string physicalRoot,
        Action<SafeFileHandle, string> ensureRootHandle,
        Action<SafeFileHandle, string> ensureOpenedHandle)
    {
        private readonly string _logicalRoot = Path.GetFullPath(logicalRoot).TrimEnd('\\');

        public string PhysicalRoot { get; } = Path.GetFullPath(physicalRoot).TrimEnd('\\');

        public string MapPath(string logicalPath)
        {
            string fullLogicalPath = Path.GetFullPath(logicalPath);
            if (string.Equals(
                    PhysicalRoot,
                    _logicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return fullLogicalPath;
            }

            return NetworkStorageIdentityResolver.MapLogicalPathToPhysicalRoot(
                _logicalRoot, PhysicalRoot, fullLogicalPath);
        }

        public void EnsureRootHandle(SafeFileHandle handle, string path) =>
            ensureRootHandle(handle, path);

        public void EnsureRoot(TargetDirectoryContinuityLease directoryLease)
        {
            directoryLease.EnsureContinuous();
            directoryLease.EnsureRootHandle(EnsureRootHandle);
            directoryLease.EnsureContinuous();
        }

        public void EnsureOpenedHandle(
            TargetDirectoryContinuityLease directoryLease,
            SafeFileHandle handle,
            string path)
        {
            EnsureRoot(directoryLease);
            ensureOpenedHandle(handle, path);
            EnsureRoot(directoryLease);
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class TemporaryFileHandleBinding(
        string tempPath,
        TargetDirectoryContinuityLease directoryLease,
        OpenedTargetStorageBinding? storageBinding)
    {
        private readonly string _tempPath = Path.GetFullPath(tempPath);

        public FileIdentity? TempFileIdentity { get; private set; }

        public void EnsureOpenedHandle(SafeFileHandle handle, string path)
        {
            directoryLease.EnsureContinuous();
            storageBinding?.EnsureOpenedHandle(directoryLease, handle, path);

            string fullPath = Path.GetFullPath(path);
            if (string.Equals(
                    fullPath, _tempPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                FileIdentity.EnsurePathStillNamesOpenedObject(
                    handle, fullPath, requireSingleLink: true);
                FileIdentity actual = FileIdentity.GetFileIdentity(handle, fullPath);
                if (TempFileIdentity is FileIdentity expected && actual != expected)
                {
                    throw new IOException(
                        $"Temporary file identity changed for '{fullPath}'.");
                }
                TempFileIdentity ??= actual;
            }

            directoryLease.EnsureContinuous();
        }
    }

    private static void EnsureManifestIdentity(
        ManifestEntry entry,
        SourceObjectIdentity current)
    {
        if (entry.FileSize != current.FileSize)
        {
            throw new IOException(
                $"Source identity mismatch for '{entry.RelativePath}': " +
                $"manifest size {entry.FileSize}, current size {current.FileSize}.");
        }

        if (entry.LastModifiedUtc.ToUniversalTime() != current.LastModifiedUtc.ToUniversalTime())
        {
            throw new IOException(
                $"Source identity mismatch for '{entry.RelativePath}': the file was modified after scanning.");
        }

        if (!string.IsNullOrWhiteSpace(entry.SourceFileId)
            && (!string.Equals(entry.SourceFileIdType, current.FileIdType, StringComparison.Ordinal)
                || !string.Equals(entry.SourceFileId, current.FileId, StringComparison.Ordinal)))
        {
            throw new IOException(
                $"Source identity mismatch for '{entry.RelativePath}': the file was replaced after scanning.");
        }

        if (!string.IsNullOrWhiteSpace(entry.SourceHash)
            && !string.Equals(entry.SourceHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Source content no longer matches the scanned manifest for '{entry.RelativePath}'.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task EnsureSourceContinuityAsync(
        SourceReadContinuityLease sourceLease,
        SourceObjectIdentity frozen,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        SourceContinuityResult continuity = await sourceLease.VerifyContinuityAsync(
            frozen, cancellationToken);
        if (!continuity.IsContinuous)
        {
            throw new IOException(
                $"Source identity changed while copying '{sourcePath}': {continuity.MismatchDetail}");
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
