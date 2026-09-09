using System.Text.Json;
using AutoCardSync.Application.Cards;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Cards;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Tests.Cards;

public sealed class StandaloneCardIdentityResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AutoCardSync-V1-CardIdentity", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Card_evidence_treats_zero_volume_serial_as_unavailable()
    {
        Directory.CreateDirectory(_root);
        Guid volumeGuid = Guid.NewGuid();
        var manifest = new TaskManifest(Guid.NewGuid()) { FilterRuleVersion = "metadata-only" };
        var sourceDomain = new FaultDomainInfo(
            "local:test",
            "Local",
            @"\\.\PHYSICALDRIVE2",
            $@"\\?\Volume{{{volumeGuid:D}}}",
            null,
            string.Empty,
            StorageIdentity: $@"local-v1:\\.\physicaldrive2:\\?\volume{{{volumeGuid:D}}}:0000000000000000",
            VolumeSerialNumber: 0,
            PhysicalEndpoint: $@"\\?\Volume{{{volumeGuid:D}}}");

        CardIdentityEvidence evidence = StandaloneCardEvidenceBuilder.Build(
            _root,
            sourceDomain,
            manifest);

        Assert.Equal(volumeGuid, evidence.VolumeGuid);
        Assert.Null(evidence.VolumeSerialNumber);
        Assert.True(evidence.Capacity > 0);
    }

    [Fact]
    public async Task New_card_is_persisted_and_strongly_matching_card_is_reused()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        CardIdentityEvidence evidence = StrongEvidence();

        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(evidence, CancellationToken.None);
        StandaloneCardIdentityResolution reused = await resolver.ResolveAsync(evidence, CancellationToken.None);

        Assert.False(created.NeedsConfirmation);
        Assert.Equal("NewCard", created.Decision);
        Assert.NotNull(created.CardInstanceId);
        Assert.False(reused.NeedsConfirmation);
        Assert.Equal("AutoAssociate", reused.Decision);
        Assert.Equal(created.CardInstanceId, reused.CardInstanceId);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Baseline_object_continuity_accepts_a_new_directory_state_without_swallowing_same_metadata_content_conflicts()
    {
        Directory.CreateDirectory(_root);
        var resolver = new StandaloneCardIdentityResolver(Path.Combine(_root, "cards.json"));
        CardIdentityEvidence initial = StrongEvidence() with
        {
            PnpDeviceInstanceId = null,
            DeviceSerialNumber = null,
        };
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(
            initial, CancellationToken.None);
        CardIdentityEvidence withNewFile = initial with
        {
            RootDirectoryHash = new string('c', 64),
            SampleFingerprint = new string('d', 64),
        };

        StandaloneCardIdentityResolution withoutBaselineContinuity = await resolver.ResolveExpectedAsync(
            created.CardInstanceId!.Value,
            withNewFile,
            CancellationToken.None,
            baselineContinuityProven: false,
            updateLastSeen: false);
        StandaloneCardIdentityResolution withBaselineContinuity = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            withNewFile,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false);
        CardIdentityEvidence sameMetadataContentConflict = initial with
        {
            SampleFingerprint = new string('e', 64),
        };
        StandaloneCardIdentityResolution conflict = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            sameMetadataContentConflict,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false);
        StandaloneCardIdentityResolution provenMountContentChange = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            sameMetadataContentConflict,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false,
            allowContentChangeOnProvenMount: true);

        Assert.True(withoutBaselineContinuity.NeedsConfirmation);
        Assert.False(withBaselineContinuity.NeedsConfirmation);
        Assert.Equal("AutoAssociateBaselineContinuity", withBaselineContinuity.Decision);
        Assert.True(conflict.NeedsConfirmation);
        Assert.False(provenMountContentChange.NeedsConfirmation);
        Assert.Equal("AutoAssociateProvenMountContentChange", provenMountContentChange.Decision);
    }
    [Fact]
    public async Task Verified_safe_completion_evidence_releases_only_the_matching_fingerprint_conflict()
    {
        Directory.CreateDirectory(_root);
        var resolver = new StandaloneCardIdentityResolver(Path.Combine(_root, "cards.json"));
        CardIdentityEvidence identityEvidence = StrongEvidence() with
        {
            PnpDeviceInstanceId = null,
            DeviceSerialNumber = null,
        };
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(
            identityEvidence, CancellationToken.None);
        CardIdentityEvidence completedEvidence = identityEvidence with
        {
            SampleFingerprint = new string('c', 64),
        };
        await resolver.RefreshSafeCompletionEvidenceAsync(
            created.CardInstanceId!.Value,
            completedEvidence,
            CancellationToken.None);

        StandaloneCardIdentityResolution withoutVerifiedReceipt = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            completedEvidence,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false);
        StandaloneCardIdentityResolution withVerifiedReceipt = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            completedEvidence,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false,
            allowVerifiedSafeCompletionEvidence: true);
        CardIdentityEvidence differentContent = completedEvidence with
        {
            SampleFingerprint = new string('d', 64),
        };
        StandaloneCardIdentityResolution mismatch = await resolver.ResolveExpectedAsync(
            created.CardInstanceId.Value,
            differentContent,
            CancellationToken.None,
            baselineContinuityProven: true,
            updateLastSeen: false,
            allowVerifiedSafeCompletionEvidence: true);

        Assert.True(withoutVerifiedReceipt.NeedsConfirmation);
        Assert.False(withVerifiedReceipt.NeedsConfirmation);
        Assert.Equal("AutoAssociateVerifiedSafeCompletion", withVerifiedReceipt.Decision);
        Assert.True(mismatch.NeedsConfirmation);
    }

    [Fact]
    public async Task Safe_completion_witness_updates_without_rewriting_identity_evidence()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        CardIdentityEvidence identityEvidence = StrongEvidence();
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(
            identityEvidence, CancellationToken.None);
        CardIdentityEvidence completionEvidence = identityEvidence with
        {
            RootDirectoryHash = new string('c', 64),
            SampleFingerprint = new string('d', 64),
        };

        await resolver.RefreshSafeCompletionEvidenceAsync(
            created.CardInstanceId!.Value,
            completionEvidence,
            CancellationToken.None);
        StandaloneCardIdentityBinding binding = Assert.IsType<StandaloneCardIdentityBinding>(
            await resolver.FindBindingAsync(created.CardInstanceId.Value, CancellationToken.None));

        Assert.Equal(identityEvidence, binding.Evidence);
        Assert.Equal(completionEvidence, binding.SafeCompletionEvidence);
    }
    [Fact]
    public async Task Weak_volume_only_match_requires_confirmation_and_does_not_add_binding()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        CardIdentityEvidence first = StrongEvidence();
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(first, CancellationToken.None);
        CardIdentityEvidence weak = first with
        {
            RootDirectoryHash = null,
            SampleFingerprint = null,
            HistoricalCardInstanceId = null,
            PnpDeviceInstanceId = null,
            DeviceSerialNumber = null,
        };

        StandaloneCardIdentityResolution result = await resolver.ResolveAsync(weak, CancellationToken.None);
        StandaloneCardIdentityMap map = JsonSerializer.Deserialize<StandaloneCardIdentityMap>(
            await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.NotNull(created.CardInstanceId);
        Assert.True(result.NeedsConfirmation);
        Assert.Equal(created.CardInstanceId, result.CardInstanceId);
        Assert.Single(map.Bindings);
    }

    [Fact]
    public async Task Explicit_new_card_confirmation_adds_a_distinct_binding_without_rewriting_existing_evidence()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        CardIdentityEvidence first = StrongEvidence();
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(first, CancellationToken.None);
        CardIdentityEvidence weak = first with
        {
            RootDirectoryHash = null,
            SampleFingerprint = null,
            HistoricalCardInstanceId = null,
            PnpDeviceInstanceId = null,
            DeviceSerialNumber = null,
        };

        StandaloneCardIdentityResolution pending = await resolver.ResolveAsync(weak, CancellationToken.None);
        StandaloneCardIdentityResolution confirmed = await resolver.RegisterNewAsync(weak, CancellationToken.None);
        StandaloneCardIdentityMap map = JsonSerializer.Deserialize<StandaloneCardIdentityMap>(
            await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(pending.NeedsConfirmation);
        Assert.Equal("UserConfirmedNewCard", confirmed.Decision);
        Assert.False(confirmed.NeedsConfirmation);
        Assert.NotNull(confirmed.CardInstanceId);
        Assert.NotEqual(created.CardInstanceId, confirmed.CardInstanceId);
        Assert.Equal(2, map.Bindings.Count);
        Assert.Equal(first, map.Bindings.Single(binding => binding.CardInstanceId == created.CardInstanceId).Evidence);
        Assert.Equal(weak, map.Bindings.Single(binding => binding.CardInstanceId == confirmed.CardInstanceId).Evidence);
    }

    [Fact]
    public async Task Missing_expected_identity_binding_fails_closed_instead_of_trusting_an_orphaned_baseline()
    {
        Directory.CreateDirectory(_root);
        var resolver = new StandaloneCardIdentityResolver(Path.Combine(_root, "cards.json"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.EnsureExpectedMountedSnapshotAsync(
                Guid.NewGuid(), "exFAT", 64L * 1024 * 1024 * 1024, CancellationToken.None));

        Assert.Contains("缺少对应的身份绑定", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expected_card_snapshot_rejects_reused_volume_with_different_capacity()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        CardIdentityEvidence evidence = StrongEvidence();
        StandaloneCardIdentityResolution created = await resolver.ResolveAsync(
            evidence, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.EnsureExpectedMountedSnapshotAsync(
                created.CardInstanceId!.Value,
                evidence.FileSystem!,
                evidence.Capacity!.Value * 2,
                CancellationToken.None));
    }

    [Fact]
    public async Task Confirmed_reinitialization_preserves_corrupt_identity_map_and_creates_usable_binding()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var resolver = new StandaloneCardIdentityResolver(path);

        StandaloneCardIdentityResolution repaired = await resolver.ReinitializeAsync(
            StrongEvidence(), preferredCardInstanceId: null, CancellationToken.None);
        StandaloneCardIdentityMap map = JsonSerializer.Deserialize<StandaloneCardIdentityMap>(
            await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.NotNull(repaired.CardInstanceId);
        Assert.Single(map.Bindings);
        Assert.Equal(repaired.CardInstanceId, map.Bindings[0].CardInstanceId);
        Assert.Single(Directory.EnumerateFiles(_root, "cards.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Confirmed_reinitialization_archives_the_superseded_binding_and_keeps_one_active_identity()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "reset-cards.json");
        var resolver = new StandaloneCardIdentityResolver(path);
        StandaloneCardIdentityResolution original = await resolver.ResolveAsync(
            StrongEvidence(), CancellationToken.None);
        Guid cardId = original.CardInstanceId!.Value;
        CardIdentityEvidence replacement = StrongEvidence() with
        {
            Capacity = StrongEvidence().Capacity!.Value * 2,
            RootDirectoryHash = new string('e', 64),
            SampleFingerprint = new string('f', 64),
        };

        StandaloneCardIdentityResolution reset = await resolver.ReinitializeAsync(
            replacement, cardId, CancellationToken.None, [cardId]);
        StandaloneCardIdentityMap map = Assert.IsType<StandaloneCardIdentityMap>(
            await new AtomicJsonFileStore<StandaloneCardIdentityMap>(path).LoadAsync(CancellationToken.None));

        Assert.Equal(cardId, reset.CardInstanceId);
        Assert.Equal(cardId, Assert.Single(map.Bindings).CardInstanceId);
        Assert.Equal(replacement.Capacity, map.Bindings[0].Evidence.Capacity);
        Assert.Equal(cardId, Assert.Single(map.ArchivedBindings).CardInstanceId);
    }

    [Fact]
    public async Task Identity_map_with_binding_that_has_no_volume_evidence_is_rejected_and_repairable()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
            new StandaloneCardIdentityMap
            {
                Bindings =
                [
                    new StandaloneCardIdentityBinding
                    {
                        CardInstanceId = Guid.NewGuid(),
                        Evidence = new CardIdentityEvidence(),
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    },
                ],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var resolver = new StandaloneCardIdentityResolver(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveAsync(StrongEvidence(), CancellationToken.None));
        StandaloneCardIdentityResolution repaired = await resolver.ReinitializeAsync(
            StrongEvidence(), preferredCardInstanceId: null, CancellationToken.None);

        Assert.NotNull(repaired.CardInstanceId);
        Assert.Single(Directory.EnumerateFiles(_root, "cards.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Corrupted_identity_map_fails_closed()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cards.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var resolver = new StandaloneCardIdentityResolver(path);

        await Assert.ThrowsAsync<JsonException>(() =>
            resolver.ResolveAsync(StrongEvidence(), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CardIdentityEvidence StrongEvidence() => new()
    {
        VolumeGuid = Guid.NewGuid(),
        VolumeSerialNumber = 1234,
        FileSystem = "exFAT",
        Capacity = 64L * 1024 * 1024 * 1024,
        PnpDeviceInstanceId = "USBSTOR\\DISK&VEN_TEST",
        DeviceSerialNumber = "SERIAL-001",
        RootDirectoryHash = new string('a', 64),
        SampleFingerprint = new string('b', 64),
    };
}
