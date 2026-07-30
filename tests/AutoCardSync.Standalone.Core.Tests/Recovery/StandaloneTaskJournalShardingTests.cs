using System.Security.Cryptography;
using System.Text.Json;
using AutoCardSync.Application.Copying;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class StandaloneTaskJournalShardingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-JournalShards", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task File_and_checkpoint_updates_rewrite_only_the_selected_file_shard()
    {
        string journalDirectory = Path.Combine(_root, "task-journal");
        Guid taskId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        StandaloneTaskJournal initial = NewJournal(taskId, nasTargetId, fileCount: 100);
        var store = new StandaloneTaskJournalStore(journalDirectory);
        await store.InitializeAsync(initial, CancellationToken.None);

        string filesDirectory = Path.Combine(journalDirectory, "files");
        Dictionary<string, string> before = HashFiles(filesDirectory);
        string headerHash = HashFile(Path.Combine(journalDirectory, "task.json"));
        string manifestHash = HashFile(Path.Combine(journalDirectory, "manifest.json"));
        string stateHash = HashFile(Path.Combine(journalDirectory, "state.json"));
        JournalPersistenceMetrics beforeMetrics = store.PersistenceMetrics;
        StandaloneFileJournal selected = initial.Files[37];
        string selectedPath = Path.Combine(filesDirectory, $"{selected.FileId:N}.json");

        await store.OnFileStartedAsync(new CopyJournalFileStarted(
            taskId,
            selected.FileId,
            nasTargetId,
            selected.RelativePath,
            "source-file-id",
            @"C:\temp\selected.partial",
            @"C:\target\selected.bin",
            selected.Length,
            ExpectedSha256: null,
            TemporaryObjectId: "temp-object-id"), CancellationToken.None);

        Dictionary<string, string> afterStart = HashFiles(filesDirectory);
        string[] changedAfterStart = before.Keys
            .Where(path => !string.Equals(before[path], afterStart[path], StringComparison.Ordinal))
            .ToArray();
        JournalPersistenceMetrics afterStartMetrics = store.PersistenceMetrics;
        Assert.Equal([selectedPath], changedAfterStart);
        Assert.Equal(1, afterStartMetrics.AtomicWrites - beforeMetrics.AtomicWrites);
        Assert.Equal(headerHash, HashFile(Path.Combine(journalDirectory, "task.json")));
        Assert.Equal(manifestHash, HashFile(Path.Combine(journalDirectory, "manifest.json")));
        Assert.Equal(stateHash, HashFile(Path.Combine(journalDirectory, "state.json")));

        var checkpoint = new BlockCheckpoint(
            0,
            0,
            4096,
            new string('a', 64),
            DateTimeOffset.UtcNow);
        await store.OnCheckpointCompletedAsync(new CopyJournalCheckpointCompleted(
            taskId,
            selected.FileId,
            nasTargetId,
            selected.RelativePath,
            "temp-object-id",
            checkpoint), CancellationToken.None);

        Dictionary<string, string> afterCheckpoint = HashFiles(filesDirectory);
        string[] changedAfterCheckpoint = afterStart.Keys
            .Where(path => !string.Equals(afterStart[path], afterCheckpoint[path], StringComparison.Ordinal))
            .ToArray();
        JournalPersistenceMetrics afterCheckpointMetrics = store.PersistenceMetrics;
        Assert.Equal([selectedPath], changedAfterCheckpoint);
        Assert.Equal(1, afterCheckpointMetrics.AtomicWrites - afterStartMetrics.AtomicWrites);

        int fullJournalBytes = JsonSerializer.SerializeToUtf8Bytes(
            initial,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }).Length;
        long checkpointWriteBytes = afterCheckpointMetrics.BytesWritten - afterStartMetrics.BytesWritten;
        Assert.True(checkpointWriteBytes < fullJournalBytes / 10,
            $"Checkpoint shard wrote {checkpointWriteBytes} bytes versus {fullJournalBytes} bytes for the full journal.");

        StandaloneTaskJournal loaded = (await store.LoadAsync(CancellationToken.None))!;
        StandaloneFileJournal loadedSelected = loaded.Files.Single(file => file.FileId == selected.FileId);
        Assert.Equal("temp-object-id", loadedSelected.NasTarget.TemporaryObjectIdentity);
        Assert.Single(loadedSelected.NasTarget.Checkpoints);
        Assert.All(loaded.Files.Where(file => file.FileId != selected.FileId), file =>
        {
            Assert.Equal(StandaloneFileState.Pending, file.State);
            Assert.Empty(file.NasTarget.Checkpoints);
        });
    }

    [Fact]
    public async Task Common_dual_checkpoint_updates_both_targets_in_one_atomic_file_shard_write()
    {
        string journalDirectory = Path.Combine(_root, "common-checkpoint");
        Guid taskId = Guid.NewGuid();
        Guid localTargetId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        var journal = new StandaloneTaskJournal
        {
            SchemaVersion = 2,
            TaskId = taskId,
            SourceIdentity = "source-storage",
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalAndNas,
            InventoryManifestHash = new string('2', 64),
            ManifestHash = string.Empty,
            ContentManifestFrozen = false,
            LocalTargetIdentity = "local-storage",
            LocalTargetRoot = @"D:\\AutoCardSync",
            NasTargetIdentity = "nas-storage",
            NasTargetRoot = @"Z:\\AutoCardSync",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = fileId,
                    RelativePath = @"DCIM\\clip.bin",
                    Length = ChunkedFileCopier.BlockSizeBytes,
                    SourceSha256 = string.Empty,
                    State = StandaloneFileState.Copying,
                    LocalTarget = RequiredTarget(localTargetId, "local", "local-storage", "local-temp"),
                    NasTarget = RequiredTarget(nasTargetId, "nas", "nas-storage", "nas-temp"),
                },
            ],
        };
        var store = new StandaloneTaskJournalStore(journalDirectory);
        await store.InitializeAsync(journal, CancellationToken.None);
        JournalPersistenceMetrics before = store.PersistenceMetrics;
        var checkpoint = new BlockCheckpoint(
            0,
            0,
            ChunkedFileCopier.BlockSizeBytes,
            new string('b', 64),
            DateTimeOffset.UtcNow);

        await store.RecordCommonCheckpointAsync(
            taskId,
            fileId,
            @"DCIM\\clip.bin",
            [
                new CommonCheckpointTargetBinding(localTargetId, "local-temp"),
                new CommonCheckpointTargetBinding(nasTargetId, "nas-temp"),
            ],
            checkpoint,
            CancellationToken.None);

        JournalPersistenceMetrics after = store.PersistenceMetrics;
        Assert.Equal(1, after.AtomicWrites - before.AtomicWrites);
        StandaloneFileJournal file = Assert.Single(
            (await store.LoadAsync(CancellationToken.None))!.Files);
        StandaloneBlockCheckpoint local = Assert.Single(file.LocalTarget.Checkpoints);
        StandaloneBlockCheckpoint nas = Assert.Single(file.NasTarget.Checkpoints);
        Assert.Equal(local.BlockIndex, nas.BlockIndex);
        Assert.Equal(local.Offset, nas.Offset);
        Assert.Equal(local.Length, nas.Length);
        Assert.Equal(local.Sha256, nas.Sha256);
    }

    private static StandaloneTargetFileJournal RequiredTarget(
        Guid targetId,
        string role,
        string identity,
        string temporaryIdentity) => new()
        {
            TargetId = targetId,
            TargetRole = role,
            TargetIdentity = identity,
            TemporaryPath = role == "local" ? @"D:\\temp.partial" : @"Z:\\temp.partial",
            TemporaryObjectIdentity = temporaryIdentity,
            State = StandaloneTargetState.Copying,
        };

    private static StandaloneTaskJournal NewJournal(Guid taskId, Guid nasTargetId, int fileCount) => new()
    {
        SchemaVersion = 2,
        TaskId = taskId,
        SourceIdentity = "source-storage",
        CardInstanceId = Guid.NewGuid(),
        TargetMode = StandaloneTargetMode.NasOnly,
        InventoryManifestHash = new string('1', 64),
        ManifestHash = string.Empty,
        ContentManifestFrozen = false,
        LocalTargetIdentity = string.Empty,
        LocalTargetRoot = string.Empty,
        NasTargetIdentity = "nas-storage",
        NasTargetRoot = @"Z:\AutoCardSync",
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Files = Enumerable.Range(0, fileCount).Select(index => new StandaloneFileJournal
        {
            FileId = Guid.NewGuid(),
            RelativePath = $"DCIM\\file-{index:D4}.bin",
            Length = 1024 + index,
            SourceSha256 = string.Empty,
            State = StandaloneFileState.Pending,
            LocalTarget = new StandaloneTargetFileJournal
            {
                TargetId = Guid.NewGuid(),
                TargetRole = "local",
                TargetIdentity = string.Empty,
                TemporaryPath = string.Empty,
                State = StandaloneTargetState.NotRequired,
            },
            NasTarget = new StandaloneTargetFileJournal
            {
                TargetId = nasTargetId,
                TargetRole = "nas",
                TargetIdentity = "nas-storage",
                TemporaryPath = string.Empty,
                State = StandaloneTargetState.Pending,
            },
        }).ToArray(),
    };

    private static Dictionary<string, string> HashFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
