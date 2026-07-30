using System.Security.Cryptography;
using AutoCardSync.Application.Copying;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Safety;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class FreshTransferRecoveryTests : IDisposable
{
    private const long PayloadBytes = 66L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-FreshRecovery", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Asymmetric_checkpoint_crash_truncates_only_identity_bound_unpersisted_suffix_and_resumes()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string localRoot = Path.Combine(_root, "local");
        string nasRoot = Path.Combine(_root, "nas");
        string relativePath = Path.Combine("DCIM", "recovery-66mb.bin");
        string sourcePath = Path.Combine(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(nasRoot);
        await WriteDeterministicFileAsync(sourcePath, PayloadBytes);

        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            taskId,
            sourceRoot,
            "standalone-v1-fresh-recovery",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".bin"]),
            CancellationToken.None);
        ManifestEntry entry = Assert.Single(inventory.Entries, item => !item.Excluded);
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        FaultDomainInfo localDomain = resolver.ResolveLocalDomain(localRoot);
        FaultDomainInfo nasDomain = resolver.ResolveLocalDomain(nasRoot);
        Guid localTargetId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        var store = new StandaloneTaskJournalStore(Path.Combine(_root, "journal"));
        await store.InitializeAsync(NewJournal(
            inventory,
            sourceDomain.StorageIdentity!,
            localRoot,
            localDomain.StorageIdentity!,
            localTargetId,
            nasRoot,
            nasDomain.StorageIdentity!,
            nasTargetId), CancellationToken.None);

        string localTemp = CopyPathConvention.GetTempPath(
            localRoot, relativePath, taskId, entry.Id, localTargetId);
        string nasTemp = CopyPathConvention.GetTempPath(
            nasRoot, relativePath, taskId, entry.Id, nasTargetId);
        string localFinal = CopyPathConvention.GetFinalPath(localRoot, relativePath);
        string nasFinal = CopyPathConvention.GetFinalPath(nasRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localTemp)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nasTemp)!);
        BlockCheckpoint firstCheckpoint = await WriteFirstCheckpointToBothTempsAsync(
            sourcePath, localTemp, nasTemp);
        string localTempIdentity = GetFileIdentity(localTemp);
        string nasTempIdentity = GetFileIdentity(nasTemp);

        await store.OnFileStartedAsync(new CopyJournalFileStarted(
            taskId,
            entry.Id,
            localTargetId,
            relativePath,
            entry.SourceFileId!,
            localTemp,
            localFinal,
            entry.FileSize,
            ExpectedSha256: null,
            TemporaryObjectId: localTempIdentity), CancellationToken.None);
        await store.OnFileStartedAsync(new CopyJournalFileStarted(
            taskId,
            entry.Id,
            nasTargetId,
            relativePath,
            entry.SourceFileId!,
            nasTemp,
            nasFinal,
            entry.FileSize,
            ExpectedSha256: null,
            TemporaryObjectId: nasTempIdentity), CancellationToken.None);
        await store.OnCheckpointCompletedAsync(new CopyJournalCheckpointCompleted(
            taskId,
            entry.Id,
            localTargetId,
            relativePath,
            localTempIdentity,
            firstCheckpoint), CancellationToken.None);

        StandaloneTaskJournal interrupted = (await store.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal interruptedFile = Assert.Single(interrupted.Files);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, new FileInfo(localTemp).Length);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, new FileInfo(nasTemp).Length);
        Assert.Single(interruptedFile.LocalTarget.Checkpoints);
        Assert.Empty(interruptedFile.NasTarget.Checkpoints);
        Assert.Equal(localTempIdentity, interruptedFile.LocalTarget.TemporaryObjectIdentity);
        Assert.Equal(nasTempIdentity, interruptedFile.NasTarget.TemporaryObjectIdentity);
        Assert.False(StandaloneSafetyDecision.Evaluate(BuildSafety(interrupted)).SafeToRemoveCard);

        var coordinator = new FreshTransferCoordinator();
        await using FreshTransferExecution execution = await coordinator.ExecuteAsync(
            inventory,
            sourceRoot,
            [
                NewPlan(localTargetId, "local", localRoot, localDomain),
                NewPlan(nasTargetId, "nas", nasRoot, nasDomain),
            ],
            store,
            resumeExistingTemps: true,
            CancellationToken.None);

        FreshTransferIoMetrics metrics = execution.Metrics;
        Assert.Equal(PayloadBytes, metrics.SourceContentBytesRead);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, metrics.RecoveryPrefixBytesRead);
        Assert.Equal(
            (PayloadBytes - ChunkedFileCopier.BlockSizeBytes) + PayloadBytes,
            metrics.TargetBytesWritten);
        Assert.Equal(PayloadBytes * 2, metrics.TargetTemporaryBytesRead);
        Assert.Equal(PayloadBytes * 2, metrics.TargetFinalBytesRead);
        Assert.Equal(0, metrics.EndOfTaskBatchFullRehashBytes);
        Assert.True(File.Exists(localFinal));
        Assert.True(File.Exists(nasFinal));
        Assert.False(File.Exists(localTemp));
        Assert.False(File.Exists(nasTemp));

        string localHash = await ComputeSha256Async(localFinal);
        string nasHash = await ComputeSha256Async(nasFinal);
        string sourceHash = execution.ContentManifest.Entries.Single(item => !item.Excluded).SourceHash!;
        Assert.Equal(sourceHash, localHash, ignoreCase: true);
        Assert.Equal(sourceHash, nasHash, ignoreCase: true);

        execution.RevalidateContinuity();
        await store.MarkCompletionReceiptPersistedAsync(CancellationToken.None);
        execution.RevalidateContinuity();
        StandaloneTaskJournal completed = (await store.LoadAsync(CancellationToken.None))!;
        Assert.All(completed.Files, file => Assert.Equal(StandaloneFileState.Verified, file.State));
        Assert.True(StandaloneSafetyDecision.Evaluate(BuildSafety(completed)).SafeToRemoveCard);
    }

    [Fact]
    public async Task Identical_existing_final_is_verified_and_reused_instead_of_permanently_blocking_retry()
    {
        string sourceRoot = Path.Combine(_root, "existing-source");
        string targetRoot = Path.Combine(_root, "existing-target");
        string relativePath = Path.Combine("DCIM", "existing.bin");
        string sourcePath = Path.Combine(sourceRoot, relativePath);
        string finalPath = Path.Combine(targetRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        byte[] content = Enumerable.Range(0, 8192).Select(index => unchecked((byte)(index * 31))).ToArray();
        await File.WriteAllBytesAsync(sourcePath, content);
        await File.WriteAllBytesAsync(finalPath, content);

        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            taskId,
            sourceRoot,
            "verified-existing-retry",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".bin"]),
            CancellationToken.None);
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        FaultDomainInfo targetDomain = resolver.ResolveLocalDomain(targetRoot);
        Guid targetId = Guid.NewGuid();
        var store = new StandaloneTaskJournalStore(Path.Combine(_root, "existing-journal"));
        await store.InitializeAsync(NewLocalJournal(
            inventory,
            sourceDomain.StorageIdentity!,
            targetRoot,
            targetDomain.StorageIdentity!,
            targetId), CancellationToken.None);

        await using FreshTransferExecution execution = await new FreshTransferCoordinator().ExecuteAsync(
            inventory,
            sourceRoot,
            [NewPlan(targetId, "local", targetRoot, targetDomain)],
            store,
            resumeExistingTemps: false,
            CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(finalPath));
        Assert.Equal(
            execution.ContentManifest.Entries.Single(entry => !entry.Excluded).SourceHash,
            await ComputeSha256Async(finalPath),
            ignoreCase: true);
        Assert.False(File.Exists(CopyPathConvention.GetTempPath(
            targetRoot,
            relativePath,
            taskId,
            inventory.Entries.Single(entry => !entry.Excluded).Id,
            targetId)));
        StandaloneTaskJournal completed = Assert.IsType<StandaloneTaskJournal>(
            await store.LoadAsync(CancellationToken.None));
        StandaloneFileJournal completedFile = Assert.Single(completed.Files);
        Assert.Equal(StandaloneFileState.Verified, completedFile.State);
        Assert.True(completedFile.LocalTarget.ReusedExisting);
        Assert.False(completedFile.LocalTarget.AtomicallyPublished);
        Assert.True(completedFile.LocalTarget.FullRereadSha256Passed);
    }

    private static StandaloneTaskJournal NewLocalJournal(
        TaskManifest inventory,
        string sourceIdentity,
        string localRoot,
        string localIdentity,
        Guid localTargetId) => new()
        {
            SchemaVersion = 2,
            TaskId = inventory.TaskId,
            SourceIdentity = sourceIdentity,
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalOnly,
            InventoryManifestHash = inventory.ManifestHash,
            ManifestHash = string.Empty,
            ContentManifestFrozen = false,
            LocalTargetIdentity = localIdentity,
            LocalTargetRoot = localRoot,
            NasTargetIdentity = string.Empty,
            NasTargetRoot = string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = inventory.Entries.Where(item => !item.Excluded).Select(item => new StandaloneFileJournal
            {
                FileId = item.Id,
                RelativePath = item.RelativePath,
                Length = item.FileSize,
                SourceSha256 = string.Empty,
                State = StandaloneFileState.Pending,
                LocalTarget = NewTarget(localTargetId, "local", localIdentity),
                NasTarget = new StandaloneTargetFileJournal
                {
                    TargetId = Guid.NewGuid(),
                    TargetRole = "nas",
                    TargetIdentity = string.Empty,
                    TemporaryPath = string.Empty,
                    State = StandaloneTargetState.NotRequired,
                },
            }).ToArray(),
        };

    private static StandaloneTaskJournal NewJournal(
        TaskManifest inventory,
        string sourceIdentity,
        string localRoot,
        string localIdentity,
        Guid localTargetId,
        string nasRoot,
        string nasIdentity,
        Guid nasTargetId) => new()
        {
            SchemaVersion = 2,
            TaskId = inventory.TaskId,
            SourceIdentity = sourceIdentity,
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalAndNas,
            InventoryManifestHash = inventory.ManifestHash,
            ManifestHash = string.Empty,
            ContentManifestFrozen = false,
            LocalTargetIdentity = localIdentity,
            LocalTargetRoot = localRoot,
            NasTargetIdentity = nasIdentity,
            NasTargetRoot = nasRoot,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = inventory.Entries.Where(item => !item.Excluded).Select(item => new StandaloneFileJournal
            {
                FileId = item.Id,
                RelativePath = item.RelativePath,
                Length = item.FileSize,
                SourceSha256 = string.Empty,
                State = StandaloneFileState.Pending,
                LocalTarget = NewTarget(localTargetId, "local", localIdentity),
                NasTarget = NewTarget(nasTargetId, "nas", nasIdentity),
            }).ToArray(),
        };

    private static StandaloneTargetFileJournal NewTarget(
        Guid targetId,
        string role,
        string identity) => new()
        {
            TargetId = targetId,
            TargetRole = role,
            TargetIdentity = identity,
            TemporaryPath = string.Empty,
            State = StandaloneTargetState.Pending,
        };

    private static FreshTargetPlan NewPlan(
        Guid targetId,
        string role,
        string root,
        FaultDomainInfo domain) => new(
            targetId,
            role,
            root,
            domain.FaultDomainId,
            domain.StorageIdentity!,
            NasIdentity: null);

    private static StandaloneSafetyFacts BuildSafety(StandaloneTaskJournal journal) => new()
    {
        TargetMode = journal.TargetMode,
        ManifestFrozen = journal.ContentManifestFrozen,
        SourceReadOnly = true,
        AllIncludedFilesAccountedFor = journal.Files.Count > 0 &&
            journal.Files.All(file => file.State == StandaloneFileState.Verified),
        LocalTargetFullRereadSha256Passed = journal.Files.All(file => file.LocalTarget.FullRereadSha256Passed),
        NasTargetFullRereadSha256Passed = journal.Files.All(file => file.NasTarget.FullRereadSha256Passed),
        FinalObjectsSafelyPublishedOrReused = journal.Files.All(file =>
            (file.LocalTarget.AtomicallyPublished || file.LocalTarget.ReusedExisting) &&
            (file.NasTarget.AtomicallyPublished || file.NasTarget.ReusedExisting)),
        SourceIdentityUnchanged = true,
        TargetIdentitiesUnchanged = true,
        FailedIncludedFiles = journal.Files.Count(file => file.State == StandaloneFileState.Failed),
        PendingIncludedFiles = journal.Files.Count(file => file.State != StandaloneFileState.Verified),
        LocalCompletionReceiptPersisted = journal.LocalCompletionReceiptPersisted,
    };

    private static async Task<BlockCheckpoint> WriteFirstCheckpointToBothTempsAsync(
        string sourcePath,
        string localTemp,
        string nasTemp)
    {
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var local = new FileStream(
            localTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var nas = new FileStream(
            nasTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (copied < ChunkedFileCopier.BlockSizeBytes)
        {
            int requested = (int)Math.Min(buffer.Length, ChunkedFileCopier.BlockSizeBytes - copied);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested));
            Assert.Equal(requested, read);
            hash.AppendData(buffer.AsSpan(0, read));
            await local.WriteAsync(buffer.AsMemory(0, read));
            await nas.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
        }
        await local.FlushAsync();
        await nas.FlushAsync();
        return new BlockCheckpoint(
            0,
            0,
            copied,
            Convert.ToHexStringLower(hash.GetHashAndReset()),
            DateTimeOffset.UtcNow);
    }

    private static string GetFileIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        FileIdentity identity = FileIdentity.GetFileIdentity(stream.SafeFileHandle, path);
        return $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";
    }

    private static async Task WriteDeterministicFileAsync(string path, long length)
    {
        byte[] buffer = new byte[1024 * 1024];
        long written = 0;
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (written < length)
        {
            int count = (int)Math.Min(buffer.Length, length - written);
            for (int index = 0; index < count; index++)
                buffer[index] = unchecked((byte)((written + index) * 17 + ((written + index) >> 9)));
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
        await stream.FlushAsync();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
