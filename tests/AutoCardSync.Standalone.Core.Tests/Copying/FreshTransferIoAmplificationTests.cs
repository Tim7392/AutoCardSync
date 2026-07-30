using System.Security.Cryptography;
using System.Text.Json;
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
using Xunit.Abstractions;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class FreshTransferIoAmplificationTests(ITestOutputHelper output) : IDisposable
{
    private const long PayloadBytes = 130L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-Fresh4X", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Three_target_modes_meet_fresh_io_gates_without_batch_rehash()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string relativePath = Path.Combine("DCIM", "100MEDIA", "fresh-130mb.bin");
        string sourcePath = Path.Combine(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await WriteDeterministicFileAsync(sourcePath, PayloadBytes);

        var reports = new List<object>();
        foreach (StandaloneTargetMode mode in Enum.GetValues<StandaloneTargetMode>())
        {
            ModeRunResult run = await RunFreshModeAsync(sourceRoot, relativePath, mode);
            FreshTransferIoMetrics metrics = run.Metrics;
            int selectedTargets = mode == StandaloneTargetMode.LocalAndNas ? 2 : 1;

            Assert.Equal(PayloadBytes, metrics.PayloadBytes);
            Assert.Equal(PayloadBytes, metrics.SourceContentBytesRead);
            Assert.Equal(PayloadBytes * selectedTargets, metrics.TargetBytesWritten);
            Assert.Equal(PayloadBytes * selectedTargets, metrics.TargetTemporaryBytesRead);
            Assert.Equal(PayloadBytes * selectedTargets, metrics.TargetFinalBytesRead);
            Assert.Equal(0, metrics.RecoveryPrefixBytesRead);
            Assert.InRange(
                metrics.PeakReadAheadBeyondDurableCheckpointBytes,
                0,
                ChunkedFileCopier.MaxReadAheadBytes);
            Assert.Equal(0, metrics.EndOfTaskBatchFullRehashBytes);
            Assert.Equal(selectedTargets == 1 ? 4d : 7d, metrics.FreshIoAmplification, 8);
            Assert.True(metrics.FreshIoAmplification <= (selectedTargets == 1 ? 4.05 : 7.05));
            Assert.Equal(selectedTargets, run.FinalHashes.Count);
            Assert.Single(run.FinalHashes.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.True(run.SafeToRemoveCard);
            Assert.False(run.UnrelatedNasFilesRead);
            foreach ((string _, IReadOnlyList<TargetCopyStatus> values) in run.TargetProgress)
            {
                Assert.Contains(values, value =>
                    value.Phase == CopyPhase.TemporaryVerifying &&
                    value.TemporaryBytesVerified is > 0 and < PayloadBytes);
                Assert.Contains(values, value =>
                    value.Phase == CopyPhase.FinalVerifying &&
                    value.FinalBytesVerified is > 0 and < PayloadBytes);
                TargetCopyStatus completed = Assert.Single(values, value => value.Phase == CopyPhase.Completed);
                Assert.Equal(PayloadBytes, completed.TemporaryBytesVerified);
                Assert.Equal(PayloadBytes, completed.FinalBytesVerified);
                AssertMonotonic(values.Select(value => value.TemporaryBytesVerified));
                AssertMonotonic(values.Select(value => value.FinalBytesVerified));
            }

            reports.Add(new
            {
                mode = mode.ToString(),
                payloadBytes = metrics.PayloadBytes,
                sourceBytesRead = metrics.SourceContentBytesRead,
                targetBytesWritten = metrics.TargetBytesWritten,
                targetTemporaryBytesRead = metrics.TargetTemporaryBytesRead,
                targetFinalBytesRead = metrics.TargetFinalBytesRead,
                recoveryPrefixBytesRead = metrics.RecoveryPrefixBytesRead,
                peakReadAheadBeyondDurableCheckpointBytes = metrics.PeakReadAheadBeyondDurableCheckpointBytes,
                endOfTaskBatchFullRehashBytes = metrics.EndOfTaskBatchFullRehashBytes,
                freshIoBytes = metrics.FreshPayloadIoBytes,
                amplification = metrics.FreshIoAmplification,
                journalBytesWritten = run.JournalMetrics.BytesWritten,
                journalAtomicWrites = run.JournalMetrics.AtomicWrites,
                peakIdentityLeases = metrics.PeakIdentityLeaseCount,
                peakProcessHandles = metrics.PeakProcessHandleCount,
                peakManagedMemoryBytes = metrics.PeakManagedMemoryBytes,
                peakProcessWorkingSetBytes = metrics.PeakProcessWorkingSetBytes,
                stageMilliseconds = metrics.StageElapsed.ToDictionary(
                    pair => pair.Key,
                    pair => Math.Round(pair.Value.TotalMilliseconds, 3)),
                finalSha256 = run.FinalHashes,
                safeToRemoveCard = run.SafeToRemoveCard,
                unrelatedNasFilesRead = run.UnrelatedNasFilesRead,
            });
        }

        output.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task Missing_later_source_prevents_any_final_publish_before_content_manifest_freezes()
    {
        string sourceRoot = Path.Combine(_root, "freeze-source");
        string targetRoot = Path.Combine(_root, "freeze-target");
        string firstRelative = Path.Combine("DCIM", "first.bin");
        string secondRelative = Path.Combine("DCIM", "second.bin");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, firstRelative), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, secondRelative), new byte[4096]);

        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await BuildInventoryAsync(taskId, sourceRoot);
        File.Delete(Path.Combine(sourceRoot, secondRelative));
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        FaultDomainInfo targetDomain = resolver.ResolveLocalDomain(targetRoot);
        Guid localTargetId = Guid.NewGuid();
        var store = new StandaloneTaskJournalStore(Path.Combine(_root, "freeze-journal"));
        await store.InitializeAsync(NewJournal(
            inventory,
            sourceDomain.StorageIdentity!,
            StandaloneTargetMode.LocalOnly,
            targetRoot,
            targetDomain.StorageIdentity!,
            localTargetId,
            string.Empty,
            string.Empty,
            Guid.NewGuid()), CancellationToken.None);

        await Assert.ThrowsAnyAsync<IOException>(() => new FreshTransferCoordinator().ExecuteAsync(
            inventory,
            sourceRoot,
            [NewPlan(localTargetId, "local", targetRoot, targetDomain)],
            store,
            resumeExistingTemps: false,
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(targetRoot, firstRelative)));
        Assert.False(File.Exists(Path.Combine(targetRoot, secondRelative)));
        StandaloneTaskJournal journal = (await store.LoadAsync(CancellationToken.None))!;
        Assert.False(journal.ContentManifestFrozen);
        Assert.Equal(string.Empty, journal.ManifestHash);
    }

    [Fact]
    public async Task Target_mode_binding_is_rejected_before_an_unselected_target_path_is_touched()
    {
        string sourceRoot = Path.Combine(_root, "binding-source");
        string relativePath = Path.Combine("DCIM", "binding.bin");
        string sourcePath = Path.Combine(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);

        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await BuildInventoryAsync(taskId, sourceRoot);
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        string nasRoot = Path.Combine(_root, "binding-nas");
        Directory.CreateDirectory(nasRoot);
        FaultDomainInfo nasDomain = resolver.ResolveLocalDomain(nasRoot);
        Guid nasTargetId = Guid.NewGuid();
        var store = new StandaloneTaskJournalStore(Path.Combine(_root, "binding-journal"));
        await store.InitializeAsync(NewJournal(
            inventory,
            sourceDomain.StorageIdentity!,
            StandaloneTargetMode.NasOnly,
            string.Empty,
            string.Empty,
            Guid.NewGuid(),
            nasRoot,
            nasDomain.StorageIdentity!,
            nasTargetId), CancellationToken.None);

        string forbiddenLocalRoot = Path.Combine(_root, "must-not-be-created");
        var mismatchedPlan = new FreshTargetPlan(
            Guid.NewGuid(),
            "local",
            forbiddenLocalRoot,
            "local:invalid",
            "local-v1:invalid",
            NasIdentity: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => new FreshTransferCoordinator().ExecuteAsync(
            inventory,
            sourceRoot,
            [mismatchedPlan],
            store,
            resumeExistingTemps: false,
            CancellationToken.None));

        Assert.False(Directory.Exists(forbiddenLocalRoot));
    }

    private async Task<ModeRunResult> RunFreshModeAsync(
        string sourceRoot,
        string relativePath,
        StandaloneTargetMode mode)
    {
        Guid taskId = Guid.NewGuid();
        TaskManifest inventory = await BuildInventoryAsync(taskId, sourceRoot);
        ManifestEntry included = Assert.Single(inventory.Entries, entry => !entry.Excluded);
        Assert.Null(included.SourceHash);

        string modeRoot = Path.Combine(_root, mode.ToString());
        string localRoot = Path.Combine(modeRoot, "local");
        string nasRoot = Path.Combine(modeRoot, "nas");
        if (mode.RequiresLocal())
            Directory.CreateDirectory(localRoot);
        if (mode.RequiresNas())
            Directory.CreateDirectory(nasRoot);

        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        FaultDomainInfo? localDomain = mode.RequiresLocal() ? resolver.ResolveLocalDomain(localRoot) : null;
        FaultDomainInfo? nasDomain = mode.RequiresNas() ? resolver.ResolveLocalDomain(nasRoot) : null;
        Guid localTargetId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        var store = new StandaloneTaskJournalStore(Path.Combine(modeRoot, "journal"));
        await store.InitializeAsync(NewJournal(
            inventory,
            sourceDomain.StorageIdentity!,
            mode,
            localRoot,
            localDomain?.StorageIdentity ?? string.Empty,
            localTargetId,
            nasRoot,
            nasDomain?.StorageIdentity ?? string.Empty,
            nasTargetId), CancellationToken.None);

        var plans = new List<FreshTargetPlan>(2);
        var targetProgress = new Dictionary<string, List<TargetCopyStatus>>(StringComparer.Ordinal);
        if (mode.RequiresLocal())
        {
            targetProgress["local"] = [];
            plans.Add(NewPlan(
                localTargetId, "local", localRoot, localDomain!,
                new InlineProgress<TargetCopyStatus>(targetProgress["local"].Add)));
        }
        if (mode.RequiresNas())
        {
            targetProgress["nas"] = [];
            plans.Add(NewPlan(
                nasTargetId, "nas", nasRoot, nasDomain!,
                new InlineProgress<TargetCopyStatus>(targetProgress["nas"].Add)));
        }

        FileStream? unrelatedNasLease = null;
        string? unrelatedNasPath = null;
        if (mode.RequiresNas())
        {
            unrelatedNasPath = Path.Combine(nasRoot, "unrelated-existing-object.keep");
            await File.WriteAllTextAsync(unrelatedNasPath, "must not be read");
            unrelatedNasLease = new FileStream(
                unrelatedNasPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }

        var coordinator = new FreshTransferCoordinator();
        FreshTransferExecution execution;
        try
        {
            execution = await coordinator.ExecuteAsync(
                inventory,
                sourceRoot,
                plans,
                store,
                resumeExistingTemps: false,
                CancellationToken.None);
        }
        finally
        {
            unrelatedNasLease?.Dispose();
        }

        if (unrelatedNasPath is not null)
            Assert.Equal("must not be read", await File.ReadAllTextAsync(unrelatedNasPath));
        await using (execution)
        {
            FreshTransferIoMetrics beforeContinuity = execution.Metrics;
            execution.RevalidateContinuity();
            Assert.Same(beforeContinuity, execution.Metrics);
            Assert.Equal(0, execution.Metrics.EndOfTaskBatchFullRehashBytes);

            StandaloneTaskJournal beforeReceipt = (await store.LoadAsync(CancellationToken.None))!;
            StandaloneSafetyResult notYetSafe = StandaloneSafetyDecision.Evaluate(BuildSafety(
                beforeReceipt,
                completionReceiptPersisted: false));
            Assert.False(notYetSafe.SafeToRemoveCard);
            Assert.Contains("LOCAL_COMPLETION_RECEIPT_PERSISTED", notYetSafe.UnmetConditions);

            await store.MarkCompletionReceiptPersistedAsync(CancellationToken.None);
            execution.RevalidateContinuity();
            StandaloneTaskJournal completed = (await store.LoadAsync(CancellationToken.None))!;
            StandaloneSafetyResult safe = StandaloneSafetyDecision.Evaluate(BuildSafety(
                completed,
                completionReceiptPersisted: true));

            Assert.True(completed.ContentManifestFrozen);
            Assert.Equal(execution.ContentManifest.ManifestHash, completed.ManifestHash);
            Assert.All(completed.Files, file => Assert.Equal(StandaloneFileState.Verified, file.State));
            Assert.All(completed.Files, file =>
            {
                Assert.Equal(
                    mode.RequiresLocal() ? StandaloneTargetState.Verified : StandaloneTargetState.NotRequired,
                    file.LocalTarget.State);
                Assert.Equal(
                    mode.RequiresNas() ? StandaloneTargetState.Verified : StandaloneTargetState.NotRequired,
                    file.NasTarget.State);
            });

            var hashes = new List<string>();
            if (mode.RequiresLocal())
            {
                string final = Path.Combine(localRoot, relativePath);
                Assert.True(File.Exists(final));
                Assert.False(File.Exists(CopyPathConvention.GetTempPath(
                    localRoot, relativePath, taskId, included.Id, localTargetId)));
                hashes.Add(await ComputeSha256Async(final));
            }
            else
            {
                Assert.False(Directory.Exists(localRoot));
            }
            if (mode.RequiresNas())
            {
                string final = Path.Combine(nasRoot, relativePath);
                Assert.True(File.Exists(final));
                Assert.False(File.Exists(CopyPathConvention.GetTempPath(
                    nasRoot, relativePath, taskId, included.Id, nasTargetId)));
                hashes.Add(await ComputeSha256Async(final));
            }
            else
            {
                Assert.False(Directory.Exists(nasRoot));
            }

            string manifestHash = execution.ContentManifest.Entries.Single(entry => !entry.Excluded).SourceHash!;
            Assert.All(hashes, hash => Assert.Equal(manifestHash, hash, ignoreCase: true));
            return new ModeRunResult(
                execution.Metrics,
                store.PersistenceMetrics,
                hashes,
                safe.SafeToRemoveCard,
                UnrelatedNasFilesRead: false,
                targetProgress.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<TargetCopyStatus>)pair.Value,
                    StringComparer.Ordinal));
        }
    }

    private static async Task<TaskManifest> BuildInventoryAsync(Guid taskId, string sourceRoot) =>
        await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            taskId,
            sourceRoot,
            "standalone-v1-fresh-4x",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".bin"]),
            CancellationToken.None);

    private static StandaloneTaskJournal NewJournal(
        TaskManifest inventory,
        string sourceIdentity,
        StandaloneTargetMode mode,
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
            TargetMode = mode,
            InventoryManifestHash = inventory.ManifestHash,
            ManifestHash = string.Empty,
            ContentManifestFrozen = false,
            LocalTargetIdentity = mode.RequiresLocal() ? localIdentity : string.Empty,
            LocalTargetRoot = mode.RequiresLocal() ? localRoot : string.Empty,
            NasTargetIdentity = mode.RequiresNas() ? nasIdentity : string.Empty,
            NasTargetRoot = mode.RequiresNas() ? nasRoot : string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = inventory.Entries.Where(entry => !entry.Excluded).Select(entry => new StandaloneFileJournal
            {
                FileId = entry.Id,
                RelativePath = entry.RelativePath,
                Length = entry.FileSize,
                SourceSha256 = string.Empty,
                State = StandaloneFileState.Pending,
                LocalTarget = NewTarget(localTargetId, "local", localIdentity, mode.RequiresLocal()),
                NasTarget = NewTarget(nasTargetId, "nas", nasIdentity, mode.RequiresNas()),
            }).ToArray(),
        };

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
        FaultDomainInfo domain,
        IProgress<TargetCopyStatus>? progress = null) => new(
            targetId,
            role,
            root,
            domain.FaultDomainId,
            domain.StorageIdentity!,
            NasIdentity: null,
            Progress: progress);

    private static StandaloneSafetyFacts BuildSafety(
        StandaloneTaskJournal journal,
        bool completionReceiptPersisted) => new()
        {
            TargetMode = journal.TargetMode,
            ManifestFrozen = journal.ContentManifestFrozen,
            SourceReadOnly = true,
            AllIncludedFilesAccountedFor = journal.Files.Count > 0 &&
                journal.Files.All(file => file.State == StandaloneFileState.Verified),
            LocalTargetFullRereadSha256Passed = !journal.TargetMode.RequiresLocal() ||
                journal.Files.All(file => file.LocalTarget.FullRereadSha256Passed),
            NasTargetFullRereadSha256Passed = !journal.TargetMode.RequiresNas() ||
                journal.Files.All(file => file.NasTarget.FullRereadSha256Passed),
            FinalObjectsSafelyPublishedOrReused = journal.Files.All(file =>
                (!journal.TargetMode.RequiresLocal() || file.LocalTarget.AtomicallyPublished || file.LocalTarget.ReusedExisting) &&
                (!journal.TargetMode.RequiresNas() || file.NasTarget.AtomicallyPublished || file.NasTarget.ReusedExisting)),
            SourceIdentityUnchanged = true,
            TargetIdentitiesUnchanged = true,
            FailedIncludedFiles = journal.Files.Count(file => file.State == StandaloneFileState.Failed),
            PendingIncludedFiles = journal.Files.Count(file => file.State != StandaloneFileState.Verified),
            LocalCompletionReceiptPersisted = completionReceiptPersisted && journal.LocalCompletionReceiptPersisted,
        };

    private static async Task WriteDeterministicFileAsync(string path, long length)
    {
        byte[] buffer = new byte[1024 * 1024];
        long written = 0;
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (written < length)
        {
            int count = (int)Math.Min(buffer.Length, length - written);
            for (int index = 0; index < count; index++)
                buffer[index] = unchecked((byte)((written + index) * 31 + ((written + index) >> 11)));
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
        await stream.FlushAsync();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static void AssertMonotonic(IEnumerable<long> values)
    {
        long previous = 0;
        foreach (long value in values)
        {
            Assert.True(value >= previous);
            previous = value;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record ModeRunResult(
        FreshTransferIoMetrics Metrics,
        JournalPersistenceMetrics JournalMetrics,
        IReadOnlyList<string> FinalHashes,
        bool SafeToRemoveCard,
        bool UnrelatedNasFilesRead,
        IReadOnlyDictionary<string, IReadOnlyList<TargetCopyStatus>> TargetProgress);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
