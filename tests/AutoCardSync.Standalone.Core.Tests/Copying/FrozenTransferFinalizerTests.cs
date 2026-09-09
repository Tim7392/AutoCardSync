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

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class FrozenTransferFinalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-FrozenFinalizer", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Content_frozen_dual_finalization_reads_source_once_and_publishes_both_targets()
    {
        FrozenFixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        var finalizer = new FrozenTransferFinalizer();

        await using FreshTransferExecution execution = await finalizer.FinalizeAsync(
            fixture.ContentManifest,
            fixture.SourceRoot,
            fixture.Plans,
            fixture.Store,
            CancellationToken.None);

        Assert.Equal(fixture.Length, execution.Metrics.SourceContentBytesRead);
        Assert.Equal(0, execution.Metrics.TargetBytesWritten);
        Assert.Equal(fixture.Length * 2, execution.Metrics.TargetTemporaryBytesRead);
        Assert.Equal(fixture.Length * 2, execution.Metrics.TargetFinalBytesRead);
        Assert.Equal(0, execution.Metrics.EndOfTaskBatchFullRehashBytes);
        Assert.Equal(2, execution.Results.Count);
        Assert.All(execution.Results, result => Assert.True(result.Verified));
        Assert.False(File.Exists(fixture.LocalTemp));
        Assert.False(File.Exists(fixture.NasTemp));
        Assert.Equal(fixture.SourceSha256, await ComputeSha256Async(fixture.LocalFinal));
        Assert.Equal(fixture.SourceSha256, await ComputeSha256Async(fixture.NasFinal));

        StandaloneTaskJournal journal = (await fixture.Store.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal file = Assert.Single(journal.Files);
        Assert.Equal(StandaloneFileState.Verified, file.State);
        Assert.Equal(StandaloneTargetState.Verified, file.LocalTarget.State);
        Assert.Equal(StandaloneTargetState.Verified, file.NasTarget.State);
        Assert.True(file.LocalTarget.AtomicallyPublished);
        Assert.True(file.NasTarget.AtomicallyPublished);
        Assert.True(file.LocalTarget.FullRereadSha256Passed);
        Assert.True(file.NasTarget.FullRereadSha256Passed);
    }

    [Fact]
    public async Task Crash_after_atomic_rename_uses_persisted_temp_identity_and_reads_source_once()
    {
        FrozenFixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        File.Move(fixture.LocalTemp, fixture.LocalFinal);

        await using FreshTransferExecution execution = await new FrozenTransferFinalizer().FinalizeAsync(
            fixture.ContentManifest,
            fixture.SourceRoot,
            fixture.Plans,
            fixture.Store,
            CancellationToken.None);

        Assert.Equal(fixture.Length, execution.Metrics.SourceContentBytesRead);
        Assert.Equal(0, execution.Metrics.TargetBytesWritten);
        Assert.Equal(0, execution.Metrics.TargetTemporaryBytesRead);
        Assert.Equal(fixture.Length, execution.Metrics.TargetFinalBytesRead);
        Assert.Equal(fixture.SourceSha256, await ComputeSha256Async(fixture.LocalFinal));
        StandaloneTargetFileJournal target = Assert.Single(
            (await fixture.Store.LoadAsync(CancellationToken.None))!.Files).LocalTarget;
        Assert.Equal(StandaloneTargetState.Verified, target.State);
        Assert.True(target.AtomicallyPublished);
        Assert.True(target.FullRereadSha256Passed);
    }

    [Fact]
    public void Temporary_publish_path_requires_final_path_full_reread_before_verified_journal_fact()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AutoCardSync.Standalone.Core",
            "Copying",
            "FrozenTransferFinalizer.cs"));
        int publish = source.IndexOf("published = await _publisher.PublishAsync(", StringComparison.Ordinal);
        int finalReread = source.IndexOf("await published.EnsureContinuousAsync(cancellationToken, finalProgress);", publish, StringComparison.Ordinal);
        int verifiedJournal = source.IndexOf("await journalStore.OnFileVerifiedAsync(", publish, StringComparison.Ordinal);

        Assert.True(publish >= 0);
        Assert.True(finalReread > publish);
        Assert.True(verifiedJournal > finalReread);
    }

    [Fact]
    public async Task Frozen_recovery_rejects_source_content_changed_without_metadata_change()
    {
        FrozenFixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        DateTime originalLastWriteUtc = File.GetLastWriteTimeUtc(fixture.SourcePath);
        byte[] changed = await File.ReadAllBytesAsync(fixture.SourcePath);
        changed[0] ^= 0x5a;
        await File.WriteAllBytesAsync(fixture.SourcePath, changed);
        File.SetLastWriteTimeUtc(fixture.SourcePath, originalLastWriteUtc);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            new FrozenTransferFinalizer().FinalizeAsync(
                fixture.ContentManifest,
                fixture.SourceRoot,
                fixture.Plans,
                fixture.Store,
                CancellationToken.None));

        Assert.Contains("content hash changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.LocalFinal));
    }
    [Fact]
    public async Task Frozen_recovery_rejects_replaced_temporary_object_before_publication()
    {
        FrozenFixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        File.Delete(fixture.LocalTemp);
        await File.WriteAllBytesAsync(fixture.LocalTemp, new byte[fixture.Length]);

        IdentityChangedException exception = await Assert.ThrowsAsync<IdentityChangedException>(() =>
            new FrozenTransferFinalizer().FinalizeAsync(
                fixture.ContentManifest,
                fixture.SourceRoot,
                fixture.Plans,
                fixture.Store,
                CancellationToken.None));

        Assert.Equal("frozen-recovery-temporary-verify", exception.Operation);
        Assert.Contains("identity changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.LocalFinal));
    }

    private async Task<FrozenFixture> CreateFixtureAsync(StandaloneTargetMode mode)
    {
        const int length = 2 * 1024 * 1024 + 17;
        string sourceRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "source");
        string localRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "local");
        string nasRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "nas");
        string relativePath = Path.Combine("DCIM", "clip.bin");
        string sourcePath = Path.Combine(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(nasRoot);
        await WriteDeterministicFileAsync(sourcePath, length);
        string sourceSha256 = await ComputeSha256Async(sourcePath);

        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            taskId,
            sourceRoot,
            "standalone-v1-frozen-finalizer",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".bin"]),
            CancellationToken.None);
        ManifestEntry entry = Assert.Single(inventory.Entries, item => !item.Excluded);
        TaskManifest content = ManifestBuilder.CreateContentManifest(
            inventory,
            new Dictionary<Guid, string> { [entry.Id] = sourceSha256 });

        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        FaultDomainInfo localDomain = resolver.ResolveLocalDomain(localRoot);
        FaultDomainInfo nasDomain = resolver.ResolveLocalDomain(nasRoot);
        Guid localTargetId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        string journalPath = Path.Combine(_root, Guid.NewGuid().ToString("N"), "journal");
        var store = new StandaloneTaskJournalStore(journalPath);
        await store.InitializeAsync(new StandaloneTaskJournal
        {
            SchemaVersion = 2,
            TaskId = taskId,
            SourceIdentity = sourceDomain.StorageIdentity!,
            CardInstanceId = Guid.NewGuid(),
            TargetMode = mode,
            InventoryManifestHash = inventory.ManifestHash,
            ManifestHash = string.Empty,
            ContentManifestFrozen = false,
            LocalTargetIdentity = mode.RequiresLocal() ? localDomain.StorageIdentity! : string.Empty,
            LocalTargetRoot = mode.RequiresLocal() ? localRoot : string.Empty,
            NasTargetIdentity = mode.RequiresNas() ? nasDomain.StorageIdentity! : string.Empty,
            NasTargetRoot = mode.RequiresNas() ? nasRoot : string.Empty,
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = entry.Id,
                    RelativePath = entry.RelativePath,
                    Length = entry.FileSize,
                    SourceSha256 = string.Empty,
                    State = StandaloneFileState.Pending,
                    LocalTarget = NewTarget(localTargetId, "local", localDomain.StorageIdentity!, mode.RequiresLocal()),
                    NasTarget = NewTarget(nasTargetId, "nas", nasDomain.StorageIdentity!, mode.RequiresNas()),
                },
            ],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var plans = new List<FreshTargetPlan>();
        string localTemp = CopyPathConvention.GetTempPath(localRoot, relativePath, taskId, entry.Id, localTargetId);
        string nasTemp = CopyPathConvention.GetTempPath(nasRoot, relativePath, taskId, entry.Id, nasTargetId);
        string localFinal = CopyPathConvention.GetFinalPath(localRoot, relativePath);
        string nasFinal = CopyPathConvention.GetFinalPath(nasRoot, relativePath);
        BlockCheckpoint checkpoint = new(0, 0, length, sourceSha256, DateTimeOffset.UtcNow);

        if (mode.RequiresLocal())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localTemp)!);
            File.Copy(sourcePath, localTemp);
            string identity = GetFileIdentity(localTemp);
            await RecordStagedTargetAsync(store, taskId, entry, localTargetId, localTemp, localFinal, identity, checkpoint);
            plans.Add(NewPlan(localTargetId, "local", localRoot, localDomain));
        }
        if (mode.RequiresNas())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(nasTemp)!);
            File.Copy(sourcePath, nasTemp);
            string identity = GetFileIdentity(nasTemp);
            await RecordStagedTargetAsync(store, taskId, entry, nasTargetId, nasTemp, nasFinal, identity, checkpoint);
            plans.Add(NewPlan(nasTargetId, "nas", nasRoot, nasDomain));
        }

        await store.RecordSourceContentAsync(
            taskId, entry.Id, entry.SourceFileId!, sourceSha256, CancellationToken.None);
        await store.FreezeContentManifestAsync(content.ManifestHash, CancellationToken.None);
        return new FrozenFixture(
            sourceRoot,
            sourcePath,
            content,
            plans,
            store,
            length,
            sourceSha256,
            localTemp,
            localFinal,
            nasTemp,
            nasFinal);
    }

    private static async Task RecordStagedTargetAsync(
        StandaloneTaskJournalStore store,
        Guid taskId,
        ManifestEntry entry,
        Guid targetId,
        string tempPath,
        string finalPath,
        string tempIdentity,
        BlockCheckpoint checkpoint)
    {
        await store.OnFileStartedAsync(new CopyJournalFileStarted(
            taskId,
            entry.Id,
            targetId,
            entry.RelativePath,
            entry.SourceFileId!,
            tempPath,
            finalPath,
            entry.FileSize,
            ExpectedSha256: null,
            TemporaryObjectId: tempIdentity), CancellationToken.None);
        await store.OnCheckpointCompletedAsync(new CopyJournalCheckpointCompleted(
            taskId,
            entry.Id,
            targetId,
            entry.RelativePath,
            tempIdentity,
            checkpoint), CancellationToken.None);
    }

    private static StandaloneTargetFileJournal NewTarget(
        Guid targetId,
        string role,
        string identity,
        bool required) => new()
        {
            TargetId = targetId,
            TargetRole = role,
            TargetIdentity = required ? identity : string.Empty,
            TemporaryPath = string.Empty,
            State = required ? StandaloneTargetState.Pending : StandaloneTargetState.NotRequired,
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

    private static string GetFileIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileIdentity identity = FileIdentity.GetFileIdentity(stream.SafeFileHandle, path);
        return $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";
    }

    private static async Task WriteDeterministicFileAsync(string path, int length)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(index * 31 + (index >> 11)));
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AutoCardSync.Standalone.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record FrozenFixture(
        string SourceRoot,
        string SourcePath,
        TaskManifest ContentManifest,
        IReadOnlyList<FreshTargetPlan> Plans,
        StandaloneTaskJournalStore Store,
        int Length,
        string SourceSha256,
        string LocalTemp,
        string LocalFinal,
        string NasTemp,
        string NasFinal);
}
