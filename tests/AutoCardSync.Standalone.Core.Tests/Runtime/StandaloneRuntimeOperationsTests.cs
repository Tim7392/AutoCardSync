using System.Reflection;
using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Application.Cards;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Cards;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoCardSync.Standalone.Core.Tests.Runtime;

public sealed class StandaloneRuntimeOperationsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"AutoCardSync-RuntimeOperations-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("exFAT", true)]
    [InlineData("FAT32", true)]
    [InlineData("NTFS", true)]
    [InlineData("", true)]
    public void File_identity_change_requires_transfer_without_content_proof(
        string fileSystem,
        bool expected)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "IdentityChangesRequireTransfer", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing file-identity policy method.");

        Assert.Equal(expected, Assert.IsType<bool>(method.Invoke(null, [fileSystem])));
    }

    [Fact]
    public void Content_witness_invalidation_forces_the_distributed_sample_into_the_transfer_delta()
    {
        var inventory = new TaskManifest(Guid.NewGuid());
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        foreach (string path in new[] { "A.mov", "B.mov", "C.mov", "D.mov", "E.mov" })
        {
            inventory.AddEntry(new ManifestEntry
            {
                RelativePath = path,
                FileSize = 1,
                LastModifiedUtc = timestamp,
                SourceFileId = path,
                SourceFileIdType = "test",
            });
            timestamp = timestamp.AddSeconds(1);
        }
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "IncludeContentWitnessTransferEntries", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing content-witness transfer policy.");

        IReadOnlyList<ManifestEntry> entries = Assert.IsAssignableFrom<IReadOnlyList<ManifestEntry>>(
            method.Invoke(null, [inventory, new[] { inventory.Entries[2] }, true, false]));
        IReadOnlyList<ManifestEntry> unchanged = Assert.IsAssignableFrom<IReadOnlyList<ManifestEntry>>(
            method.Invoke(null, [inventory, Array.Empty<ManifestEntry>(), false, false]));
        IReadOnlyList<ManifestEntry> fullReverification = Assert.IsAssignableFrom<IReadOnlyList<ManifestEntry>>(
            method.Invoke(null, [inventory, Array.Empty<ManifestEntry>(), false, true]));

        Assert.Equal(["A.mov", "C.mov", "E.mov"], entries.Select(entry => entry.RelativePath));
        Assert.Empty(unchanged);
        Assert.Equal(["A.mov", "B.mov", "C.mov", "D.mov", "E.mov"],
            fullReverification.Select(entry => entry.RelativePath));
    }
    [Fact]
    public async Task Refresh_without_mounted_volume_reports_that_no_reevaluation_was_performed()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await store.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = [@"XDROOT\Clip"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);

        StandaloneRuntimeOperationResult result = await runtime.RefreshAsync(CancellationToken.None);

        Assert.Contains("没有已挂载的素材卡", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_change_is_rejected_while_copy_or_verification_is_active()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(runtime, "_activeTask", active.Task);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            runtime.EnsureConfigurationChangeAllowed);

        Assert.Contains("任务仍在复制或校验", exception.Message, StringComparison.Ordinal);
        active.SetResult();
        runtime.EnsureConfigurationChangeAllowed();
    }

    [Fact]
    public async Task Configuration_change_is_rejected_while_card_state_repair_is_active()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        SetPrivateField(runtime, "_stateRepairInProgress", true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            runtime.BeginConfigurationChange);

        Assert.Contains("恢复操作仍在进行", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_change_lease_queues_a_card_arrival_until_the_save_finishes()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(_root, "queued-during-save", "NTFS", 1, DateTimeOffset.UtcNow);
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");
        IDisposable lease = runtime.BeginConfigurationChange();
        try
        {
            Assert.Null(start.Invoke(runtime, [volume, false]));
            Assert.Null(GetPrivateField(runtime, "_activeTask"));
            Assert.Single(Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task Late_cancel_after_terminal_state_does_not_revoke_safe_result()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var completed = new { view = "complete", safeToRemoveCard = true };
        SetPrivateField(runtime, "_status", completed);
        SetPrivateField(runtime, "_activeTask", Task.CompletedTask);

        object response = await runtime.CancelAsync(expectedOperationId: null);

        Assert.Same(completed, runtime.GetStatusSnapshot());
        Assert.Same(completed, GetProperty<object>(response, "status"));
        Assert.Contains("已经结束", GetProperty<string>(response, "message"), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Forced_reevaluation_bypasses_safe_volume_suppression()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            _root,
            "volume-guid",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000001",
            MountContinuityProven: true);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(method.Invoke(runtime, [volume, false]));
        Task forced = Assert.IsAssignableFrom<Task>(method.Invoke(runtime, [volume, true]));
        await forced;

        Assert.DoesNotContain(GetVolumeKey(volume), GetSafeCompletedVolumes(runtime));
    }

    [Fact]
    public async Task Safe_mounted_volume_restarts_only_after_metadata_delta_appears()
    {
        string sourceRoot = Path.Combine(_root, "source-card");
        string approvedRoot = Path.Combine(sourceRoot, "XDROOT", "Clip");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = [@"XDROOT\Clip"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await store.SaveAsync(configuration, CancellationToken.None);

        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "same-card-poll-test",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        Guid cardId = Guid.NewGuid();
        await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile).SaveInitialAsync(
            cardId,
            sourceDomain.StorageIdentity!,
            selectionPolicyHash,
            inventory,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, inventory, "NTFS", 1);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            sourceRoot,
            "same-card-volume",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000001",
            MountContinuityProven: true);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ShouldReevaluateSafeVolumeAsync", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing mounted-card delta method.");

        bool unchanged = await Assert.IsAssignableFrom<Task<bool>>(
            method.Invoke(runtime, [volume, configuration, CancellationToken.None]));
        Assert.False(unchanged);

        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "new.mov"), new byte[2048]);
        bool added = await Assert.IsAssignableFrom<Task<bool>>(
            method.Invoke(runtime, [volume, configuration, CancellationToken.None]));
        Assert.True(added);
    }
    [Fact]
    public async Task Safe_completion_cache_is_not_reused_without_proven_mount_continuity()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var weakVolume = new VolumeEventArgs(
            _root,
            "volume-guid",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:weak",
            MountContinuityProven: false);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(weakVolume));
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Task reevaluation = Assert.IsAssignableFrom<Task>(method.Invoke(runtime, [weakVolume, false]));
        await reevaluation;

        Assert.DoesNotContain(GetVolumeKey(weakVolume), GetSafeCompletedVolumes(runtime));
    }

    [Fact]
    public async Task Safe_mounted_volume_rechecks_sample_content_when_metadata_is_unchanged()
    {
        string sourceRoot = Path.Combine(_root, "source-card-content-witness");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        string sourcePath = Path.Combine(approvedRoot, "existing.mov");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x11, 1024).ToArray());
        DateTime originalLastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);

        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target-content-witness"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await store.SaveAsync(configuration, CancellationToken.None);

        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "same-card-content-witness-test",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        Guid cardId = Guid.NewGuid();
        await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile).SaveInitialAsync(
            cardId,
            sourceDomain.StorageIdentity!,
            selectionPolicyHash,
            inventory,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, inventory, "NTFS", 1);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            sourceRoot,
            "same-card-content-witness-volume",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000001",
            MountContinuityProven: true);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));

        await File.WriteAllBytesAsync(sourcePath, Enumerable.Repeat((byte)0x22, 1024).ToArray());
        File.SetLastWriteTimeUtc(sourcePath, originalLastWriteUtc);
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ShouldReevaluateSafeVolumeAsync", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing mounted-card delta method.");

        bool shouldReevaluate = await Assert.IsAssignableFrom<Task<bool>>(
            method.Invoke(runtime, [volume, configuration, CancellationToken.None]));

        Assert.True(shouldReevaluate);
        Assert.Contains(GetVolumeKey(volume), GetContentWitnessInvalidatedVolumes(runtime));

    }
    [Fact]
    public async Task Safe_mounted_volume_rechecks_middle_region_of_a_distributed_content_sample()
    {
        string sourceRoot = Path.Combine(_root, "source-card-distributed-witness");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        string[] sourcePaths = [
            Path.Combine(approvedRoot, "A.mov"),
            Path.Combine(approvedRoot, "B.mov"),
            Path.Combine(approvedRoot, "C.mov"),
        ];
        DateTime originalLastWriteUtc = new(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        foreach (string sourcePath in sourcePaths)
        {
            await File.WriteAllBytesAsync(sourcePath, new byte[256 * 1024]);
            File.SetLastWriteTimeUtc(sourcePath, originalLastWriteUtc);
        }

        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target-distributed-witness"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await store.SaveAsync(configuration, CancellationToken.None);

        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "distributed-content-witness-test",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        Guid cardId = Guid.NewGuid();
        await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile).SaveInitialAsync(
            cardId,
            sourceDomain.StorageIdentity!,
            selectionPolicyHash,
            inventory,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, inventory, "NTFS", 1);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            sourceRoot,
            "distributed-content-witness-volume",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000001",
            MountContinuityProven: true);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));

        await using (var stream = new FileStream(
            sourcePaths[1], FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Position = 128 * 1024;
            await stream.WriteAsync(Enumerable.Repeat((byte)0x7f, 64 * 1024).ToArray());
            await stream.FlushAsync();
        }
        File.SetLastWriteTimeUtc(sourcePaths[1], originalLastWriteUtc);
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ShouldReevaluateSafeVolumeAsync", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing mounted-card delta method.");

        bool shouldReevaluate = await Assert.IsAssignableFrom<Task<bool>>(
            method.Invoke(runtime, [volume, configuration, CancellationToken.None]));

        Assert.True(shouldReevaluate);
    }
    [Fact]
    public async Task Ambiguous_same_endpoint_baselines_force_reevaluation_instead_of_blocking_mounted_polling()
    {
        string sourceRoot = Path.Combine(_root, "shared-endpoint-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await store.SaveAsync(configuration, CancellationToken.None);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "shared-endpoint-poll-test",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            Guid.NewGuid(), sourceDomain.StorageIdentity!, selectionPolicyHash, inventory, CancellationToken.None);
        await baselineStore.SaveInitialAsync(
            Guid.NewGuid(), sourceDomain.StorageIdentity!, selectionPolicyHash, inventory, CancellationToken.None);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            sourceRoot, "shared-endpoint-volume", "NTFS", 1, DateTimeOffset.UtcNow);
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ShouldReevaluateSafeVolumeAsync", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing mounted-card delta method.");

        bool shouldReevaluate = await Assert.IsAssignableFrom<Task<bool>>(
            method.Invoke(runtime, [volume, configuration, CancellationToken.None]));

        Assert.True(shouldReevaluate);
    }

    [Fact]
    public async Task Distinct_cards_are_queued_once_while_another_card_is_active()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "first"), "first-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var second = new VolumeEventArgs(Path.Combine(_root, "second"), "second-card", "NTFS", 2, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(start.Invoke(runtime, [second, false]));
        Assert.Null(start.Invoke(runtime, [second, false]));
        var pending = Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes"));
        Assert.Single(pending.Cast<object>());

        MethodInfo removed = typeof(StandaloneRuntimeService).GetMethod(
            "OnVolumeRemoved", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing volume removal method.");
        removed.Invoke(runtime, [runtime, second]);
        Assert.Empty(pending.Cast<object>());
    }
    [Fact]
    public async Task Second_card_arrival_does_not_replace_the_current_card_recovery_context()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "first"), "first-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var second = new VolumeEventArgs(Path.Combine(_root, "second"), "second-card", "NTFS", 2, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        SetPrivateField(runtime, "_lastArrivedVolume", first);
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(start.Invoke(runtime, [second, false]));

        Assert.Same(first, GetPrivateField(runtime, "_lastArrivedVolume"));
        object queued = Assert.Single(Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());
        Assert.Same(second, queued.GetType().GetProperty("Volume")!.GetValue(queued));
    }

    [Fact]
    public async Task Failed_current_card_keeps_its_recovery_screen_until_removed_before_next_card_starts()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "failed"), "failed-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var second = new VolumeEventArgs(Path.Combine(_root, "queued"), "queued-card", "NTFS", 2, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_activeTask", Task.CompletedTask);
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        SetPrivateField(runtime, "_lastArrivedVolume", first);
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");
        MethodInfo finishFailed = typeof(StandaloneRuntimeService).GetMethod(
            "CompleteFailedCurrentVolume", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing failed-card completion method.");
        MethodInfo removed = typeof(StandaloneRuntimeService).GetMethod(
            "OnVolumeRemoved", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing volume removal method.");
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        Assert.Null(start.Invoke(runtime, [second, false]));
        SetPrivateField(runtime, "_activeTask", Task.CompletedTask);

        finishFailed.Invoke(runtime, [GetVolumeKey(first)]);

        Assert.True(Assert.IsType<bool>(GetPrivateField(runtime, "_currentVolumeBlocked")));
        Assert.Same(first, GetPrivateField(runtime, "_lastArrivedVolume"));
        Assert.Single(Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());

        removed.Invoke(runtime, [runtime, first]);

        Assert.NotEqual(GetVolumeKey(first), GetPrivateField(runtime, "_activeVolumeKey"));
        Assert.Same(second, GetPrivateField(runtime, "_lastArrivedVolume"));
    }

    [Fact]
    public async Task Reused_volume_guid_with_different_capacity_is_a_distinct_queued_card()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "first"), "shared-reader-guid", "exFAT", 1, DateTimeOffset.UtcNow);
        var replacement = new VolumeEventArgs(Path.Combine(_root, "replacement"), "shared-reader-guid", "exFAT", 2, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(start.Invoke(runtime, [replacement, false]));
        object pending = Assert.Single(Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());
        VolumeEventArgs queued = Assert.IsType<VolumeEventArgs>(
            pending.GetType().GetProperty("Volume")!.GetValue(pending));
        Assert.Equal(replacement.Capacity, queued.Capacity);

        MethodInfo removed = typeof(StandaloneRuntimeService).GetMethod(
            "OnVolumeRemoved", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing volume removal method.");
        removed.Invoke(runtime, [runtime, first]);

        Assert.Single(Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());
    }
    [Fact]
    public async Task Reused_volume_snapshot_with_changed_mount_identity_is_a_distinct_queued_card()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(
            Path.Combine(_root, "first"), "shared-reader-guid", "exFAT", 1,
            DateTimeOffset.UtcNow, "root:M00000001", MountContinuityProven: true);
        var replacement = first with { MountIdentity = "root:M00000002" };
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(start.Invoke(runtime, [replacement, false]));

        object pending = Assert.Single(Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>());
        VolumeEventArgs queued = Assert.IsType<VolumeEventArgs>(
            pending.GetType().GetProperty("Volume")!.GetValue(pending));
        Assert.Equal(replacement.MountIdentity, queued.MountIdentity);
    }

    [Fact]
    public async Task Starting_a_new_card_publishes_non_safe_status_before_async_setup()
    {
        string sourceRoot = Path.Combine(_root, "handoff-card");
        Directory.CreateDirectory(sourceRoot);
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        SetPrivateField(runtime, "_status", new { view = "complete", safeToRemoveCard = true });
        object? firstStatus = null;
        runtime.StatusChanged += (_, status) => firstStatus ??= status;
        var volume = new VolumeEventArgs(
            sourceRoot, "handoff-volume", "exFAT", 1, DateTimeOffset.UtcNow,
            "root:M00000001", MountContinuityProven: true);
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "StartTransferNoLock", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        _ = Assert.IsAssignableFrom<Task>(start.Invoke(runtime, [volume, false]));

        Assert.NotNull(firstStatus);
        Assert.Equal("copying", GetProperty<string>(firstStatus!, "view"));
        Assert.False(GetProperty<bool>(firstStatus!, "safeToRemoveCard"));
    }

    [Fact]
    public async Task Corrupt_identity_map_exposes_explicit_reinitialize_recovery()
    {
        string sourceRoot = Path.Combine(_root, "corrupt-identity-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "DCIM", "clip.mov"),
            new byte[1024]);
        string targetRoot = Path.Combine(_root, "corrupt-identity-target");
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await store.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CardIdentityFile)!);
        await File.WriteAllTextAsync(paths.CardIdentityFile, "{");
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "corrupt-identity-volume", "NTFS", 1, DateTimeOffset.UtcNow,
            "root:M00000001", MountContinuityProven: true));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(result.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("身份记录已损坏", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(targetRoot));
    }
    [Fact]
    public async Task Already_safe_card_is_not_requeued_while_another_card_is_active()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var active = new VolumeEventArgs(Path.Combine(_root, "active"), "active-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var safe = new VolumeEventArgs(
            Path.Combine(_root, "safe"),
            "safe-card",
            "NTFS",
            2,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000002",
            MountContinuityProven: true);
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(active));
        GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(safe));
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Assert.Null(start.Invoke(runtime, [safe, false]));

        var pending = Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes"));
        Assert.Empty(pending.Cast<object>());
    }

    [Fact]
    public async Task Removing_one_safe_card_keeps_other_cards_independently_safe()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "first"), "first-safe-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var second = new VolumeEventArgs(Path.Combine(_root, "second"), "second-safe-card", "NTFS", 2, DateTimeOffset.UtcNow);
        HashSet<string> safe = GetSafeCompletedVolumes(runtime);
        safe.Add(GetVolumeKey(first));
        safe.Add(GetVolumeKey(second));
        MethodInfo removed = typeof(StandaloneRuntimeService).GetMethod(
            "OnVolumeRemoved", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing volume removal method.");

        removed.Invoke(runtime, [runtime, first]);

        Assert.DoesNotContain(GetVolumeKey(first), safe);
        Assert.Contains(GetVolumeKey(second), safe);
    }

    [Fact]
    public async Task Active_card_removal_publishes_failure_before_cancellation_can_publish_next_operation()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var removedVolume = new VolumeEventArgs(
            Path.Combine(_root, "removed-active"), "removed-active", "NTFS", 1, DateTimeOffset.UtcNow);
        var cancellation = new CancellationTokenSource();
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(removedVolume));
        SetPrivateField(runtime, "_activeOperationId", Guid.NewGuid());
        SetPrivateField(runtime, "_taskCts", cancellation);
        SetPrivateField(runtime, "_lastArrivedVolume", removedVolume);
        var events = new List<string>();
        runtime.StatusChanged += (_, status) => events.Add(GetProperty<string>(status, "view")!);
        MethodInfo setStatus = typeof(StandaloneRuntimeService).GetMethod(
            "SetStatus", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing status publisher.");
        cancellation.Token.Register(() =>
        {
            SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
            SetPrivateField(runtime, "_activeVolumeKey", "next-card");
            SetPrivateField(runtime, "_activeOperationId", Guid.NewGuid());
            setStatus.Invoke(runtime, [new { view = "copying" }]);
        });
        MethodInfo removed = typeof(StandaloneRuntimeService).GetMethod(
            "OnVolumeRemoved", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing volume removal method.");

        removed.Invoke(runtime, [runtime, removedVolume]);

        Assert.Equal(["failure", "copying"], events);
        Assert.Equal("copying", GetProperty<string>(runtime.GetStatusSnapshot(), "view"));
    }

    [Fact]
    public async Task Distinct_cards_keep_fifo_arrival_order_in_the_serial_queue()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var first = new VolumeEventArgs(Path.Combine(_root, "first"), "first-card", "NTFS", 1, DateTimeOffset.UtcNow);
        var second = new VolumeEventArgs(Path.Combine(_root, "second"), "second-card", "NTFS", 2, DateTimeOffset.UtcNow);
        var third = new VolumeEventArgs(Path.Combine(_root, "third"), "third-card", "NTFS", 3, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_activeTask", Task.Delay(TimeSpan.FromMinutes(1)));
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(first));
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        start.Invoke(runtime, [second, false]);
        start.Invoke(runtime, [third, false]);
        object[] pending = Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivateField(runtime, "_pendingVolumes")).Cast<object>().ToArray();

        Assert.Equal([second.VolumeGuid, third.VolumeGuid], pending.Select(item =>
            Assert.IsType<VolumeEventArgs>(item.GetType().GetProperty("Volume")!.GetValue(item)).VolumeGuid));
    }

    [Fact]
    public async Task External_drive_without_any_approved_source_directory_is_ignored_without_creating_card_state()
    {
        string sourceRoot = Path.Combine(_root, "unrelated-usb");
        Directory.CreateDirectory(sourceRoot);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "unrelated-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "unrelated-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("waiting", GetProperty<string>(result.Status, "view"));
        Assert.Contains("已批准素材目录", GetProperty<string>(result.Status, "headline"), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.CardIdentityFile));
        Assert.False(File.Exists(paths.CardInventoryBaselineFile));
        Assert.False(Directory.Exists(paths.TasksDirectory));
    }

    [Fact]
    public async Task Empty_card_is_initialized_so_its_first_later_material_will_not_be_swallowed()
    {
        string sourceRoot = Path.Combine(_root, "empty-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "empty-card-target");
        Directory.CreateDirectory(approvedRoot);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        StandaloneConfiguration configuration = new()
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "empty-card-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile)
                .FindAsync(sourceIdentity, CancellationToken.None));
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "FIRST.mov"), new byte[128]);
        TaskManifest later = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "empty-card-later",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]),
            CancellationToken.None);
        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            baseline,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]),
            later);

        Assert.Equal("baseline", GetProperty<string>(result.Status, "view"));
        Assert.Empty(baseline.Entries);
        Assert.Equal(@"DCIM\FIRST.mov", Assert.Single(delta.TransferEntries).RelativePath);
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public async Task Fat_file_identity_change_with_same_metadata_does_not_advance_baseline_without_content_proof()
    {
        string sourceRoot = Path.Combine(_root, "identity-churn-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "identity-churn-target");
        Directory.CreateDirectory(approvedRoot);
        Directory.CreateDirectory(targetRoot);
        string sourceFile = Path.Combine(approvedRoot, "A.mov");
        DateTime timestamp = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        await File.WriteAllBytesAsync(sourceFile, new byte[1024]);
        File.SetLastWriteTimeUtc(sourceFile, timestamp);

        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        var manifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
        var policy = new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        TaskManifest initial = await manifestBuilder.BuildAsync(
            Guid.NewGuid(), sourceRoot, "identity-churn", SourceHashPolicy.MetadataOnly, policy, CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        Guid cardId = Guid.NewGuid();
        await baselineStore.SaveInitialAsync(
            cardId,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            initial,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, initial, "exFAT", 1);

        string replacement = Path.Combine(approvedRoot, "replacement.tmp");
        await File.WriteAllBytesAsync(replacement, Enumerable.Repeat((byte)0x5A, 1024).ToArray());
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Delete(sourceFile);
        File.Move(replacement, sourceFile);
        File.SetLastWriteTimeUtc(sourceFile, timestamp);
        TaskManifest current = await manifestBuilder.BuildAsync(
            Guid.NewGuid(), sourceRoot, "identity-churn", SourceHashPolicy.MetadataOnly, policy, CancellationToken.None);
        Assert.NotEqual(
            Assert.Single(initial.Entries).SourceFileId,
            Assert.Single(current.Entries).SourceFileId);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "identity-churn-volume", "exFAT", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        Assert.False(GetProperty<bool>(result.Status, "safeToRemoveCard"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetRoot));
        StandaloneInventoryBaseline preserved = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindAsync(sourceIdentity, CancellationToken.None));
        Assert.Equal(Assert.Single(initial.Entries).SourceFileId, Assert.Single(preserved.Entries).SourceFileIdentity);
    }

    [Fact]
    public async Task Changed_selection_policy_fails_closed_without_swallowing_current_inventory()
    {
        string sourceRoot = Path.Combine(_root, "policy-change-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "policy-change-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "A.mov"), new byte[1024]);
        Guid templateId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "policy-change",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            cardId,
            templateId,
            sourceIdentity,
            new string('f', 64),
            inventory,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, inventory, "NTFS", 1);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "policy-change-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        Assert.False(GetProperty<bool>(result.Status, "safeToRemoveCard"));
        Assert.False(Directory.Exists(targetRoot));
        StandaloneInventoryBaseline preserved = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindAsync(sourceIdentity, CancellationToken.None));
        Assert.Equal(new string('f', 64), preserved.SelectionPolicyHash);
        Assert.Single(preserved.Entries);
    }

    [Fact]
    public async Task Missing_previous_completion_evidence_fails_closed_and_offers_full_reverification()
    {
        string sourceRoot = Path.Combine(_root, "missing-completion-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "missing-completion-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "A.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        var policy = new SourceSelectionPolicy(configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "missing-completion", SourceHashPolicy.MetadataOnly, policy, CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        Guid cardId = Guid.NewGuid();
        await baselineStore.SaveInitialAsync(
            cardId,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            inventory,
            CancellationToken.None);
        Guid staleCompletedTaskId = Guid.NewGuid();
        await baselineStore.AdvanceAfterCompletionAsync(
            cardId,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                configuration.ApprovedSourceDirectories, configuration.NormalizedExtensions),
            inventory,
            staleCompletedTaskId,
            CancellationToken.None);
        await BindCardSnapshotAsync(paths, cardId, sourceRoot, inventory, "NTFS", 1);

        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "missing-completion-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        Assert.False(GetProperty<bool>(result.Status, "safeToRemoveCard"));
        object failure = GetProperty<object?>(result.Status, "failure") ??
            throw new InvalidDataException("Failure payload is missing.");
        Assert.True(GetProperty<bool>(failure, "canRestartFresh"));
        Assert.Contains("完成", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(targetRoot));
        StandaloneInventoryBaseline preserved = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindAsync(sourceIdentity, CancellationToken.None));
        Assert.Equal(staleCompletedTaskId, preserved.LastCompletedTaskId);
    }

    [Fact]
    public async Task Fresh_restart_cannot_abandon_a_journal_while_a_task_is_still_active()
    {
        string sourceRoot = Path.Combine(_root, "active-restart-card");
        Directory.CreateDirectory(sourceRoot);
        StandaloneDataPaths paths = new(_root);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        Guid taskId = Guid.NewGuid();
        Directory.CreateDirectory(paths.TasksDirectory);
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(taskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = taskId,
                SourceIdentity = sourceIdentity,
                CardInstanceId = Guid.NewGuid(),
                TargetMode = StandaloneTargetMode.LocalOnly,
                ManifestHash = "active",
                LocalTargetIdentity = "local",
                LocalTargetRoot = Path.Combine(_root, "target"),
                NasTargetIdentity = string.Empty,
                NasTargetRoot = string.Empty,
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "active-restart-volume", "NTFS", 1, DateTimeOffset.UtcNow));
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(runtime, "_activeTask", active.Task);

        StandaloneRuntimeOperationResult result = await runtime.RestartFreshAsync(
            true, CancellationToken.None);

        Assert.Contains("仍在处理中", result.Message, StringComparison.Ordinal);
        Assert.Empty(await new StandaloneAbandonedTaskStore(paths.AbandonedTasksFile)
            .GetTaskIdsAsync(CancellationToken.None));
        Assert.NotNull(await new StandaloneCompletedTaskCatalog(paths).FindResumeCandidateAsync(
            sourceIdentity, CancellationToken.None));
        active.SetResult();
    }

    [Fact]
    public async Task Changed_target_mode_with_unfinished_task_fails_without_baselining_pending_material()
    {
        string sourceRoot = Path.Combine(_root, "target-mode-change-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string currentTarget = Path.Combine(_root, "current-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "pending.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = currentTarget,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        string sourceIdentity = sourceDomain.StorageIdentity!;
        Guid cardId = Guid.NewGuid();
        TaskManifest identityInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "target-mode-change-identity",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]),
            CancellationToken.None);
        await new StandaloneCardIdentityResolver(paths.CardIdentityFile).ReinitializeAsync(
            StandaloneCardEvidenceBuilder.Build(sourceRoot, sourceDomain, identityInventory, "NTFS", 1),
            cardId,
            CancellationToken.None);
        Guid oldTaskId = Guid.NewGuid();
        Directory.CreateDirectory(paths.TasksDirectory);
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(oldTaskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = oldTaskId,
                SourceIdentity = sourceIdentity,
                CardInstanceId = cardId,
                TargetMode = StandaloneTargetMode.NasOnly,
                ManifestHash = string.Empty,
                ContentManifestFrozen = false,
                LocalTargetIdentity = string.Empty,
                LocalTargetRoot = string.Empty,
                NasTargetIdentity = "old-nas",
                NasTargetRoot = @"Z:\old-task",
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        var volume = new VolumeEventArgs(sourceRoot, "target-mode-change-volume", "NTFS", 1, DateTimeOffset.UtcNow);
        MethodInfo start = typeof(StandaloneRuntimeService).GetMethod(
            "TryStartTransfer", BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing transfer start method.");

        Task run = Assert.IsAssignableFrom<Task>(start.Invoke(runtime, [volume, true]));
        await run;

        object status = runtime.GetStatusSnapshot();
        Assert.Equal("failure", GetProperty<string>(status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canRestartFresh"));
        Assert.Contains("保存方式与当前设置不一致", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.CardInventoryBaselineFile));
        Assert.True(File.Exists(paths.CardIdentityFile));
        Assert.True(File.Exists(paths.GetTaskJournalPath(oldTaskId)));
        Assert.False(Directory.Exists(currentTarget));
    }
    [Fact]
    public async Task Confirmed_fresh_restart_preserves_and_abandons_unrecoverable_old_journals()
    {
        string sourceRoot = Path.Combine(_root, "restart-fresh-card");
        Directory.CreateDirectory(sourceRoot);
        StandaloneDataPaths paths = new(_root);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        Guid oldTaskId = Guid.NewGuid();
        Directory.CreateDirectory(paths.TasksDirectory);
        var journalStore = new StandaloneTaskJournalStore(paths.GetTaskJournalPath(oldTaskId));
        await journalStore.InitializeAsync(new StandaloneTaskJournal
        {
            TaskId = oldTaskId,
            SourceIdentity = sourceIdentity,
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalOnly,
            ManifestHash = "old-manifest",
            LocalTargetIdentity = "old-local",
            LocalTargetRoot = Path.Combine(_root, "old-target"),
            NasTargetIdentity = string.Empty,
            NasTargetRoot = string.Empty,
            Files = [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "restart-fresh-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RestartFreshAsync(false, CancellationToken.None));
        StandaloneRuntimeOperationResult result = await runtime.RestartFreshAsync(
            true, CancellationToken.None);

        Assert.Contains("已保留并停用 1 个旧任务记录", result.Message, StringComparison.Ordinal);
        Assert.Contains(oldTaskId, await new StandaloneAbandonedTaskStore(paths.AbandonedTasksFile)
            .GetTaskIdsAsync(CancellationToken.None));
        Assert.Null(await new StandaloneCompletedTaskCatalog(paths).FindResumeCandidateAsync(
            sourceIdentity, CancellationToken.None));
        Assert.True(File.Exists(paths.GetTaskJournalPath(oldTaskId)));
    }

    [Fact]
    public async Task Changed_reader_offers_same_card_path_and_never_swallows_new_files_into_a_fresh_baseline()
    {
        string sourceRoot = Path.Combine(_root, "changed-reader-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "changed-reader-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[64]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        var selection = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        var builder = new ManifestBuilder(new FileSystemSourceEnumerator());
        TaskManifest historicalInventory = await builder.BuildAsync(
            Guid.NewGuid(), sourceRoot, "historical-reader", SourceHashPolicy.MetadataOnly,
            selection, CancellationToken.None);
        FaultDomainInfo currentSource = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        CardIdentityEvidence historicalEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, currentSource, historicalInventory);
        Guid cardId = Guid.NewGuid();
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = cardId,
                        Evidence = historicalEvidence,
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
            },
            CancellationToken.None);
        string policyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            ["DCIM"], [".mov"]);
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            cardId,
            "local-v1:old-reader-endpoint:old-volume:0000000000000001",
            policyHash,
            historicalInventory,
            CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "new.mov"), new byte[128]);
        var drive = new DriveInfo(Path.GetPathRoot(sourceRoot)!);
        var volume = new VolumeEventArgs(
            sourceRoot,
            "changed-reader-volume",
            drive.DriveFormat,
            drive.TotalSize,
            DateTimeOffset.UtcNow);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", volume);

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(failed.Status, "view"));
        object failure = GetProperty<object>(failed.Status, "failure")!;
        Assert.True(GetProperty<bool>(failure, "canReassociateCard"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("不会自动吞掉当前素材差分", GetProperty<string>(failure, "what"), StringComparison.Ordinal);

        _ = await runtime.ReassociateCurrentCardAsync(true, CancellationToken.None);

        Assert.Null(await baselineStore.FindAsync(
            "local-v1:old-reader-endpoint:old-volume:0000000000000001",
            CancellationToken.None));
        StandaloneInventoryBaseline rebound = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindAsync(currentSource.StorageIdentity!, CancellationToken.None));
        Assert.Single(rebound.Entries);
        Assert.Equal(@"DCIM\existing.mov", rebound.Entries[0].RelativePath);
        TaskManifest currentInventory = await builder.BuildAsync(
            Guid.NewGuid(), sourceRoot, "current-reader", SourceHashPolicy.MetadataOnly,
            selection, CancellationToken.None);
        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            rebound, policyHash, currentInventory, identityChangesRequireTransfer: true);
        Assert.Equal(@"DCIM\new.mov", Assert.Single(delta.TransferEntries).RelativePath);
    }

    [Fact]
    public async Task Identity_confirmation_failure_offers_and_completes_explicit_card_reinitialization()
    {
        string sourceRoot = Path.Combine(_root, "identity-confirmation-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "identity-confirmation-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "current.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "identity-confirmation",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        CardIdentityEvidence observed = StandaloneCardEvidenceBuilder.Build(sourceRoot, sourceDomain, inventory);
        CardIdentityEvidence weakCandidate = observed with
        {
            HistoricalCardInstanceId = "different-history",
            PnpDeviceInstanceId = "different-device",
            DeviceSerialNumber = "different-serial",
            RootDirectoryHash = new string('c', 64),
            SampleFingerprint = new string('d', 64),
        };
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = Guid.NewGuid(),
                        Evidence = weakCandidate,
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
            },
            CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "identity-confirmation-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(failed.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(failed.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.False(GetProperty<bool>(failure, "canRestartFresh"));

        StandaloneRuntimeOperationResult repaired = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(repaired.Status, "view"));
        Assert.False(GetProperty<bool>(repaired.Status, "safeToRemoveCard"));
        Assert.False(Directory.Exists(targetRoot));
        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile).FindAsync(
                sourceDomain.StorageIdentity!, CancellationToken.None));
        Assert.Empty(baseline.Entries);
        StandaloneCardIdentityMap identityMap = Assert.IsType<StandaloneCardIdentityMap>(
            await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile)
                .LoadAsync(CancellationToken.None));
        Assert.Equal(2, identityMap.Bindings.Count);
    }

    [Fact]
    public async Task Explicit_reinitialization_recovers_from_corrupt_baseline_state_without_target_writes()
    {
        string sourceRoot = Path.Combine(_root, "corrupt-baseline-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "corrupt-baseline-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "current.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.CardInventoryBaselineFile)!);
        await File.WriteAllTextAsync(paths.CardInventoryBaselineFile, "{not-json");
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "corrupt-baseline-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(failed.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(failed.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));

        StandaloneRuntimeOperationResult repaired = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(repaired.Status, "view"));
        Assert.False(GetProperty<bool>(repaired.Status, "safeToRemoveCard"));
        Assert.False(Directory.Exists(targetRoot));
        Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(paths.CardInventoryBaselineFile)!,
            Path.GetFileName(paths.CardInventoryBaselineFile) + ".corrupt-*.bak"));
        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile)
                .FindAsync(sourceDomain.StorageIdentity!, CancellationToken.None));
        Assert.Empty(baseline.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_card_initialization_resumes_same_card_id_without_swallowing_current_material(
        bool readerChangedAfterInterruption)
    {
        string sourceRoot = Path.Combine(_root, "pending-initialization-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "pending-initialization-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "pending.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid historicalCardId = Guid.NewGuid();
        Guid pendingCardId = Guid.NewGuid();
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = pendingCardId,
                    DisplayName = "Pending card",
                    CameraTemplateId = templateId,
                },
            ],
        }, CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        var selectionPolicy = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]);
        TaskManifest pendingInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "pending-initialization-evidence", SourceHashPolicy.MetadataOnly,
            selectionPolicy, CancellationToken.None);
        CardIdentityEvidence pendingEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, sourceDomain, pendingInventory, "NTFS", 1);
        await baselineStore.ReinitializePendingAsync(
            historicalCardId,
            templateId,
            sourceDomain.StorageIdentity!,
            selectionPolicyHash,
            pendingEvidence,
            abandonOtherPending: false,
            CancellationToken.None);
        await baselineStore.CommitInitializationAsync(historicalCardId, CancellationToken.None);
        string pendingSourceIdentity = readerChangedAfterInterruption
            ? "LOCAL|DISK=previous-reader|VOLUME=previous-volume"
            : sourceDomain.StorageIdentity!;
        await baselineStore.ReinitializePendingAsync(
            pendingCardId,
            templateId,
            pendingSourceIdentity,
            selectionPolicyHash,
            pendingEvidence,
            abandonOtherPending: false,
            CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "pending-initialization-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);
        Assert.Equal("failure", GetProperty<string>(failed.Status, "view"));
        object failure = GetProperty<object>(failed.Status, "failure")!;
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));

        StandaloneRuntimeOperationResult resumed = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(resumed.Status, "view"));
        object resumedFailure = GetProperty<object>(resumed.Status, "failure")!;
        string resumedWhat = GetProperty<string>(resumedFailure, "what")!;
        Assert.DoesNotContain("身份绑定", resumedWhat, StringComparison.Ordinal);
        Assert.DoesNotContain("初始化在提交完成前中断", resumedWhat, StringComparison.Ordinal);
        Assert.False(GetProperty<bool>(resumedFailure, "canReinitializeCard"));
        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindByCardInstanceIdAsync(pendingCardId, CancellationToken.None));
        Assert.False(baseline.InitializationPending);
        Assert.Equal(sourceDomain.StorageIdentity, baseline.SourceIdentity);
        Assert.Empty(baseline.Entries);
        StandaloneInventoryBaseline historical = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindByCardInstanceIdAsync(historicalCardId, CancellationToken.None));
        Assert.False(historical.InitializationPending);
        StandaloneCardIdentityMap identityMap = Assert.IsType<StandaloneCardIdentityMap>(
            await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile)
                .LoadAsync(CancellationToken.None));
        Assert.Contains(identityMap.Bindings, binding => binding.CardInstanceId == pendingCardId);
        Assert.False(Directory.Exists(targetRoot));

        StandaloneRuntimeOperationResult retried = await runtime.RetryAsync(CancellationToken.None);
        Assert.Equal("failure", GetProperty<string>(retried.Status, "view"));
        StandaloneInventoryBaselineDocument stable = Assert.IsType<StandaloneInventoryBaselineDocument>(
            await new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(paths.CardInventoryBaselineFile)
                .LoadAsync(CancellationToken.None));
        Assert.Equal(2, stable.Baselines.Count);
        Assert.DoesNotContain(stable.Baselines, value => value.InitializationPending);
    }

    [Fact]
    public async Task Unrelated_and_duplicate_pending_initializations_are_abandoned_without_card_id_overwrite_or_growth()
    {
        string sourceRoot = Path.Combine(_root, "current-card");
        string sourceA = Path.Combine(_root, "previous-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        Directory.CreateDirectory(Path.Combine(sourceA, "DCIM"));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "DCIM", "current.mov"), Enumerable.Repeat((byte)0xB2, 2048).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(sourceA, "DCIM", "previous.mov"), Enumerable.Repeat((byte)0xA1, 1024).ToArray());
        string targetRoot = Path.Combine(_root, "pending-quarantine-target");
        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid knownCurrentCard = Guid.NewGuid();
        Guid pendingA = Guid.NewGuid();
        Guid pendingDuplicate = Guid.NewGuid();
        Guid pendingMatching = Guid.NewGuid();
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
        }, CancellationToken.None);
        var policy = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        string policyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]);
        FaultDomainInfo currentDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        FaultDomainInfo previousDomain = new FaultDomainResolver().ResolveLocalDomain(sourceA);
        var manifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
        TaskManifest previousInventory = await manifestBuilder.BuildAsync(
            Guid.NewGuid(), sourceA, "previous-pending-evidence", SourceHashPolicy.MetadataOnly,
            policy, CancellationToken.None);
        CardIdentityEvidence previousEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceA, previousDomain, previousInventory, "NTFS", 1);
        TaskManifest currentInventory = await manifestBuilder.BuildAsync(
            Guid.NewGuid(), sourceRoot, "current-card-evidence", SourceHashPolicy.MetadataOnly,
            policy, CancellationToken.None);
        CardIdentityEvidence currentEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, currentDomain, currentInventory, "NTFS", 1);
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.ReinitializePendingAsync(
            knownCurrentCard, templateId, currentDomain.StorageIdentity!, policyHash, currentEvidence,
            abandonOtherPending: false, CancellationToken.None);
        await baselineStore.CommitInitializationAsync(knownCurrentCard, CancellationToken.None);
        await baselineStore.ReinitializePendingAsync(
            pendingA, templateId, currentDomain.StorageIdentity!, policyHash, previousEvidence,
            abandonOtherPending: false, CancellationToken.None);
        await baselineStore.ReinitializePendingAsync(
            pendingDuplicate, templateId, currentDomain.StorageIdentity!, policyHash, previousEvidence,
            abandonOtherPending: false, CancellationToken.None);
        await baselineStore.ReinitializePendingAsync(
            pendingMatching, templateId, currentDomain.StorageIdentity!, policyHash, currentEvidence,
            abandonOtherPending: false, CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = pendingA,
                        Evidence = previousEvidence,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    },
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = knownCurrentCard,
                        Evidence = currentEvidence,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    },
                ],
            }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "current-card-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);
        object failedFacts = GetProperty<object>(failed.Status, "failure")!;
        Assert.True(GetProperty<bool>(failedFacts, "canReinitializeCard"));

        StandaloneRuntimeOperationResult repaired = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(repaired.Status, "view"));
        StandaloneInventoryBaselineDocument repairedDocument = Assert.IsType<StandaloneInventoryBaselineDocument>(
            await new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(paths.CardInventoryBaselineFile)
                .LoadAsync(CancellationToken.None));
        StandaloneInventoryBaseline current = Assert.Single(repairedDocument.Baselines);
        Assert.Equal(knownCurrentCard, current.CardInstanceId);
        Assert.NotEqual(pendingA, current.CardInstanceId);
        Assert.NotEqual(pendingDuplicate, current.CardInstanceId);
        Assert.NotEqual(pendingMatching, current.CardInstanceId);
        Assert.False(current.InitializationPending);
        Assert.Empty(current.Entries);
        Assert.Equal(3, repairedDocument.AbandonedInitializations.Count);
        Assert.Contains(repairedDocument.AbandonedInitializations, value => value.CardInstanceId == pendingA);
        Assert.Contains(repairedDocument.AbandonedInitializations, value => value.CardInstanceId == pendingDuplicate);
        Assert.Contains(repairedDocument.AbandonedInitializations, value => value.CardInstanceId == pendingMatching);
        StandaloneCardIdentityMap identities = Assert.IsType<StandaloneCardIdentityMap>(
            await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile)
                .LoadAsync(CancellationToken.None));
        StandaloneCardIdentityBinding original = Assert.Single(
            identities.Bindings, value => value.CardInstanceId == pendingA);
        Assert.Equal(previousEvidence.RootDirectoryHash, original.Evidence.RootDirectoryHash);
        Assert.Equal(previousEvidence.SampleFingerprint, original.Evidence.SampleFingerprint);

        _ = await runtime.RetryAsync(CancellationToken.None);
        StandaloneInventoryBaselineDocument stable = Assert.IsType<StandaloneInventoryBaselineDocument>(
            await new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(paths.CardInventoryBaselineFile)
                .LoadAsync(CancellationToken.None));
        Assert.Single(stable.Baselines);
        Assert.Equal(3, stable.AbandonedInitializations.Count);
        Assert.DoesNotContain(stable.Baselines, value => value.InitializationPending);
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public async Task Multiple_committed_identity_matches_fail_without_creating_or_mutating_card_state()
    {
        string sourceRoot = Path.Combine(_root, "duplicate-committed-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "DCIM", "same.mov"),
            Enumerable.Repeat((byte)0x5A, 2048).ToArray());
        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid firstCommitted = Guid.NewGuid();
        Guid secondCommitted = Guid.NewGuid();
        Guid interrupted = Guid.NewGuid();
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "duplicate-committed-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
        }, CancellationToken.None);
        var policy = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "duplicate-committed-evidence", SourceHashPolicy.MetadataOnly,
            policy, CancellationToken.None);
        FaultDomainInfo domain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        CardIdentityEvidence evidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, domain, inventory, "NTFS", 1);
        string policyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]);
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        foreach (Guid cardId in new[] { firstCommitted, secondCommitted })
        {
            await baselineStore.ReinitializePendingAsync(
                cardId, templateId, domain.StorageIdentity!, policyHash, evidence,
                abandonOtherPending: false, CancellationToken.None);
            await baselineStore.CommitInitializationAsync(cardId, CancellationToken.None);
        }
        await baselineStore.ReinitializePendingAsync(
            interrupted, templateId, domain.StorageIdentity!, policyHash, evidence,
            abandonOtherPending: false, CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = firstCommitted,
                        Evidence = evidence,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    },
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = secondCommitted,
                        Evidence = evidence,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    },
                ],
            }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "duplicate-committed-volume", "NTFS", 1, DateTimeOffset.UtcNow));
        StandaloneRuntimeOperationResult initial = await runtime.RetryAsync(CancellationToken.None);
        Assert.True(GetProperty<bool>(GetProperty<object>(initial.Status, "failure")!, "canReinitializeCard"));
        byte[] baselineBefore = await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile);
        byte[] identityBefore = await File.ReadAllBytesAsync(paths.CardIdentityFile);

        StandaloneRuntimeOperationResult firstRepair = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);
        StandaloneRuntimeOperationResult secondRepair = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(firstRepair.Status, "view"));
        Assert.Contains("多个已提交", GetProperty<string>(
            GetProperty<object>(firstRepair.Status, "failure")!, "what"), StringComparison.Ordinal);
        Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile));
        Assert.Equal(identityBefore, await File.ReadAllBytesAsync(paths.CardIdentityFile));
        Assert.Equal("failure", GetProperty<string>(secondRepair.Status, "view"));
        StandaloneInventoryBaselineDocument stable = Assert.IsType<StandaloneInventoryBaselineDocument>(
            await new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(paths.CardInventoryBaselineFile)
                .LoadAsync(CancellationToken.None));
        Assert.Equal(3, stable.Baselines.Count);
        Assert.Empty(stable.AbandonedInitializations);
    }

    [Fact]
    public async Task Confirmed_card_reinitialization_repairs_template_binding_without_source_content_or_target_writes()
    {
        string sourceRoot = Path.Combine(_root, "reinitialize-card");
        string oldRoot = Path.Combine(sourceRoot, "OLD");
        string currentRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "reinitialize-target");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(currentRoot);
        await File.WriteAllBytesAsync(Path.Combine(oldRoot, "old.mxf"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(currentRoot, "current.mov"), new byte[2048]);

        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        Guid oldTemplateId = Guid.NewGuid();
        Guid currentTemplateId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        var configuration = new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["OLD"],
            ApprovedExtensions = [".mxf"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = currentTemplateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = oldTemplateId,
                    Name = "旧模板",
                    ApprovedSourceDirectories = ["OLD"],
                    ApprovedExtensions = [".mxf"],
                },
                new StandaloneCameraTemplate
                {
                    TemplateId = currentTemplateId,
                    Name = "当前模板",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = cardId,
                    DisplayName = "当前素材卡",
                    CameraTemplateId = currentTemplateId,
                },
            ],
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        TaskManifest oldInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "old-template",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["OLD"], [".mxf"]),
            CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            cardId,
            oldTemplateId,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["OLD"], [".mxf"]),
            oldInventory,
            CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "reinitialize-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ReinitializeCurrentCardAsync(false, CancellationToken.None));
        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);
        Assert.Equal("failure", GetProperty<string>(failed.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(failed.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));

        StandaloneRuntimeOperationResult result = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        Assert.False(GetProperty<bool>(result.Status, "safeToRemoveCard"));
        Assert.False(Directory.Exists(targetRoot));
        IReadOnlyList<StandaloneInventoryBaseline> sourceBaselines = await baselineStore
            .FindAllBySourceIdentityAsync(sourceIdentity, CancellationToken.None);
        StandaloneInventoryBaseline repaired = Assert.Single(
            sourceBaselines, candidate => candidate.CardInstanceId != cardId);
        Assert.Contains(sourceBaselines, candidate => candidate.CardInstanceId == cardId);
        Assert.Equal(currentTemplateId, repaired.CameraTemplateId);
        Assert.Null(repaired.LastCompletedTaskId);
        Assert.Empty(repaired.Entries);
        StandaloneConfiguration persisted = Assert.IsType<StandaloneConfiguration>(
            await configurationStore.LoadAsync(CancellationToken.None));
        Assert.Equal(currentTemplateId, persisted.CardProfiles.Single(
            profile => profile.CardInstanceId == repaired.CardInstanceId).CameraTemplateId);
        Assert.Contains(persisted.CardProfiles, profile => profile.CardInstanceId == cardId);
    }

    [Fact]
    public async Task Reused_source_identity_with_different_media_capacity_blocks_old_baseline_and_can_be_reinitialized()
    {
        string sourceRoot = Path.Combine(_root, "reused-source-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        string targetRoot = Path.Combine(_root, "reused-source-target");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[64]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        StandaloneConfiguration configuration = new()
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        TaskManifest initial = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "reused-source-initial", SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]), CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        var resolver = new StandaloneCardIdentityResolver(paths.CardIdentityFile);
        StandaloneCardIdentityResolution oldCard = await resolver.ResolveAsync(new CardIdentityEvidence
        {
            VolumeGuid = Guid.NewGuid(),
            FileSystem = "NTFS",
            Capacity = 32,
            RootDirectoryHash = new string('a', 64),
        }, CancellationToken.None);
        await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile).SaveInitialAsync(
            oldCard.CardInstanceId!.Value,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]),
            initial,
            CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "new.mov"), new byte[128]);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "reused-volume-guid", "NTFS", 64, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult failed = await runtime.RetryAsync(CancellationToken.None);
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(failed.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("容量", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        StandaloneRuntimeOperationResult repaired = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);
        var repairedBaselines = await new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile)
            .FindAllBySourceIdentityAsync(sourceIdentity, CancellationToken.None);
        StandaloneInventoryBaseline baseline = Assert.Single(
            repairedBaselines, candidate => candidate.CardInstanceId != oldCard.CardInstanceId);

        Assert.Equal("failure", GetProperty<string>(repaired.Status, "view"));
        Assert.NotEqual(oldCard.CardInstanceId, baseline.CardInstanceId);
        Assert.Empty(baseline.Entries);
        Assert.Contains(repairedBaselines, candidate => candidate.CardInstanceId == oldCard.CardInstanceId);
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public async Task Local_state_persistence_failure_does_not_offer_card_reinitialization()
    {
        string sourceRoot = Path.Combine(_root, "state-persistence-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[64]);

        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "state-persistence-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        Directory.CreateDirectory(paths.CardInventoryBaselineFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "state-persistence-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(result.Status, "failure"));
        Assert.False(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.False(GetProperty<bool>(failure, "canRestartFresh"));
        Assert.False(Directory.Exists(Path.Combine(_root, "state-persistence-target")));
    }

    [Fact]
    public async Task Failed_card_reinitialization_returns_terminal_recoverable_failure_instead_of_staying_preparing()
    {
        string sourceRoot = Path.Combine(_root, "reinitialize-persistence-failure-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[64]);

        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "reinitialize-persistence-failure-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);

        var volume = new VolumeEventArgs(
            sourceRoot, "reinitialize-persistence-failure-volume", "NTFS", 1, DateTimeOffset.UtcNow);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", volume);
        SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(volume));
        SetPrivateField(runtime, "_currentVolumeBlocked", true);
        SetPrivateField(runtime, "_cardReinitializationAuthorizedVolumeKey", GetVolumeKey(volume));
        SetPrivateField(runtime, "_cardReinitializationAuthorizedSourceIdentity", sourceIdentity);
        Directory.CreateDirectory(paths.CardIdentityFile);

        StandaloneRuntimeOperationResult result = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(result.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("重新初始化未完成", result.Message, StringComparison.Ordinal);
        Assert.False(Assert.IsType<bool>(GetPrivateField(runtime, "_stateRepairInProgress")));
        Assert.True(Assert.IsType<bool>(GetPrivateField(runtime, "_currentVolumeBlocked")));
        Assert.False(Directory.Exists(Path.Combine(_root, "reinitialize-persistence-failure-target")));
    }

    [Fact]
    public async Task Card_reinitialization_is_rejected_without_a_current_recoverable_failure()
    {
        string sourceRoot = Path.Combine(_root, "unauthorized-reinitialize-card");
        string approvedRoot = Path.Combine(sourceRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "existing.mov"), new byte[1024]);
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "unauthorized-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        var policy = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        TaskManifest initial = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "unauthorized", SourceHashPolicy.MetadataOnly, policy, CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        Guid cardId = Guid.NewGuid();
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            cardId,
            sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]),
            initial,
            CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "new.mov"), new byte[2048]);
        byte[] baselineBefore = await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "unauthorized-reinitialize-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Contains("未执行重新初始化", result.Message, StringComparison.Ordinal);
        Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile));
        StandaloneInventoryBaseline unchanged = Assert.IsType<StandaloneInventoryBaseline>(
            await baselineStore.FindAsync(sourceIdentity, CancellationToken.None));
        Assert.Equal(@"DCIM\existing.mov", Assert.Single(unchanged.Entries).RelativePath);
        Assert.False(File.Exists(paths.CardIdentityFile));
        Assert.False(Directory.Exists(configuration.LocalTargetPath));
    }

    [Fact]
    public async Task New_card_reinitialization_refuses_to_hide_a_cross_reader_unfinished_task_candidate()
    {
        string sourceRoot = Path.Combine(_root, "cross-reader-reinitialize-blocked-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await store.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "cross-reader-reinitialize-blocked-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        Guid cardId = Guid.NewGuid();
        Guid unfinishedTaskId = Guid.NewGuid();
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(unfinishedTaskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = unfinishedTaskId,
                SourceIdentity = "old-reader-source",
                CardInstanceId = cardId,
                TargetMode = StandaloneTargetMode.LocalOnly,
                ManifestHash = "unfinished-cross-reader",
                LocalTargetIdentity = "local",
                LocalTargetRoot = Path.Combine(_root, "old-target"),
                NasTargetIdentity = string.Empty,
                NasTargetRoot = string.Empty,
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        FaultDomainInfo currentSource = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        var drive = new DriveInfo(Path.GetPathRoot(sourceRoot)!);
        var volume = new VolumeEventArgs(
            sourceRoot, "cross-reader-reinitialize-blocked-volume", drive.DriveFormat,
            drive.TotalSize, DateTimeOffset.UtcNow);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        SetPrivateField(runtime, "_lastArrivedVolume", volume);
        SetPrivateField(runtime, "_cardReinitializationAuthorizedVolumeKey", GetVolumeKey(volume));
        SetPrivateField(runtime, "_cardReinitializationAuthorizedSourceIdentity", currentSource.StorageIdentity);
        SetPrivateField(runtime, "_cardReassociationAuthorizedCardInstanceId", cardId);
        SetPrivateField(runtime, "_cardReassociationAuthorizedPreviousSourceIdentity", "old-reader-source");

        StandaloneRuntimeOperationResult result = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        Assert.Contains("仍有未完成任务", GetProperty<string>(
            GetProperty<object>(result.Status, "failure")!, "title"), StringComparison.Ordinal);
        Assert.True(File.Exists(paths.GetTaskJournalPath(unfinishedTaskId)));
        Assert.False(File.Exists(paths.CardIdentityFile));
        Assert.False(File.Exists(paths.CardInventoryBaselineFile));
    }

    [Fact]
    public async Task Card_reinitialization_refuses_to_hide_an_unfinished_task()
    {
        string sourceRoot = Path.Combine(_root, "reinitialize-blocked-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "blocked-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        }, CancellationToken.None);
        string sourceIdentity = new FaultDomainResolver().ResolveLocalDomain(sourceRoot).StorageIdentity!;
        Guid unfinishedTaskId = Guid.NewGuid();
        Guid unfinishedCardId = Guid.NewGuid();
        Directory.CreateDirectory(paths.TasksDirectory);
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(unfinishedTaskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = unfinishedTaskId,
                SourceIdentity = sourceIdentity,
                CardInstanceId = unfinishedCardId,
                TargetMode = StandaloneTargetMode.LocalOnly,
                ManifestHash = "unfinished",
                LocalTargetIdentity = "local",
                LocalTargetRoot = Path.Combine(_root, "blocked-target"),
                NasTargetIdentity = string.Empty,
                NasTargetRoot = string.Empty,
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        var blockedVolume = new VolumeEventArgs(
            sourceRoot, "reinitialize-blocked-volume", "NTFS", 1, DateTimeOffset.UtcNow);
        SetPrivateField(runtime, "_lastArrivedVolume", blockedVolume);
        SetPrivateField(runtime, "_cardReinitializationAuthorizedVolumeKey", GetVolumeKey(blockedVolume));
        SetPrivateField(runtime, "_cardReinitializationAuthorizedSourceIdentity", sourceIdentity);
        SetPrivateField(runtime, "_cardReassociationAuthorizedCardInstanceId", unfinishedCardId);

        StandaloneRuntimeOperationResult result = await runtime.ReinitializeCurrentCardAsync(
            true, CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = Assert.IsAssignableFrom<object>(GetProperty<object>(result.Status, "failure"));
        Assert.True(GetProperty<bool>(failure, "canRestartFresh"));
        Assert.False(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.False(File.Exists(paths.CardInventoryBaselineFile));
        Assert.False(File.Exists(paths.CardIdentityFile));
        Assert.True(File.Exists(paths.GetTaskJournalPath(unfinishedTaskId)));
    }

    [Fact]
    public void Legacy_baseline_selection_policy_resolves_the_matching_camera_template()
    {
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        StandaloneConfiguration configuration = MultiTemplateConfiguration(firstTemplate, secondTemplate);
        StandaloneCameraTemplate expected = configuration.CameraTemplates[1];
        var baseline = new StandaloneInventoryBaseline
        {
            CardInstanceId = Guid.NewGuid(),
            SourceIdentity = "source-a",
            SelectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                expected.ApprovedSourceDirectories,
                expected.NormalizedExtensions),
            Entries = [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        StandaloneCameraTemplate selected = ResolveCameraTemplate(configuration, baseline, _root);

        Assert.Equal(secondTemplate, selected.TemplateId);
    }

    [Fact]
    public void Unknown_card_uses_the_only_template_with_an_approved_candidate()
    {
        string sourceRoot = Path.Combine(_root, "camera-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "CONTENTS", "CLIPS001"));
        File.WriteAllBytes(Path.Combine(sourceRoot, "CONTENTS", "CLIPS001", "clip.mp4"), [1, 2, 3]);
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        StandaloneConfiguration configuration = MultiTemplateConfiguration(firstTemplate, secondTemplate);

        StandaloneCameraTemplate selected = ResolveCameraTemplate(configuration, baseline: null, sourceRoot);

        Assert.Equal(secondTemplate, selected.TemplateId);
    }

    [Fact]
    public void Unknown_card_with_multiple_templates_and_no_unique_candidate_requires_explicit_selection()
    {
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        StandaloneConfiguration configuration = MultiTemplateConfiguration(firstTemplate, secondTemplate);
        string sourceRoot = Path.Combine(_root, "ambiguous-empty-card");
        Directory.CreateDirectory(sourceRoot);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            ResolveCameraTemplate(configuration, baseline: null, sourceRoot));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Unknown_card_matching_multiple_templates_requires_explicit_selection()
    {
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        string sourceRoot = Path.Combine(_root, "ambiguous-multi-card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        File.WriteAllBytes(Path.Combine(sourceRoot, "DCIM", "A.mov"), [1, 2, 3]);
        StandaloneConfiguration configuration = MultiTemplateConfiguration(firstTemplate, secondTemplate) with
        {
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = firstTemplate,
                    Name = "A",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
                new StandaloneCameraTemplate
                {
                    TemplateId = secondTemplate,
                    Name = "B",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
        };

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            ResolveCameraTemplate(configuration, baseline: null, sourceRoot));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Cancel_contract_requires_the_expected_task_identity()
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(nameof(StandaloneRuntimeService.CancelAsync)) ??
            throw new InvalidOperationException("Missing cancel operation.");

        ParameterInfo parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(Guid?), parameter.ParameterType);
    }

    [Fact]
    public void Baseline_and_card_profile_template_mismatch_fails_closed()
    {
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        StandaloneConfiguration configuration = MultiTemplateConfiguration(firstTemplate, secondTemplate) with
        {
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = cardId,
                    DisplayName = "A 机位",
                    CameraTemplateId = firstTemplate,
                },
            ],
        };
        var baseline = new StandaloneInventoryBaseline
        {
            SchemaVersion = 2,
            CardInstanceId = cardId,
            CameraTemplateId = secondTemplate,
            SourceIdentity = "source-a",
            SelectionPolicyHash = "policy-a",
            Entries = [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            ResolveCameraTemplate(configuration, baseline, _root));
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task Retry_reports_the_actual_failed_closed_result_instead_of_a_success_toast()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        var volume = new VolumeEventArgs(
            _root,
            "volume-guid",
            "NTFS",
            1,
            DateTimeOffset.UtcNow,
            MountIdentity: "root:M00000001",
            MountContinuityProven: true);
        SetPrivateField(runtime, "_lastArrivedVolume", volume);

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Contains("重新检查被拒绝", result.Message, StringComparison.Ordinal);
        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
    }

    [Fact]
    public async Task Delayed_cancel_for_previous_task_does_not_cancel_current_task()
    {
        StandaloneDataPaths paths = new(_root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, store);
        Guid previousOperationId = Guid.NewGuid();
        Guid currentOperationId = Guid.NewGuid();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        var status = new
        {
            view = "copying",
            operationId = currentOperationId.ToString("D"),
            safeToRemoveCard = false,
        };
        SetPrivateField(runtime, "_activeTask", active.Task);
        SetPrivateField(runtime, "_activeOperationId", currentOperationId);
        SetPrivateField(runtime, "_taskCts", cts);
        SetPrivateField(runtime, "_status", status);

        object staleResponse = await runtime.CancelAsync(previousOperationId);

        Assert.False(cts.IsCancellationRequested);
        Assert.Same(status, runtime.GetStatusSnapshot());
        Assert.Contains("旧执行", GetProperty<string>(staleResponse, "message"), StringComparison.Ordinal);

        _ = await runtime.CancelAsync(currentOperationId);
        Assert.True(cts.IsCancellationRequested);
        active.SetResult();
    }

    [Fact]
    public void Local_target_task_directory_is_not_created_before_physical_isolation_passes()
    {
        string sourceRoot = Path.Combine(_root, "isolation-source");
        string configuredTarget = Path.Combine(_root, "isolation-target");
        string desiredTarget = Path.Combine(configuredTarget, "task-suffix");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(configuredTarget);
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "PrepareLocalTargetRoot", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing local target preflight method.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [resolver, sourceDomain, configuredTarget, desiredTarget]));

        Assert.IsAssignableFrom<Exception>(exception.InnerException);
        Assert.False(Directory.Exists(desiredTarget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(configuredTarget));
    }

    [Fact]
    public void Missing_configured_target_root_is_not_created_until_physical_isolation_passes()
    {
        string sourceRoot = Path.Combine(_root, "missing-root-source");
        string configuredTarget = Path.Combine(_root, "missing-root-target", "nested");
        string desiredTarget = Path.Combine(configuredTarget, "task-suffix");
        Directory.CreateDirectory(sourceRoot);
        var resolver = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = resolver.ResolveLocalDomain(sourceRoot);
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "PrepareLocalTargetRoot", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing local target preflight method.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [resolver, sourceDomain, configuredTarget, desiredTarget]));

        Assert.IsAssignableFrom<Exception>(exception.InnerException);
        Assert.False(Directory.Exists(configuredTarget));
        Assert.False(Directory.Exists(desiredTarget));
    }

    [Fact]
    public async Task Reused_storage_endpoint_with_different_card_content_requires_confirmation_and_preserves_old_baseline()
    {
        string currentRoot = Path.Combine(_root, "collision-current");
        string approvedRoot = Path.Combine(currentRoot, "DCIM");
        Directory.CreateDirectory(approvedRoot);
        string commonPath = Path.Combine(approvedRoot, "COMMON.mov");
        string replacedPath = Path.Combine(approvedRoot, "OLD.mov");
        await File.WriteAllBytesAsync(commonPath, Enumerable.Repeat((byte)0x22, 256).ToArray());
        await File.WriteAllBytesAsync(replacedPath, Enumerable.Repeat((byte)0x11, 256).ToArray());

        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid historicalCardId = Guid.NewGuid();
        string targetRoot = Path.Combine(_root, "collision-target");
        var configuration = new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = historicalCardId,
                    DisplayName = "Historical",
                    CameraTemplateId = templateId,
                },
            ],
        };
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        var builder = new ManifestBuilder(new FileSystemSourceEnumerator());
        var selection = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        TaskManifest historicalInventory = await builder.BuildAsync(
            Guid.NewGuid(), currentRoot, "collision-history", SourceHashPolicy.MetadataOnly,
            selection, CancellationToken.None);
        FaultDomainInfo domain = new FaultDomainResolver().ResolveLocalDomain(currentRoot);
        string sourceIdentity = domain.StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            historicalCardId, templateId, sourceIdentity,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]),
            historicalInventory, CancellationToken.None);
        CardIdentityEvidence historicalEvidence = StandaloneCardEvidenceBuilder.Build(
            currentRoot, domain, historicalInventory, "NTFS", 1);
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = historicalCardId,
                        Evidence = historicalEvidence,
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
            }, CancellationToken.None);
        File.Delete(replacedPath);
        await File.WriteAllBytesAsync(Path.Combine(approvedRoot, "NEW.mov"),
            Enumerable.Repeat((byte)0x77, 256).ToArray());
        byte[] baselineBefore = await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            currentRoot, "reused-endpoint", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = GetProperty<object>(result.Status, "failure")!;
        Assert.True(GetProperty<bool>(failure, "canReassociateCard"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("共享相同存储端点", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile));
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public async Task Reused_endpoint_with_empty_historical_baseline_requires_confirmation()
    {
        string sourceRoot = Path.Combine(_root, "empty-endpoint-collision");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        string targetRoot = Path.Combine(_root, "empty-endpoint-target");
        StandaloneDataPaths paths = new(_root);
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        TaskManifest emptyInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "empty-endpoint-history",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]),
            CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        Guid historicalCardId = Guid.NewGuid();
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        await baselineStore.SaveInitialAsync(
            historicalCardId,
            sourceDomain.StorageIdentity!,
            StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]),
            emptyInventory,
            CancellationToken.None);
        await new StandaloneCardIdentityResolver(paths.CardIdentityFile).ReinitializeAsync(
            StandaloneCardEvidenceBuilder.Build(sourceRoot, sourceDomain, emptyInventory, "NTFS", 1),
            historicalCardId,
            CancellationToken.None);
        byte[] baselineBefore = await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "empty-endpoint-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = GetProperty<object>(result.Status, "failure")!;
        Assert.True(GetProperty<bool>(failure, "canReassociateCard"));
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(paths.CardInventoryBaselineFile));
        Assert.False(Directory.Exists(targetRoot));
    }

    [Fact]
    public async Task Same_endpoint_multiple_card_candidates_select_the_unique_content_match_before_journal_lookup()
    {
        string cardARoot = Path.Combine(_root, "multi-candidate-a");
        string cardBRoot = Path.Combine(_root, "multi-candidate-mounted");
        Directory.CreateDirectory(Path.Combine(cardARoot, "DCIM"));
        Directory.CreateDirectory(Path.Combine(cardBRoot, "DCIM"));
        await File.WriteAllBytesAsync(Path.Combine(cardARoot, "DCIM", "A.mov"),
            Enumerable.Repeat((byte)0x11, 512).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(cardBRoot, "DCIM", "B.mov"),
            Enumerable.Repeat((byte)0x77, 512).ToArray());
        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid cardAId = Guid.NewGuid();
        Guid cardBId = Guid.NewGuid();
        string targetRoot = Path.Combine(_root, "multi-candidate-target");
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var configuration = new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = targetRoot,
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = cardAId,
                    DisplayName = "Card A",
                    CameraTemplateId = templateId,
                },
                new StandaloneCardProfile
                {
                    CardInstanceId = cardBId,
                    DisplayName = "Card B",
                    CameraTemplateId = templateId,
                },
            ],
        };
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        var builder = new ManifestBuilder(new FileSystemSourceEnumerator());
        var policy = new SourceSelectionPolicy(["DCIM"], [".mov"]);
        TaskManifest inventoryA = await builder.BuildAsync(
            Guid.NewGuid(), cardARoot, "candidate-a", SourceHashPolicy.MetadataOnly,
            policy, CancellationToken.None);
        TaskManifest inventoryB = await builder.BuildAsync(
            Guid.NewGuid(), cardBRoot, "candidate-b", SourceHashPolicy.MetadataOnly,
            policy, CancellationToken.None);
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(cardBRoot);
        string sourceIdentity = sourceDomain.StorageIdentity!;
        var baselineStore = new StandaloneInventoryBaselineStore(paths.CardInventoryBaselineFile);
        string selectionHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(["DCIM"], [".mov"]);
        await baselineStore.SaveInitialAsync(
            cardAId, templateId, sourceIdentity, selectionHash, inventoryA, CancellationToken.None);
        await baselineStore.SaveInitialAsync(
            cardBId, templateId, sourceIdentity, selectionHash, inventoryB, CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = cardAId,
                        Evidence = StandaloneCardEvidenceBuilder.Build(
                            cardARoot, sourceDomain, inventoryA, "NTFS", 1),
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-2),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-2),
                    },
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = cardBId,
                        Evidence = StandaloneCardEvidenceBuilder.Build(
                            cardBRoot, sourceDomain, inventoryB, "NTFS", 1),
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
            }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            cardBRoot, "multi-candidate-volume", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("baseline", GetProperty<string>(result.Status, "view"));
        Assert.True(GetProperty<bool>(result.Status, "safeToRemoveCard"));
        Assert.False(Directory.Exists(targetRoot));
        StandaloneCardIdentityMap persisted = Assert.IsType<StandaloneCardIdentityMap>(
            await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile)
                .LoadAsync(CancellationToken.None));
        StandaloneCardIdentityBinding cardB = Assert.Single(
            persisted.Bindings, binding => binding.CardInstanceId == cardBId);
        StandaloneCardIdentityBinding cardA = Assert.Single(
            persisted.Bindings, binding => binding.CardInstanceId == cardAId);
        Assert.True(cardB.LastSeenUtc > cardA.LastSeenUtc);
    }

    [Fact]
    public async Task Known_card_with_missing_baseline_is_not_silently_first_baselined()
    {
        string sourceRoot = Path.Combine(_root, "missing-known-baseline");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "DCIM", "A.mov"), new byte[128]);
        StandaloneDataPaths paths = new(_root);
        Guid templateId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        var configuration = new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "missing-known-target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = cardId,
                    DisplayName = "Known",
                    CameraTemplateId = templateId,
                },
            ],
        };
        var configurationStore = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        await configurationStore.SaveAsync(configuration, CancellationToken.None);
        TaskManifest inventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
            Guid.NewGuid(), sourceRoot, "known-missing", SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(["DCIM"], [".mov"]), CancellationToken.None);
        FaultDomainInfo domain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        CardIdentityEvidence evidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, domain, inventory, "NTFS", 1);
        await new AtomicJsonFileStore<StandaloneCardIdentityMap>(paths.CardIdentityFile).SaveAsync(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = cardId,
                        Evidence = evidence,
                        FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                ],
            }, CancellationToken.None);
        await using StandaloneRuntimeService runtime = CreateRuntime(paths, configurationStore);
        SetPrivateField(runtime, "_lastArrivedVolume", new VolumeEventArgs(
            sourceRoot, "known-missing", "NTFS", 1, DateTimeOffset.UtcNow));

        StandaloneRuntimeOperationResult result = await runtime.RetryAsync(CancellationToken.None);

        Assert.Equal("failure", GetProperty<string>(result.Status, "view"));
        object failure = GetProperty<object>(result.Status, "failure")!;
        Assert.True(GetProperty<bool>(failure, "canReinitializeCard"));
        Assert.Contains("基线缺失", GetProperty<string>(failure, "what"), StringComparison.Ordinal);
        Assert.False(File.Exists(paths.CardInventoryBaselineFile));
        Assert.False(Directory.Exists(configuration.LocalTargetPath));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private static StandaloneCameraTemplate ResolveCameraTemplate(
        StandaloneConfiguration configuration,
        StandaloneInventoryBaseline? baseline,
        string sourceRoot)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ResolveCameraTemplate", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing camera template resolver.");
        return Assert.IsType<StandaloneCameraTemplate>(
            method.Invoke(null, [configuration, baseline, null, sourceRoot]));
    }

    private static StandaloneConfiguration MultiTemplateConfiguration(Guid firstTemplate, Guid secondTemplate) => new StandaloneConfiguration
    {
        SchemaVersion = 2,
        ApprovedSourceDirectories = [@"XDROOT\Clip"],
        ApprovedExtensions = [".mxf"],
        LocalTargetPath = Path.Combine(Path.GetTempPath(), "AutoCardSync-Local"),
        NasMappedTargetPath = string.Empty,
        TargetMode = StandaloneTargetMode.LocalOnly,
        TargetNamingRule = TargetNamingRule.PreserveRelativePath,
        AutoStartOnLogin = false,
        DefaultCameraTemplateId = firstTemplate,
        CameraTemplates =
        [
            new StandaloneCameraTemplate
            {
                TemplateId = firstTemplate,
                Name = "索尼",
                ApprovedSourceDirectories = [@"XDROOT\Clip"],
                ApprovedExtensions = [".mxf"],
            },
            new StandaloneCameraTemplate
            {
                TemplateId = secondTemplate,
                Name = "佳能",
                ApprovedSourceDirectories = [@"CONTENTS\CLIPS001"],
                ApprovedExtensions = [".mp4"],
            },
        ],
    };

    private static async Task BindCardSnapshotAsync(
        StandaloneDataPaths paths,
        Guid cardId,
        string sourceRoot,
        TaskManifest inventory,
        string fileSystem,
        long capacity)
    {
        FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
        CardIdentityEvidence evidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, sourceDomain, inventory, fileSystem, capacity);
        var resolver = new StandaloneCardIdentityResolver(paths.CardIdentityFile);
        StandaloneCardIdentityResolution bound = await resolver.ReinitializeAsync(
            evidence,
            cardId,
            CancellationToken.None);
        Assert.Equal(cardId, bound.CardInstanceId);
        await resolver.RefreshSafeCompletionEvidenceAsync(
            cardId,
            evidence,
            CancellationToken.None);
    }

    private static StandaloneRuntimeService CreateRuntime(
        StandaloneDataPaths paths,
        AtomicJsonFileStore<StandaloneConfiguration> store) => new(
            new StandaloneConfigurationService(store, new TestLoginAutoStartService()),
            store,
            paths,
            NullLogger<StandaloneRuntimeService>.Instance);

    private static void SetPrivateField(object instance, string name, object? value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException($"Missing private field {name}.");
        field.SetValue(instance, value);
    }

    private static T? GetProperty<T>(object instance, string name)
    {
        PropertyInfo property = instance.GetType().GetProperty(name) ??
            throw new InvalidOperationException($"Missing property {name}.");
        return (T?)property.GetValue(instance);
    }

    private static string GetVolumeKey(VolumeEventArgs volume)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "VolumeKey", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing mounted volume key method.");
        return Assert.IsType<string>(method.Invoke(null, [volume]));
    }

    private static HashSet<string> GetSafeCompletedVolumes(object instance) =>
        Assert.IsType<HashSet<string>>(GetPrivateField(instance, "_safeCompletedVolumeKeys"));

    private static HashSet<string> GetContentWitnessInvalidatedVolumes(object instance) =>
        Assert.IsType<HashSet<string>>(GetPrivateField(instance, "_contentWitnessInvalidatedVolumeKeys"));

    private static object? GetPrivateField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException($"Missing private field {name}.");
        return field.GetValue(instance);
    }
}
