using AutoCardSync.Application.Copying;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Hashing;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Safety;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class DualTargetCopyIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-DualTarget", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Approved_files_are_atomically_published_and_fully_verified_on_both_targets()
    {
        string source = Path.Combine(_root, "source");
        string local = Path.Combine(_root, "local");
        string backup = Path.Combine(_root, "backup");
        string journalPath = Path.Combine(_root, "journal", "task.json");
        Directory.CreateDirectory(Path.Combine(source, "DCIM", "100MEDIA"));
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(backup);
        byte[] approvedBytes = Enumerable.Range(0, 256 * 1024)
            .Select(index => (byte)(index % 251)).ToArray();
        string approvedPath = Path.Combine(source, "DCIM", "100MEDIA", "clip.jpg");
        await File.WriteAllBytesAsync(approvedPath, approvedBytes);
        await File.WriteAllTextAsync(Path.Combine(source, "DCIM", "ignore.txt"), "not approved");

        Guid taskId = Guid.NewGuid();
        var manifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
        TaskManifest manifest = await manifestBuilder.BuildAsync(
            taskId,
            source,
            "standalone-v1",
            SourceHashPolicy.Sha256DuringScan,
            new SourceSelectionPolicy(["DCIM"], [".jpg"]),
            CancellationToken.None);
        ManifestEntry included = Assert.Single(manifest.Entries, entry => !entry.Excluded);
        Assert.Single(manifest.Entries, entry => entry.Excluded);

        var domainResolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = domainResolver.ResolveLocalDomain(source);
        FaultDomainInfo localDomain = domainResolver.ResolveLocalDomain(local);
        FaultDomainInfo backupDomain = domainResolver.ResolveLocalDomain(backup);
        Guid localId = Guid.NewGuid();
        Guid backupId = Guid.NewGuid();
        var journalStore = new StandaloneTaskJournalStore(journalPath);
        await journalStore.InitializeAsync(new StandaloneTaskJournal
        {
            TaskId = taskId,
            SourceIdentity = sourceDomain.StorageIdentity!,
            CardInstanceId = Guid.NewGuid(),
            ManifestHash = manifest.ManifestHash,
            LocalTargetIdentity = localDomain.StorageIdentity!,
            LocalTargetRoot = local,
            NasTargetIdentity = backupDomain.StorageIdentity!,
            NasTargetRoot = backup,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = manifest.Entries.Where(entry => !entry.Excluded).Select(entry => new StandaloneFileJournal
            {
                FileId = entry.Id,
                RelativePath = entry.RelativePath,
                Length = entry.FileSize,
                SourceSha256 = entry.SourceHash!,
                State = StandaloneFileState.Pending,
                LocalTarget = NewTarget(localId, "local", localDomain.StorageIdentity!),
                NasTarget = NewTarget(backupId, "nas", backupDomain.StorageIdentity!),
            }).ToArray(),
        }, CancellationToken.None);

        var coordinator = new DualTargetCopyCoordinator();
        Task<IReadOnlyList<FileCopyResult>> localTask = coordinator.CopyToTargetAsync(
            manifest, source, local, localId, localDomain.FaultDomainId,
            progress: null, CancellationToken.None, localDomain.StorageIdentity,
            journalSink: journalStore);
        Task<IReadOnlyList<FileCopyResult>> backupTask = coordinator.CopyToTargetAsync(
            manifest, source, backup, backupId, backupDomain.FaultDomainId,
            progress: null, CancellationToken.None, backupDomain.StorageIdentity,
            journalSink: journalStore);
        await Task.WhenAll(localTask, backupTask);
        IReadOnlyList<FileCopyResult> localResults = await localTask;
        IReadOnlyList<FileCopyResult> backupResults = await backupTask;

        Assert.All(localResults, result => Assert.True(result.Verified, result.Error));
        Assert.All(backupResults, result => Assert.True(result.Verified, result.Error));
        string localFinal = Path.Combine(local, included.RelativePath);
        string backupFinal = Path.Combine(backup, included.RelativePath);
        var verifier = new Sha256Verifier();
        Assert.Equal(included.SourceHash, await verifier.ComputeFileHashAsync(localFinal, CancellationToken.None));
        Assert.Equal(included.SourceHash, await verifier.ComputeFileHashAsync(backupFinal, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(local, "DCIM", "ignore.txt")));
        Assert.False(File.Exists(Path.Combine(backup, "DCIM", "ignore.txt")));
        Assert.Empty(Directory.EnumerateFiles(local, "*.partial.*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(backup, "*.partial.*", SearchOption.AllDirectories));

        await journalStore.MarkCompletionReceiptPersistedAsync(CancellationToken.None);
        StandaloneTaskJournal persisted = (await journalStore.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal file = Assert.Single(persisted.Files);
        Assert.Equal(StandaloneFileState.Verified, file.State);
        Assert.Equal(StandaloneTargetState.Verified, file.LocalTarget.State);
        Assert.Equal(StandaloneTargetState.Verified, file.NasTarget.State);
        Assert.True(file.LocalTarget.AtomicallyPublished);
        Assert.True(file.NasTarget.AtomicallyPublished);
        Assert.True(file.LocalTarget.FullRereadSha256Passed);
        Assert.True(file.NasTarget.FullRereadSha256Passed);
        Assert.Single(file.LocalTarget.Checkpoints);
        Assert.Single(file.NasTarget.Checkpoints);
        Assert.True(persisted.LocalCompletionReceiptPersisted);

        StandaloneSafetyResult safety = StandaloneSafetyDecision.Evaluate(new StandaloneSafetyFacts
        {
            ManifestFrozen = !string.IsNullOrWhiteSpace(manifest.ManifestHash),
            SourceReadOnly = true,
            AllIncludedFilesAccountedFor = persisted.Files.Count == manifest.TotalFiles,
            LocalTargetFullRereadSha256Passed = persisted.Files.All(value => value.LocalTarget.FullRereadSha256Passed),
            NasTargetFullRereadSha256Passed = persisted.Files.All(value => value.NasTarget.FullRereadSha256Passed),
            FinalObjectsSafelyPublishedOrReused = persisted.Files.All(value =>
                (value.LocalTarget.AtomicallyPublished || value.LocalTarget.ReusedExisting) &&
                (value.NasTarget.AtomicallyPublished || value.NasTarget.ReusedExisting)),
            SourceIdentityUnchanged = true,
            TargetIdentitiesUnchanged = true,
            FailedIncludedFiles = persisted.Files.Count(value => value.State == StandaloneFileState.Failed),
            PendingIncludedFiles = persisted.Files.Count(value => value.State != StandaloneFileState.Verified),
            LocalCompletionReceiptPersisted = persisted.LocalCompletionReceiptPersisted,
        });
        Assert.True(safety.SafeToRemoveCard);
    }

    [Fact]
    public async Task Inconsistent_existing_final_file_fails_closed_and_is_not_overwritten()
    {
        string source = Path.Combine(_root, "source-conflict");
        string target = Path.Combine(_root, "target-conflict");
        Directory.CreateDirectory(Path.Combine(source, "DCIM"));
        Directory.CreateDirectory(Path.Combine(target, "DCIM"));
        await File.WriteAllTextAsync(Path.Combine(source, "DCIM", "clip.jpg"), "expected");
        string existing = Path.Combine(target, "DCIM", "clip.jpg");
        await File.WriteAllTextAsync(existing, "do-not-overwrite");
        var builder = new ManifestBuilder(new FileSystemSourceEnumerator());
        TaskManifest manifest = await builder.BuildAsync(
            Guid.NewGuid(), source, "standalone-v1", SourceHashPolicy.Sha256DuringScan,
            new SourceSelectionPolicy(["DCIM"], [".jpg"]), CancellationToken.None);
        FaultDomainInfo domain = new FaultDomainResolver().ResolveLocalDomain(target);

        IReadOnlyList<FileCopyResult> results = await new DualTargetCopyCoordinator().CopyToTargetAsync(
            manifest, source, target, Guid.NewGuid(), domain.FaultDomainId,
            progress: null, CancellationToken.None, domain.StorageIdentity);

        FileCopyResult result = Assert.Single(results);
        Assert.False(result.Verified);
        Assert.Equal("do-not-overwrite", await File.ReadAllTextAsync(existing));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

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
}



