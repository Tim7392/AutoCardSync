using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Cards;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Tests.Cards;

public sealed class StandaloneInventoryBaselineStoreTests : IDisposable
{
    private static readonly string PolicyA = new('a', 64);
    private static readonly string PolicyB = new('b', 64);
    private static readonly string PolicyEmpty = new('e', 64);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-InventoryBaseline", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task First_observation_persists_metadata_only_and_same_inventory_has_no_delta()
    {
        string path = Path.Combine(_root, "baselines.json");
        var store = new StandaloneInventoryBaselineStore(path);
        Guid cardId = Guid.NewGuid();
        TaskManifest inventory = CreateInventory(("DCIM\\A.mov", 100, "file-a"));

        StandaloneInventoryDelta first = StandaloneInventoryBaselineStore.Compare(
            baseline: null,
            PolicyA,
            inventory);
        Assert.False(first.HasBaseline);
        Assert.Empty(first.AddedEntries);

        await store.SaveInitialAsync(
            cardId,
            "source-a",
            PolicyA,
            inventory,
            CancellationToken.None);
        StandaloneInventoryBaseline? baseline = await store.FindAsync("source-a", CancellationToken.None);
        Assert.NotNull(baseline);
        StandaloneInventoryDelta unchanged = StandaloneInventoryBaselineStore.Compare(
            baseline!,
            PolicyA,
            inventory);

        Assert.True(unchanged.CanTransfer);
        Assert.Empty(unchanged.AddedEntries);
        Assert.Empty(unchanged.ChangedPaths);
        Assert.Empty(unchanged.MissingPaths);
        string json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("sourceHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subsequent_inventory_returns_only_added_entries()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        TaskManifest initial = CreateInventory(("DCIM\\A.mov", 100, "file-a"));
        await store.SaveInitialAsync(
            cardId,
            "source-a",
            PolicyA,
            initial,
            CancellationToken.None);
        TaskManifest observed = CreateInventory(
            ("DCIM\\A.mov", 100, "file-a"),
            ("DCIM\\B.mov", 200, "file-b"));

        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            await store.FindAsync("source-a", CancellationToken.None),
            PolicyA,
            observed);

        ManifestEntry added = Assert.Single(delta.AddedEntries);
        Assert.Equal("DCIM\\B.mov", added.RelativePath);
        Assert.Empty(delta.ChangedPaths);
        Assert.Empty(delta.MissingPaths);
    }

    [Fact]
    public async Task Existing_path_file_identity_churn_is_advisory_when_metadata_is_stable()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        await store.SaveInitialAsync(
            Guid.NewGuid(),
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            await store.FindAsync("source-a", CancellationToken.None),
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "replacement-file-id")));

        Assert.True(delta.CanTransfer);
        Assert.Empty(delta.TransferEntries);
        Assert.Empty(delta.ChangedPaths);
        Assert.Equal("DCIM\\A.mov", Assert.Single(delta.IdentityChangedPaths));
    }

    [Fact]
    public async Task Stable_metadata_with_replaced_identity_is_transferable_when_file_ids_are_reliable()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        await store.SaveInitialAsync(
            Guid.NewGuid(),
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            await store.FindAsync("source-a", CancellationToken.None),
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "replacement-file-id")),
            identityChangesRequireTransfer: true);

        ManifestEntry modified = Assert.Single(delta.ModifiedEntries);
        Assert.Equal("DCIM\\A.mov", modified.RelativePath);
        Assert.Same(modified, Assert.Single(delta.TransferEntries));
        Assert.Equal("DCIM\\A.mov", Assert.Single(delta.IdentityChangedPaths));
    }

    [Fact]
    public async Task Existing_path_metadata_change_is_a_transferable_new_generation()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        await store.SaveInitialAsync(
            Guid.NewGuid(),
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            await store.FindAsync("source-a", CancellationToken.None),
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 101, "replacement-a")));

        Assert.True(delta.CanTransfer);
        ManifestEntry modified = Assert.Single(delta.ModifiedEntries);
        Assert.Equal("DCIM\\A.mov", modified.RelativePath);
        Assert.Equal("DCIM\\A.mov", Assert.Single(delta.ChangedPaths));
        Assert.Same(modified, Assert.Single(delta.TransferEntries));
        Assert.Empty(delta.AddedEntries);
    }

    [Fact]
    public async Task Advance_after_completion_retires_missing_paths_and_keeps_only_current_inventory()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);
        Guid completedTask = Guid.NewGuid();
        TaskManifest current = CreateInventory(("DCIM\\B.mov", 200, "file-b"));

        await store.AdvanceAfterCompletionAsync(
            cardId,
            "source-a",
            PolicyA,
            current,
            completedTask,
            CancellationToken.None);
        StandaloneInventoryBaseline? baseline = await store.FindAsync("source-a", CancellationToken.None);
        Assert.NotNull(baseline);

        Assert.Equal(completedTask, baseline!.LastCompletedTaskId);
        StandaloneInventoryBaselineEntry entry = Assert.Single(baseline.Entries);
        Assert.Equal("DCIM\\B.mov", entry.RelativePath);
    }

    [Fact]
    public async Task Selection_policy_change_is_not_accepted_as_a_transfer_delta()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        TaskManifest inventory = CreateInventory(("DCIM\\A.mov", 100, "file-a"));
        await store.SaveInitialAsync(
            Guid.NewGuid(),
            "source-a",
            PolicyA,
            inventory,
            CancellationToken.None);

        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            await store.FindAsync("source-a", CancellationToken.None),
            PolicyB,
            inventory);

        Assert.False(delta.SelectionPolicyMatches);
        Assert.False(delta.CanTransfer);
    }

    [Fact]
    public async Task Refresh_observed_retires_missing_paths_but_preserves_last_completed_task()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        Guid completedTask = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);
        await store.AdvanceAfterCompletionAsync(
            cardId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            completedTask,
            CancellationToken.None);

        await store.RefreshObservedAsync(
            cardId,
            cameraTemplateId: null,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\B.mov", 200, "file-b")),
            CancellationToken.None);

        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("source-a", CancellationToken.None));
        Assert.Equal(completedTask, baseline.LastCompletedTaskId);
        Assert.Equal("DCIM\\B.mov", Assert.Single(baseline.Entries).RelativePath);
    }

    [Fact]
    public async Task Recovered_completion_merges_only_verified_subset_and_leaves_later_files_pending()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        Guid templateId = Guid.NewGuid();
        Guid completedTaskId = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            templateId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        await store.MergeRecoveredCompletionAsync(
            cardId,
            templateId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\B.mov", 200, "file-b")),
            completedTaskId,
            CancellationToken.None);

        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("source-a", CancellationToken.None));
        Assert.Equal(completedTaskId, baseline.LastCompletedTaskId);
        Assert.Equal(["DCIM\\A.mov", "DCIM\\B.mov"],
            baseline.Entries.Select(entry => entry.RelativePath));
        StandaloneInventoryDelta remaining = StandaloneInventoryBaselineStore.Compare(
            baseline,
            PolicyA,
            CreateInventory(
                ("DCIM\\A.mov", 100, "file-a"),
                ("DCIM\\B.mov", 200, "file-b"),
                ("DCIM\\C.mov", 300, "file-c")));
        ManifestEntry pending = Assert.Single(remaining.TransferEntries);
        Assert.Equal("DCIM\\C.mov", pending.RelativePath);
    }
    [Fact]
    public async Task Empty_card_baseline_makes_the_first_later_file_a_transfer_delta()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "empty-baseline.json"));
        Guid cardId = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            "source-empty",
            PolicyEmpty,
            CreateInventory(),
            CancellationToken.None);

        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("source-empty", CancellationToken.None));
        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            baseline,
            PolicyEmpty,
            CreateInventory((@"DCIM\FIRST.mov", 123, "first-id")));

        Assert.Empty(baseline.Entries);
        Assert.Equal(@"DCIM\FIRST.mov", Assert.Single(delta.TransferEntries).RelativePath);
    }

    [Fact]
    public async Task Invalid_selection_policy_hash_is_corruption_not_a_silent_policy_change()
    {
        string path = Path.Combine(_root, "invalid-policy-baselines.json");
        await new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(path).SaveAsync(
            new StandaloneInventoryBaselineDocument
            {
                Baselines =
                [
                    new StandaloneInventoryBaseline
                    {
                        CardInstanceId = Guid.NewGuid(),
                        SourceIdentity = "source-a",
                        SelectionPolicyHash = "damaged-policy-hash",
                        Entries = [],
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    },
                ],
            },
            CancellationToken.None);
        var store = new StandaloneInventoryBaselineStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.FindAsync("source-a", CancellationToken.None));
        Assert.Null(await store.FindForReinitializationAsync("source-a", CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(_root, "invalid-policy-baselines.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Explicit_reinitialization_preserves_corrupt_baseline_file_and_rebinds_source()
    {
        string path = Path.Combine(_root, "corrupt-baselines.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new StandaloneInventoryBaselineStore(path);

        Assert.Empty(await store.FindAllForExplicitReinitializationAsync(
            "source-a", CancellationToken.None));
        Guid replacementCard = Guid.NewGuid();
        await store.ReinitializeAsync(
            replacementCard,
            cameraTemplateId: null,
            "source-a",
            PolicyA,
            CreateInventory((@"DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("source-a", CancellationToken.None));
        Assert.Equal(replacementCard, baseline.CardInstanceId);
        Assert.Single(Directory.EnumerateFiles(_root, "corrupt-baselines.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Two_card_instances_can_preserve_independent_baselines_on_the_same_storage_endpoint()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "shared-endpoint-baselines.json"));
        Guid firstCard = Guid.NewGuid();
        Guid secondCard = Guid.NewGuid();
        await store.SaveInitialAsync(
            firstCard, "shared-source", PolicyA,
            CreateInventory((@"DCIM\A.mov", 100, "file-a")), CancellationToken.None);
        await store.SaveInitialAsync(
            secondCard, "shared-source", PolicyA,
            CreateInventory((@"DCIM\B.mov", 200, "file-b")), CancellationToken.None);

        IReadOnlyList<StandaloneInventoryBaseline> matches = await store.FindAllBySourceIdentityAsync(
            "shared-source", CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Equal(@"DCIM\A.mov", Assert.Single((await store.FindByCardInstanceIdAsync(
            firstCard, CancellationToken.None))!.Entries).RelativePath);
        Assert.Equal(@"DCIM\B.mov", Assert.Single((await store.FindByCardInstanceIdAsync(
            secondCard, CancellationToken.None))!.Entries).RelativePath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.FindAsync("shared-source", CancellationToken.None));
    }

    [Fact]
    public async Task Reinitialize_clears_previous_completion_binding()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);
        await store.AdvanceAfterCompletionAsync(
            cardId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            Guid.NewGuid(),
            CancellationToken.None);

        await store.ReinitializeAsync(
            cardId,
            cameraTemplateId: null,
            "source-a",
            PolicyB,
            CreateInventory(("DCIM\\B.mov", 200, "file-b")),
            CancellationToken.None);

        StandaloneInventoryBaseline baseline = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("source-a", CancellationToken.None));
        Assert.Null(baseline.LastCompletedTaskId);
        Assert.Equal(PolicyB, baseline.SelectionPolicyHash);
        Assert.Equal("DCIM\\B.mov", Assert.Single(baseline.Entries).RelativePath);
    }

    [Fact]
    public async Task Schema_two_persists_camera_template_identity_and_preserves_it_on_advance()
    {
        var store = new StandaloneInventoryBaselineStore(Path.Combine(_root, "baselines.json"));
        Guid cardId = Guid.NewGuid();
        Guid templateId = Guid.NewGuid();
        await store.SaveInitialAsync(
            cardId,
            templateId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\A.mov", 100, "file-a")),
            CancellationToken.None);

        StandaloneInventoryBaseline? initial = await store.FindAsync("source-a", CancellationToken.None);
        Assert.NotNull(initial);
        Assert.Equal(2, initial!.SchemaVersion);
        Assert.Equal(templateId, initial.CameraTemplateId);

        await store.AdvanceAfterCompletionAsync(
            cardId,
            templateId,
            "source-a",
            PolicyA,
            CreateInventory(("DCIM\\B.mov", 200, "file-b")),
            Guid.NewGuid(),
            CancellationToken.None);
        StandaloneInventoryBaseline? advanced = await store.FindAsync("source-a", CancellationToken.None);
        Assert.Equal(templateId, advanced!.CameraTemplateId);
        Assert.Equal(2, advanced.SchemaVersion);
    }

    [Fact]
    public async Task Rebinding_same_card_to_new_reader_preserves_delta_baseline_and_clears_old_completion_claim()
    {
        var store = new StandaloneInventoryBaselineStore(
            Path.Combine(_root, "rebind-reader-baseline.json"));
        Guid cardId = Guid.NewGuid();
        Guid completedTaskId = Guid.NewGuid();
        TaskManifest inventory = CreateInventory(("DCIM\existing.mov", 100, "file-a"));
        await store.SaveInitialAsync(
            cardId, "reader-old", PolicyA, inventory, CancellationToken.None);
        await store.AdvanceAfterCompletionAsync(
            cardId, "reader-old", PolicyA, inventory, completedTaskId, CancellationToken.None);

        await store.RebindSourceIdentityAsync(
            cardId, "reader-old", "reader-new", CancellationToken.None);

        Assert.Null(await store.FindAsync("reader-old", CancellationToken.None));
        StandaloneInventoryBaseline rebound = Assert.IsType<StandaloneInventoryBaseline>(
            await store.FindAsync("reader-new", CancellationToken.None));
        Assert.Equal(cardId, rebound.CardInstanceId);
        Assert.Single(rebound.Entries);
        Assert.Null(rebound.LastCompletedTaskId);
    }

    private static TaskManifest CreateInventory(params (string Path, long Length, string FileId)[] files)
    {
        var manifest = new TaskManifest(Guid.NewGuid()) { FilterRuleVersion = "metadata-only" };
        DateTimeOffset timestamp = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        foreach ((string path, long length, string fileId) in files)
        {
            manifest.AddEntry(new ManifestEntry
            {
                RelativePath = path,
                FileSize = length,
                LastModifiedUtc = timestamp,
                SourceFileId = fileId,
                SourceFileIdType = "test",
                SourceHash = null,
            });
        }
        manifest.Freeze();
        return manifest;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
