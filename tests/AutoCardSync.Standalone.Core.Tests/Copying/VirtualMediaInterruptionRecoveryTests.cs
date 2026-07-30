using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Application.Copying;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.Hashing;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Safety;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class VirtualMediaInterruptionRecoveryTests : IDisposable
{
    private const long VirtualMediaBytes = 130L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-VirtualMedia", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Interrupted_after_first_checkpoint_resumes_from_same_identities_and_only_then_allows_safe_removal()
    {
        string source = Path.Combine(_root, "source");
        string local = Path.Combine(_root, "local");
        string backup = Path.Combine(_root, "backup");
        string journalPath = Path.Combine(_root, "journal", "task.json");
        string relativePath = Path.Combine("DCIM", "100MEDIA", "virtual-130mb.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relativePath))!);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(backup);
        await WriteDeterministicFileAsync(Path.Combine(source, relativePath), VirtualMediaBytes);

        Guid taskId = Guid.NewGuid();
        var manifest = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            taskId,
            source,
            "standalone-v1",
            SourceHashPolicy.Sha256DuringScan,
            new SourceSelectionPolicy(["DCIM"], [".bin"]),
            CancellationToken.None);
        ManifestEntry included = Assert.Single(manifest.Entries, entry => !entry.Excluded);
        Assert.Equal(VirtualMediaBytes, included.FileSize);

        var domainResolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = domainResolver.ResolveLocalDomain(source);
        FaultDomainInfo localDomain = domainResolver.ResolveLocalDomain(local);
        FaultDomainInfo backupDomain = domainResolver.ResolveLocalDomain(backup);
        Guid cardInstanceId = Guid.NewGuid();
        Guid localId = Guid.NewGuid();
        Guid backupId = Guid.NewGuid();
        var journalStore = new StandaloneTaskJournalStore(journalPath);
        await journalStore.InitializeAsync(NewJournal(
            taskId, sourceDomain.StorageIdentity!, cardInstanceId, manifest, local,
            localDomain.StorageIdentity!, localId, backup, backupDomain.StorageIdentity!, backupId),
            CancellationToken.None);

        var interruptingSink = new InterruptAfterCheckpointSink(journalStore, localId);
        var coordinator = new DualTargetCopyCoordinator();
        var interruption = await Assert.ThrowsAsync<VirtualMediaInterruptedException>(() =>
            coordinator.CopyToTargetAsync(
                manifest, source, local, localId, localDomain.FaultDomainId,
                progress: null, CancellationToken.None, localDomain.StorageIdentity,
                resumeExistingTemps: false, journalSink: interruptingSink));
        Assert.Equal(0, interruption.Checkpoint.BlockIndex);
        Assert.Equal(0, interruption.Checkpoint.Offset);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, interruption.Checkpoint.Length);

        string localFinal = Path.Combine(local, relativePath);
        string backupFinal = Path.Combine(backup, relativePath);
        Assert.False(File.Exists(localFinal));
        Assert.False(File.Exists(backupFinal));

        StandaloneTaskJournal interrupted = (await journalStore.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal interruptedFile = Assert.Single(interrupted.Files);
        Assert.Equal(taskId, interrupted.TaskId);
        Assert.Equal(sourceDomain.StorageIdentity, interrupted.SourceIdentity);
        Assert.Equal(manifest.ManifestHash, interrupted.ManifestHash);
        Assert.Equal(localDomain.StorageIdentity, interrupted.LocalTargetIdentity);
        Assert.Equal(backupDomain.StorageIdentity, interrupted.NasTargetIdentity);
        Assert.Equal(included.Id, interruptedFile.FileId);
        Assert.Equal(included.SourceHash, interruptedFile.SourceSha256);
        Assert.Equal(StandaloneFileState.Copying, interruptedFile.State);
        Assert.Equal(StandaloneTargetState.Copying, interruptedFile.LocalTarget.State);
        Assert.Equal(StandaloneTargetState.Pending, interruptedFile.NasTarget.State);
        Assert.False(string.IsNullOrWhiteSpace(interruptedFile.LocalTarget.TemporaryObjectIdentity));
        StandaloneBlockCheckpoint persistedCheckpoint = Assert.Single(interruptedFile.LocalTarget.Checkpoints);
        Assert.Equal(0, persistedCheckpoint.BlockIndex);
        Assert.Equal(0, persistedCheckpoint.Offset);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, persistedCheckpoint.Length);
        Assert.Equal(ChunkedFileCopier.BlockSizeBytes, new FileInfo(interruptedFile.LocalTarget.TemporaryPath).Length);
        AssertPrefixMatches(Path.Combine(source, relativePath), interruptedFile.LocalTarget.TemporaryPath,
            ChunkedFileCopier.BlockSizeBytes);
        Assert.False(SafetyFor(interrupted, manifest).SafeToRemoveCard);

        RecoveryIdentitySnapshot currentIdentity = new(
            sourceDomain.StorageIdentity!, cardInstanceId, manifest.ManifestHash,
            localDomain.StorageIdentity!, backupDomain.StorageIdentity!);
        Assert.True(RecoveryGuard.Evaluate(interrupted, currentIdentity).CanResume);
        Assert.False(RecoveryGuard.Evaluate(interrupted, currentIdentity with { SourceIdentity = "fake-source" }).CanResume);
        Assert.False(RecoveryGuard.Evaluate(interrupted, currentIdentity with { ManifestHash = "fake-manifest" }).CanResume);
        Assert.False(RecoveryGuard.Evaluate(interrupted, currentIdentity with { LocalTargetIdentity = "fake-local" }).CanResume);
        Assert.False(RecoveryGuard.Evaluate(interrupted, currentIdentity with { NasTargetIdentity = "fake-nas" }).CanResume);

        await Task.Delay(TimeSpan.FromMilliseconds(25));
        IReadOnlyList<FileCopyResult> resumedLocal = await coordinator.CopyToTargetAsync(
            manifest, source, local, localId, localDomain.FaultDomainId,
            progress: null, CancellationToken.None, localDomain.StorageIdentity,
            resumeExistingTemps: true, journalSink: journalStore);
        IReadOnlyList<FileCopyResult> copiedBackup = await coordinator.CopyToTargetAsync(
            manifest, source, backup, backupId, backupDomain.FaultDomainId,
            progress: null, CancellationToken.None, backupDomain.StorageIdentity,
            resumeExistingTemps: false, journalSink: journalStore);

        FileCopyResult localResult = Assert.Single(resumedLocal);
        FileCopyResult backupResult = Assert.Single(copiedBackup);
        Assert.True(localResult.Verified, localResult.Error);
        Assert.True(backupResult.Verified, backupResult.Error);
        Assert.False(File.Exists(interruptedFile.LocalTarget.TemporaryPath));
        Assert.False(Directory.EnumerateFiles(local, "*.partial.*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.EnumerateFiles(backup, "*.partial.*", SearchOption.AllDirectories).Any());

        var verifier = new Sha256Verifier();
        string localSha256 = await verifier.ComputeFileHashAsync(localFinal, CancellationToken.None);
        string backupSha256 = await verifier.ComputeFileHashAsync(backupFinal, CancellationToken.None);
        Assert.Equal(included.SourceHash, localSha256);
        Assert.Equal(included.SourceHash, backupSha256);

        await journalStore.MarkCompletionReceiptPersistedAsync(CancellationToken.None);
        StandaloneTaskJournal completed = (await journalStore.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal completedFile = Assert.Single(completed.Files);
        Assert.Equal(StandaloneFileState.Verified, completedFile.State);
        Assert.Equal(StandaloneTargetState.Verified, completedFile.LocalTarget.State);
        Assert.Equal(StandaloneTargetState.Verified, completedFile.NasTarget.State);
        Assert.True(completedFile.LocalTarget.AtomicallyPublished);
        Assert.True(completedFile.NasTarget.AtomicallyPublished);
        Assert.True(completedFile.LocalTarget.FullRereadSha256Passed);
        Assert.True(completedFile.NasTarget.FullRereadSha256Passed);
        Assert.Equal(0, completed.Files.Count(file => file.State == StandaloneFileState.Pending));
        Assert.Equal(0, completed.Files.Count(file => file.State == StandaloneFileState.Failed));
        Assert.True(SafetyFor(completed, manifest).SafeToRemoveCard);
    }

    [Fact]
    public async Task Fake_volume_notifications_cancel_on_confirmed_removal_and_only_reenumerate_on_arrival()
    {
        var card = new VolumeEventArgs("V:\\", "virtual-card", "exFAT", 64 * 1024 * 1024, DateTimeOffset.UtcNow);
        var snapshots = new FakeVolumeSnapshotProvider([card]);
        var backend = new FakeVolumeNotificationBackend();
        await using var listener = new VolumeNotificationListener(
            snapshots, backend, () => new FakeVolumeNotificationBackend(),
            debounceWindow: TimeSpan.Zero, settleDelay: TimeSpan.Zero);
        int removed = 0;
        int arrived = 0;
        using var activeCopy = new CancellationTokenSource();
        listener.VolumeRemoved += (_, volume) =>
        {
            if (volume.VolumeGuid == card.VolumeGuid)
            {
                removed++;
                activeCopy.Cancel();
            }
        };
        listener.VolumeArrived += (_, volume) =>
        {
            if (volume.VolumeGuid == card.VolumeGuid)
                arrived++;
        };

        await listener.StartAsync(CancellationToken.None);
        Assert.Equal(1, snapshots.Enumerations);

        backend.EmitRemoval();
        await WaitUntilAsync(() => snapshots.Enumerations >= 2);
        Assert.Equal(0, removed);
        Assert.False(activeCopy.IsCancellationRequested);

        snapshots.Set([]);
        backend.EmitRemoval();
        await WaitUntilAsync(() => removed == 1);
        Assert.True(activeCopy.IsCancellationRequested);

        backend.EmitArrival();
        await WaitUntilAsync(() => snapshots.Enumerations >= 4);
        Assert.Equal(0, arrived);

        snapshots.Set([card]);
        backend.EmitArrival();
        await WaitUntilAsync(() => arrived == 1);
        Assert.True(activeCopy.IsCancellationRequested);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static StandaloneTaskJournal NewJournal(
        Guid taskId,
        string sourceIdentity,
        Guid cardInstanceId,
        TaskManifest manifest,
        string localRoot,
        string localIdentity,
        Guid localId,
        string backupRoot,
        string backupIdentity,
        Guid backupId) => new()
        {
            TaskId = taskId,
            SourceIdentity = sourceIdentity,
            CardInstanceId = cardInstanceId,
            ManifestHash = manifest.ManifestHash,
            LocalTargetIdentity = localIdentity,
            LocalTargetRoot = localRoot,
            NasTargetIdentity = backupIdentity,
            NasTargetRoot = backupRoot,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = manifest.Entries.Where(entry => !entry.Excluded).Select(entry => new StandaloneFileJournal
            {
                FileId = entry.Id,
                RelativePath = entry.RelativePath,
                Length = entry.FileSize,
                SourceSha256 = entry.SourceHash!,
                State = StandaloneFileState.Pending,
                LocalTarget = NewTarget(localId, "local", localIdentity),
                NasTarget = NewTarget(backupId, "nas", backupIdentity),
            }).ToArray(),
        };

    private static StandaloneTargetFileJournal NewTarget(Guid targetId, string role, string identity) => new()
    {
        TargetId = targetId,
        TargetRole = role,
        TargetIdentity = identity,
        TemporaryPath = string.Empty,
        State = StandaloneTargetState.Pending,
    };

    private static StandaloneSafetyResult SafetyFor(StandaloneTaskJournal journal, TaskManifest manifest) =>
        StandaloneSafetyDecision.Evaluate(new StandaloneSafetyFacts
        {
            ManifestFrozen = !string.IsNullOrWhiteSpace(manifest.ManifestHash),
            SourceReadOnly = true,
            AllIncludedFilesAccountedFor = journal.Files.Count == manifest.TotalFiles,
            LocalTargetFullRereadSha256Passed = journal.Files.All(value => value.LocalTarget.FullRereadSha256Passed),
            NasTargetFullRereadSha256Passed = journal.Files.All(value => value.NasTarget.FullRereadSha256Passed),
            FinalObjectsSafelyPublishedOrReused = journal.Files.All(value =>
                (value.LocalTarget.AtomicallyPublished || value.LocalTarget.ReusedExisting) &&
                (value.NasTarget.AtomicallyPublished || value.NasTarget.ReusedExisting)),
            SourceIdentityUnchanged = true,
            TargetIdentitiesUnchanged = true,
            FailedIncludedFiles = journal.Files.Count(value => value.State == StandaloneFileState.Failed),
            PendingIncludedFiles = journal.Files.Count(value => value.State != StandaloneFileState.Verified),
            LocalCompletionReceiptPersisted = journal.LocalCompletionReceiptPersisted,
        });

    private static async Task WriteDeterministicFileAsync(string path, long length)
    {
        byte[] buffer = new byte[1024 * 1024];
        long written = 0;
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: buffer.Length, useAsync: true);
        while (written < length)
        {
            int count = (int)Math.Min(buffer.Length, length - written);
            for (int i = 0; i < count; i++)
                buffer[i] = unchecked((byte)((written + i) * 31 + ((written + i) >> 11)));
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
    }

    private static void AssertPrefixMatches(string sourcePath, string tempPath, long length)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var temp = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] sourceBuffer = new byte[1024 * 1024];
        byte[] tempBuffer = new byte[1024 * 1024];
        long compared = 0;
        while (compared < length)
        {
            int count = (int)Math.Min(sourceBuffer.Length, length - compared);
            Assert.Equal(count, source.Read(sourceBuffer, 0, count));
            Assert.Equal(count, temp.Read(tempBuffer, 0, count));
            Assert.True(sourceBuffer.AsSpan(0, count).SequenceEqual(tempBuffer.AsSpan(0, count)),
                $"Prefix mismatch at offset {compared}.");
            compared += count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class InterruptAfterCheckpointSink(
        ICopyJournalSink inner,
        Guid interruptedTargetId) : ICopyJournalSink
    {
        private bool _thrown;

        public ValueTask OnFileStartedAsync(CopyJournalFileStarted value, CancellationToken cancellationToken) =>
            inner.OnFileStartedAsync(value, cancellationToken);

        public async ValueTask OnCheckpointCompletedAsync(
            CopyJournalCheckpointCompleted value,
            CancellationToken cancellationToken)
        {
            await inner.OnCheckpointCompletedAsync(value, cancellationToken);
            if (!_thrown && value.TargetId == interruptedTargetId && value.Checkpoint.BlockIndex == 0)
            {
                _thrown = true;
                throw new VirtualMediaInterruptedException(value.Checkpoint);
            }
        }

        public ValueTask OnFileVerifiedAsync(CopyJournalFileVerified value, CancellationToken cancellationToken) =>
            inner.OnFileVerifiedAsync(value, cancellationToken);

        public ValueTask OnFileFailedAsync(CopyJournalFileFailed value, CancellationToken cancellationToken) =>
            inner.OnFileFailedAsync(value, cancellationToken);
    }

    private sealed class VirtualMediaInterruptedException(BlockCheckpoint checkpoint)
        : OperationCanceledException("Virtual media interrupted after a persisted checkpoint.")
    {
        public BlockCheckpoint Checkpoint { get; } = checkpoint;
    }

    private sealed class FakeVolumeSnapshotProvider(IReadOnlyList<VolumeEventArgs> initial) : IVolumeSnapshotProvider
    {
        private readonly object _gate = new();
        private IReadOnlyList<VolumeEventArgs> _volumes = initial;

        public int Enumerations { get; private set; }

        public IReadOnlyList<VolumeEventArgs> EnumerateMountedVolumes()
        {
            lock (_gate)
            {
                Enumerations++;
                return _volumes.ToArray();
            }
        }

        public void Set(IReadOnlyList<VolumeEventArgs> volumes)
        {
            lock (_gate)
                _volumes = volumes;
        }
    }

    private sealed class FakeVolumeNotificationBackend : IVolumeNotificationBackend
    {
        public event EventHandler<VolumeSignal>? Signal;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitRemoval() =>
            Signal?.Invoke(this, new VolumeSignal(VolumeSignalKind.Removal, DateTimeOffset.UtcNow));

        public void EmitArrival() =>
            Signal?.Invoke(this, new VolumeSignal(VolumeSignalKind.Arrival, DateTimeOffset.UtcNow));
    }
}
