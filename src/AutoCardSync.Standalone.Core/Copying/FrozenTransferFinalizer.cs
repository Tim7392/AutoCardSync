using System.Diagnostics;
using System.Runtime.Versioning;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Application.Copying;

/// <summary>
/// Completes a content-frozen transfer after one source-handle reread verifies the frozen content hash.
/// The reread is shared across all selected targets; it never reopens the source once per target. A
/// frozen journal proves that every selected temporary object was durably staged before publication.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FrozenTransferFinalizer
{
    private readonly AtomicFilePublisher _publisher = new();

    /// <summary>
    /// Completes a content-frozen task by validating its frozen bindings, rereading source and staged targets,
    /// publishing verified final objects, and returning the leases required for the receipt boundary.
    /// </summary>
    /// <remarks>
    /// The caller must first use recovery eligibility checks to reject a changed card instance. This method then
    /// rejects inconsistent journal, manifest, source, target mode, target identity, staged object, or expected
    /// source hash facts. The caller must still revalidate the returned execution before it persists a completion
    /// receipt.
    /// </remarks>
    public async Task<FreshTransferExecution> FinalizeAsync(
        TaskManifest contentManifest,
        string sourceRoot,
        IReadOnlyList<FreshTargetPlan> targets,
        StandaloneTaskJournalStore journalStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentManifest);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(journalStore);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Frozen transfer finalization requires Windows identity APIs.");
        if (contentManifest.FrozenAt is null || string.IsNullOrWhiteSpace(contentManifest.ManifestHash))
            throw new InvalidDataException("The content manifest must be frozen before recovery finalization.");
        if (contentManifest.Entries.Where(entry => !entry.Excluded)
            .Any(entry => string.IsNullOrWhiteSpace(entry.SourceHash)))
        {
            throw new InvalidDataException("Frozen recovery requires a complete source SHA-256 for every included file.");
        }
        if (targets.Count is < 1 or > 2 ||
            targets.Select(target => target.Role).Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            throw new InvalidDataException("Frozen recovery requires one or two distinct selected targets.");
        }

        StandaloneTaskJournal journal = await journalStore.LoadAsync(cancellationToken) ??
            throw new InvalidDataException("The frozen task journal is missing.");
        ManifestJournalBindingValidator.EnsureValid(contentManifest, journal);
        EnsureFrozenJournalBinding(contentManifest, journal, targets);

        var faultDomainResolver = new FaultDomainResolver();
        var targetContexts = targets.ToDictionary(
            target => target.TargetId,
            target => OpenTargetContext(target, faultDomainResolver));
        var sourceLeases = new List<SourceReadContinuityLease>(contentManifest.TotalFiles);
        var targetLeases = new List<TargetDirectoryContinuityLease>(checked(contentManifest.TotalFiles * targets.Count));
        var publishLeases = new List<PublishResult>(checked(contentManifest.TotalFiles * targets.Count));
        var results = new List<FileCopyResult>(checked(contentManifest.TotalFiles * targets.Count));
        var verifiedCounts = targets.ToDictionary(target => target.TargetId, _ => 0);
        var temporaryVerifiedBytes = targets.ToDictionary(target => target.TargetId, _ => 0L);
        var finalVerifiedBytes = targets.ToDictionary(target => target.TargetId, _ => 0L);
        var stageTimes = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var totalStopwatch = Stopwatch.StartNew();
        long sourceContentBytesRead = 0;
        long targetTemporaryBytesRead = 0;
        long targetFinalBytesRead = 0;
        int peakLeases = 0;
        int peakProcessHandles = Process.GetCurrentProcess().HandleCount;
        long peakManaged = GC.GetTotalMemory(false);

        try
        {
            var sourceGuard = new SourceHandleContinuityGuard();
            foreach (ManifestEntry entry in contentManifest.Entries.Where(entry => !entry.Excluded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StandaloneFileJournal fileJournal = journal.Files.Single(file => file.FileId == entry.Id);
                string sourcePath = SafePathResolver.ResolveSafePath(sourceRoot, entry.RelativePath);
                SourceReadContinuityLease sourceLease = sourceGuard.AcquireReadLease(sourcePath);
                sourceLeases.Add(sourceLease);
                EnsureSourceMetadataBinding(entry, fileJournal, sourceLease.Identity);
                if (!string.Equals(sourceLease.Identity.ContentHash, entry.SourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Frozen source content hash changed for '{entry.RelativePath}'.");
                sourceContentBytesRead += entry.FileSize;
                peakLeases = Math.Max(peakLeases, sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                peakProcessHandles = Math.Max(peakProcessHandles, Process.GetCurrentProcess().HandleCount);

                foreach (FreshTargetPlan plan in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StandaloneTargetFileJournal targetJournal = GetTargetJournal(fileJournal, plan.Role);
                    FrozenTargetContext targetContext = targetContexts[plan.TargetId];
                    FrozenTargetPaths paths = ResolveAndValidatePaths(
                        contentManifest, entry, journal, fileJournal, plan, targetJournal, targetContext);
                    TargetDirectoryContinuityLease directoryLease = AcquireTargetLease(targetContext, paths.FinalPath);
                    targetLeases.Add(directoryLease);
                    peakLeases = Math.Max(peakLeases, sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(peakProcessHandles, Process.GetCurrentProcess().HandleCount);

                    Action<SafeFileHandle, string> openedHandleCheck = (handle, path) =>
                    {
                        directoryLease.EnsureContinuous();
                        targetContext.StorageBinding?.EnsureOpenedHandle(directoryLease, handle, path);
                        FileIdentity.EnsurePathStillNamesOpenedObject(handle, path, requireSingleLink: true);
                        directoryLease.EnsureContinuous();
                    };

                    PublishResult published;
                    bool finalExists = File.Exists(paths.FinalPath);
                    bool tempExists = File.Exists(paths.TempPath);
                    if (finalExists && tempExists)
                    {
                        throw new IOException(
                            $"Frozen recovery found both temporary and final objects for '{entry.RelativePath}' on {plan.Role}.");
                    }

                    if (finalExists)
                    {
                        temporaryVerifiedBytes[plan.TargetId] = Math.Min(
                            contentManifest.TotalBytes,
                            temporaryVerifiedBytes[plan.TargetId] + entry.FileSize);
                        long completedBeforeFinalVerification = finalVerifiedBytes[plan.TargetId];
                        var verificationProgress = new InlineProgress<FileVerificationProgress>(value =>
                        {
                            finalVerifiedBytes[plan.TargetId] = Math.Min(
                                contentManifest.TotalBytes,
                                completedBeforeFinalVerification + value.BytesVerified);
                            Report(
                                plan, contentManifest, targetJournal, CopyPhase.FinalVerifying,
                                entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                                temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        });
                        Report(
                            plan, contentManifest, targetJournal, CopyPhase.FinalVerifying,
                            entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                            temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        published = await _publisher.VerifyExistingAsync(
                            paths.FinalPath,
                            entry.SourceHash!,
                            entry.FileSize,
                            cancellationToken,
                            directoryLease.EnsureContinuous,
                            openedHandleCheck,
                            verificationProgress);
                        if (!published.Success)
                        {
                            await published.DisposeAsync();
                            throw new IOException(published.Error ?? "Frozen final-object verification failed.");
                        }

                        string expectedFinalIdentity = !string.IsNullOrWhiteSpace(targetJournal.FinalObjectIdentity)
                            ? targetJournal.FinalObjectIdentity
                            : targetJournal.TemporaryObjectIdentity ?? string.Empty;
                        finalVerifiedBytes[plan.TargetId] = Math.Min(
                            contentManifest.TotalBytes,
                            completedBeforeFinalVerification + entry.FileSize);
                        if (string.IsNullOrWhiteSpace(expectedFinalIdentity) ||
                            !string.Equals(expectedFinalIdentity, published.FileId, StringComparison.Ordinal))
                        {
                            await published.DisposeAsync();
                            throw new IOException(
                                $"Frozen final object identity changed for '{entry.RelativePath}' on {plan.Role}.");
                        }
                    }
                    else
                    {
                        if (!tempExists)
                        {
                            throw new FileNotFoundException(
                                $"Frozen recovery temporary object is missing for '{entry.RelativePath}' on {plan.Role}.",
                                paths.TempPath);
                        }
                        if (targetJournal.State is StandaloneTargetState.Verified or StandaloneTargetState.Published)
                        {
                            throw new IOException(
                                $"Frozen journal claims publication but the final object is missing for '{entry.RelativePath}' on {plan.Role}.");
                        }
                        if (string.IsNullOrWhiteSpace(targetJournal.TemporaryObjectIdentity))
                            throw new InvalidDataException("Frozen recovery temporary object identity is missing.");
                        if (GetPersistedOffset(targetJournal, entry.FileSize) != entry.FileSize)
                        {
                            throw new InvalidDataException(
                                $"Frozen recovery checkpoints are incomplete for '{entry.RelativePath}' on {plan.Role}.");
                        }

                        long completedBeforeTemporaryVerification = temporaryVerifiedBytes[plan.TargetId];
                        var temporaryProgress = new InlineProgress<FileVerificationProgress>(value =>
                        {
                            temporaryVerifiedBytes[plan.TargetId] = Math.Min(
                                contentManifest.TotalBytes,
                                completedBeforeTemporaryVerification + value.BytesVerified);
                            Report(
                                plan, contentManifest, targetJournal, CopyPhase.TemporaryVerifying,
                                entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                                temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        });
                        Report(
                            plan, contentManifest, targetJournal, CopyPhase.TemporaryVerifying,
                            entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                            temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        published = await _publisher.PublishAsync(
                            paths.TempPath,
                            paths.FinalPath,
                            entry.SourceHash!,
                            entry.FileSize,
                            cancellationToken,
                            directoryLease.EnsureContinuous,
                            openedHandleCheck,
                            expectedTempIdentity: null,
                            allowVerifiedExisting: false,
                            expectedTempObjectIdentity: targetJournal.TemporaryObjectIdentity,
                            temporaryVerificationProgress: temporaryProgress);
                        if (!published.Success)
                        {
                            await published.DisposeAsync();
                            throw new IOException(published.Error ?? "Frozen temporary-object verification failed.");
                        }
                        temporaryVerifiedBytes[plan.TargetId] = Math.Min(
                            contentManifest.TotalBytes,
                            completedBeforeTemporaryVerification + entry.FileSize);
                        targetTemporaryBytesRead += entry.FileSize;

                        long completedBeforeFinalVerification = finalVerifiedBytes[plan.TargetId];
                        var finalProgress = new InlineProgress<FileVerificationProgress>(value =>
                        {
                            finalVerifiedBytes[plan.TargetId] = Math.Min(
                                contentManifest.TotalBytes,
                                completedBeforeFinalVerification + value.BytesVerified);
                            Report(
                                plan, contentManifest, targetJournal, CopyPhase.FinalVerifying,
                                entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                                temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        });
                        Report(
                            plan, contentManifest, targetJournal, CopyPhase.FinalVerifying,
                            entry.RelativePath, verifiedCounts[plan.TargetId], completed: false,
                            temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
                        await published.EnsureContinuousAsync(cancellationToken, finalProgress);
                        finalVerifiedBytes[plan.TargetId] = Math.Min(
                            contentManifest.TotalBytes,
                            completedBeforeFinalVerification + entry.FileSize);
                    }

                    publishLeases.Add(published);
                    targetFinalBytesRead += entry.FileSize;
                    peakLeases = Math.Max(peakLeases, sourceLeases.Count + targetLeases.Count + publishLeases.Count);
                    peakProcessHandles = Math.Max(peakProcessHandles, Process.GetCurrentProcess().HandleCount);
                    await journalStore.OnFileVerifiedAsync(new CopyJournalFileVerified(
                        contentManifest.TaskId,
                        entry.Id,
                        plan.TargetId,
                        entry.RelativePath,
                        paths.FinalPath,
                        published.FileId ?? throw new IOException("Frozen final object identity is missing."),
                        entry.FileSize,
                        published.Hash ?? entry.SourceHash!,
                        ReusedExisting: false), cancellationToken);
                    verifiedCounts[plan.TargetId]++;
                    results.Add(new FileCopyResult
                    {
                        FileId = entry.Id,
                        TargetId = plan.TargetId,
                        RelativePath = entry.RelativePath,
                        Verified = true,
                        Hash = published.Hash,
                        FinalPath = paths.FinalPath,
                        TargetFileId = published.FileId,
                    });
                    peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                }
            }

            foreach (FreshTargetPlan plan in targets)
            {
                Report(
                    plan, contentManifest, null, CopyPhase.Completed,
                    currentFile: null, verifiedCounts[plan.TargetId], completed: true,
                    temporaryVerifiedBytes[plan.TargetId], finalVerifiedBytes[plan.TargetId]);
            }

            totalStopwatch.Stop();
            stageTimes["frozen-finalization"] = totalStopwatch.Elapsed;
            JournalPersistenceMetrics journalMetrics = journalStore.PersistenceMetrics;
            var metrics = new FreshTransferIoMetrics
            {
                PayloadBytes = contentManifest.TotalBytes,
                SourceContentBytesRead = sourceContentBytesRead,
                TargetBytesWritten = 0,
                TargetTemporaryBytesRead = targetTemporaryBytesRead,
                TargetFinalBytesRead = targetFinalBytesRead,
                RecoveryPrefixBytesRead = 0,
                PeakReadAheadBeyondDurableCheckpointBytes = 0,
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

    private static FrozenTargetContext OpenTargetContext(
        FreshTargetPlan plan,
        FaultDomainResolver resolver)
    {
        DualTargetCopyCoordinator.EnsureTargetStorageIdentity(
            resolver,
            plan.TargetRoot,
            plan.FaultDomainId,
            plan.StorageIdentity,
            plan.NasIdentity);
        FaultDomainInfo openedTarget = DualTargetCopyCoordinator.ResolveTargetDomain(
            resolver, plan.TargetRoot, plan.NasIdentity);
        DualTargetCopyCoordinator.OpenedTargetStorageBinding? storageBinding =
            DualTargetCopyCoordinator.CreateOpenedTargetStorageBinding(
                plan.TargetRoot, openedTarget, plan.StorageIdentity);
        return new FrozenTargetContext(plan, storageBinding);
    }

    private static FrozenTargetPaths ResolveAndValidatePaths(
        TaskManifest contentManifest,
        ManifestEntry entry,
        StandaloneTaskJournal taskJournal,
        StandaloneFileJournal fileJournal,
        FreshTargetPlan plan,
        StandaloneTargetFileJournal journal,
        FrozenTargetContext context)
    {
        string logicalFinalPath = CopyPathConvention.GetFinalPath(plan.TargetRoot, taskJournal, fileJournal);
        string logicalTempPath = CopyPathConvention.GetTempPath(
            plan.TargetRoot, taskJournal, fileJournal, contentManifest.TaskId, plan.TargetId);
        string finalPath = context.StorageBinding?.MapPath(logicalFinalPath) ?? logicalFinalPath;
        string tempPath = context.StorageBinding?.MapPath(logicalTempPath) ?? logicalTempPath;
        if (!PathsEqual(journal.FinalPath ?? string.Empty, finalPath) ||
            !PathsEqual(journal.TemporaryPath, tempPath))
        {
            throw new InvalidDataException(
                $"Frozen {plan.Role} paths changed for '{entry.RelativePath}'.");
        }
        return new FrozenTargetPaths(tempPath, finalPath);
    }

    private static TargetDirectoryContinuityLease AcquireTargetLease(
        FrozenTargetContext context,
        string finalPath)
    {
        string openedRoot = context.StorageBinding?.PhysicalRoot ?? context.Plan.TargetRoot;
        Action<SafeFileHandle, string>? rootHandleCheck = context.StorageBinding is null
            ? null
            : context.StorageBinding.EnsureRootHandle;
        TargetDirectoryContinuityLease lease = TargetDirectoryContinuityLease.Acquire(
            openedRoot, finalPath, rootHandleCheck);
        context.StorageBinding?.EnsureRoot(lease);
        return lease;
    }

    private static void EnsureFrozenJournalBinding(
        TaskManifest contentManifest,
        StandaloneTaskJournal journal,
        IReadOnlyList<FreshTargetPlan> targets)
    {
        if (journal.SchemaVersion < 2 || !journal.ContentManifestFrozen ||
            journal.TaskId != contentManifest.TaskId ||
            !string.Equals(journal.ManifestHash, contentManifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The frozen journal task or manifest binding is invalid.");
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
            throw new InvalidDataException("The selected frozen target plans do not match the journal target mode.");

        Dictionary<Guid, ManifestEntry> included = contentManifest.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.Id);
        if (included.Count == 0 || included.Count != journal.Files.Count)
            throw new InvalidDataException("The frozen journal file set does not match the content manifest.");

        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (!included.TryGetValue(file.FileId, out ManifestEntry? entry) ||
                file.Length != entry.FileSize ||
                !string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(file.SourceSha256, entry.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The frozen journal file facts changed from the content manifest.");
            }
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
                throw new InvalidDataException($"The frozen {target.Role} target plan changed from the journal binding.");
            }

            foreach (StandaloneFileJournal file in journal.Files)
            {
                StandaloneTargetFileJournal targetJournal = GetTargetJournal(file, target.Role);
                if (targetJournal.State is StandaloneTargetState.NotRequired or StandaloneTargetState.Pending or StandaloneTargetState.Failed ||
                    targetJournal.TargetId != target.TargetId ||
                    !string.Equals(targetJournal.TargetRole, target.Role, StringComparison.Ordinal) ||
                    !string.Equals(targetJournal.TargetIdentity, target.StorageIdentity, StringComparison.Ordinal) ||
                    !string.Equals(targetJournal.ExpectedSha256, file.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The frozen {target.Role} file target binding is inconsistent for '{file.RelativePath}'.");
                }
            }
        }
    }

    private static void EnsureSourceMetadataBinding(
        ManifestEntry entry,
        StandaloneFileJournal journal,
        SourceObjectIdentity current)
    {
        if (entry.FileSize != current.FileSize ||
            entry.LastModifiedUtc.ToUniversalTime() != current.LastModifiedUtc.ToUniversalTime() ||
            !string.Equals(entry.SourceFileIdType, current.FileIdType, StringComparison.Ordinal) ||
            !string.Equals(entry.SourceFileId, current.FileId, StringComparison.Ordinal) ||
            !string.Equals(journal.SourceFileIdentity, current.FileId, StringComparison.Ordinal))
        {
            throw new IOException($"Frozen source identity changed for '{entry.RelativePath}'.");
        }
    }

    private static long GetPersistedOffset(StandaloneTargetFileJournal journal, long fileLength)
    {
        StandaloneBlockCheckpoint[] ordered = journal.Checkpoints.OrderBy(value => value.BlockIndex).ToArray();
        long expectedOffset = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            StandaloneBlockCheckpoint checkpoint = ordered[index];
            if (checkpoint.BlockIndex != index || checkpoint.Offset != expectedOffset || checkpoint.Length <= 0 ||
                checkpoint.Length > ChunkedFileCopier.BlockSizeBytes)
            {
                throw new InvalidDataException("Frozen recovery checkpoints are not a contiguous valid prefix.");
            }
            expectedOffset += checkpoint.Length;
        }
        if (expectedOffset > fileLength ||
            (expectedOffset != fileLength && expectedOffset % ChunkedFileCopier.BlockSizeBytes != 0))
        {
            throw new InvalidDataException("Frozen recovery checkpoints exceed or misalign with the source length.");
        }
        return expectedOffset;
    }

    private static StandaloneTargetFileJournal GetTargetJournal(
        StandaloneFileJournal file,
        string role) => role switch
        {
            "local" => file.LocalTarget,
            "nas" => file.NasTarget,
            _ => throw new InvalidDataException($"Unsupported target role '{role}'."),
        };

    private static void Report(
        FreshTargetPlan plan,
        TaskManifest manifest,
        StandaloneTargetFileJournal? journal,
        CopyPhase phase,
        string? currentFile,
        int filesVerified,
        bool completed,
        long temporaryBytesVerified,
        long finalBytesVerified)
    {
        try
        {
            plan.Progress?.Report(new TargetCopyStatus
            {
                TaskId = manifest.TaskId,
                TargetId = plan.TargetId,
                FaultDomainId = plan.FaultDomainId,
                BytesCopied = completed ? manifest.TotalBytes : Math.Min(manifest.TotalBytes,
                    journal?.Checkpoints.Sum(checkpoint => checkpoint.Length) ?? manifest.TotalBytes),
                BytesTransferred = completed ? manifest.TotalBytes : Math.Min(manifest.TotalBytes,
                    journal?.Checkpoints.Sum(checkpoint => checkpoint.Length) ?? manifest.TotalBytes),
                TemporaryBytesVerified = Math.Min(manifest.TotalBytes, temporaryBytesVerified),
                FinalBytesVerified = Math.Min(manifest.TotalBytes, finalBytesVerified),
                TotalBytes = manifest.TotalBytes,
                TotalFiles = manifest.TotalFiles,
                CurrentFile = currentFile,
                Phase = phase,
                FilesVerified = filesVerified,
                FilesFailed = 0,
                IsComplete = completed,
            });
        }
        catch
        {
            // Progress is observational and cannot alter recovery correctness.
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

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record FrozenTargetContext(
        FreshTargetPlan Plan,
        DualTargetCopyCoordinator.OpenedTargetStorageBinding? StorageBinding);

    private sealed record FrozenTargetPaths(string TempPath, string FinalPath);
}
