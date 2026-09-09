using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoCardSync.Application.Cards;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Cards;

public sealed record StandaloneInventoryBaselineEntry
{
    public required string RelativePath { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastModifiedUtc { get; init; }
    public required string SourceFileIdentity { get; init; }
    public required string SourceFileIdentityType { get; init; }
}

public sealed record StandaloneInventoryBaseline
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid CardInstanceId { get; init; }
    public Guid? CameraTemplateId { get; init; }
    public required string SourceIdentity { get; init; }
    public required string SelectionPolicyHash { get; init; }
    public required IReadOnlyList<StandaloneInventoryBaselineEntry> Entries { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public Guid? LastCompletedTaskId { get; init; }
    public bool InitializationPending { get; init; }
    public CardIdentityEvidence? InitializationEvidence { get; init; }
}

public sealed record StandaloneInventoryBaselineDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<StandaloneInventoryBaseline> Baselines { get; init; } = [];
    public IReadOnlyList<StandaloneInventoryBaseline> AbandonedInitializations { get; init; } = [];
    public IReadOnlyList<StandaloneInventoryBaseline> ArchivedBaselines { get; init; } = [];
}

public sealed record StandaloneInventoryDelta(
    bool HasBaseline,
    bool SelectionPolicyMatches,
    IReadOnlyList<ManifestEntry> AddedEntries,
    IReadOnlyList<ManifestEntry> ModifiedEntries,
    IReadOnlyList<string> IdentityChangedPaths,
    IReadOnlyList<string> MissingPaths)
{
    public IReadOnlyList<string> ChangedPaths => ModifiedEntries
        .Select(entry => entry.RelativePath)
        .ToArray();

    public IReadOnlyList<ManifestEntry> TransferEntries => AddedEntries
        .Concat(ModifiedEntries)
        .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool CanTransfer => HasBaseline && SelectionPolicyMatches;
}

public sealed class StandaloneInventoryBaselineStore
{
    private readonly AtomicJsonFileStore<StandaloneInventoryBaselineDocument> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StandaloneInventoryBaselineStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _store = new AtomicJsonFileStore<StandaloneInventoryBaselineDocument>(path);
    }

    public async Task<StandaloneInventoryBaseline?> FindAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        StandaloneInventoryBaselineDocument document =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
        EnsureValidDocument(document);
        StandaloneInventoryBaseline[] matches = document.Baselines
            .Where(value => string.Equals(value.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => ValidateAndReturn(matches[0]),
            _ => throw new InvalidDataException("The inventory baseline document contains duplicate source identities."),
        };
    }

    public async Task<IReadOnlyList<StandaloneInventoryBaseline>> FindAllBySourceIdentityAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        StandaloneInventoryBaselineDocument document =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
        EnsureValidDocument(document);
        return document.Baselines
            .Where(value => string.Equals(value.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            .Select(ValidateAndReturn)
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<IReadOnlyList<StandaloneInventoryBaseline>> FindAllBaselinesAsync(
        CancellationToken cancellationToken)
    {
        StandaloneInventoryBaselineDocument document =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
        EnsureValidDocument(document);
        return document.Baselines
            .Select(ValidateAndReturn)
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<IReadOnlyList<StandaloneInventoryBaseline>> FindPendingInitializationsAsync(
        CancellationToken cancellationToken)
    {
        StandaloneInventoryBaselineDocument document =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
        EnsureValidDocument(document);
        return document.Baselines
            .Where(value => value.InitializationPending)
            .Select(ValidateAndReturn)
            .OrderByDescending(value => value.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<StandaloneInventoryBaseline?> FindByCardInstanceIdAsync(
        Guid cardInstanceId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        StandaloneInventoryBaselineDocument document =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
        EnsureValidDocument(document);
        StandaloneInventoryBaseline[] matches = document.Baselines
            .Where(value => value.CardInstanceId == cardInstanceId)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => ValidateAndReturn(matches[0]),
            _ => throw new InvalidDataException("The inventory baseline document contains duplicate card identities."),
        };
    }

    public async Task RebindSourceIdentityAsync(
        Guid cardInstanceId,
        string expectedSourceIdentity,
        string newSourceIdentity,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSourceIdentity);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            StandaloneInventoryBaseline baseline = document.Baselines.SingleOrDefault(value =>
                    value.CardInstanceId == cardInstanceId &&
                    string.Equals(value.SourceIdentity, expectedSourceIdentity, StringComparison.Ordinal)) ??
                throw new InvalidDataException("The expected card baseline is unavailable for source reassociation.");
            StandaloneInventoryBaseline rebound = baseline with
            {
                SourceIdentity = newSourceIdentity,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                // Completion receipts bind the old source identity and cannot prove the current
                // reader endpoint safe. The preserved metadata baseline remains valid for delta
                // detection, but completion status must be rebuilt by a fresh no-op or transfer.
                LastCompletedTaskId = null,
                InitializationPending = false,
                InitializationEvidence = null,
            };
            StandaloneInventoryBaselineDocument updated = document with
            {
                Baselines = document.Baselines
                    .Where(value => value.CardInstanceId != cardInstanceId)
                    .Append(rebound)
                    .ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StandaloneInventoryBaseline>> FindAllForExplicitReinitializationAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FindAllBySourceIdentityAsync(sourceIdentity, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _ = exception;
            if (File.Exists(_store.FilePath))
                CorruptStateFileRecovery.Preserve(_store.FilePath);
            return [];
        }
    }

    public async Task<StandaloneInventoryBaseline?> FindForReinitializationAsync(
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FindAsync(sourceIdentity, cancellationToken);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("duplicate source identities", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Multiple card baselines share this storage endpoint; reinitialization must use the confirmed card identity.",
                exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _ = exception;
            if (File.Exists(_store.FilePath))
                CorruptStateFileRecovery.Preserve(_store.FilePath);
            return null;
        }
    }

    public static string ComputeSelectionPolicyHash(
        IEnumerable<string> approvedDirectories,
        IEnumerable<string> approvedExtensions)
    {
        ArgumentNullException.ThrowIfNull(approvedDirectories);
        ArgumentNullException.ThrowIfNull(approvedExtensions);
        string canonical = string.Join("\n", approvedDirectories
            .Select(value => value.Trim().Replace('\\', '/').Trim('/').ToUpperInvariant())
            .Where(value => value.Length > 0)
            .OrderBy(value => value, StringComparer.Ordinal));
        canonical += "\n--extensions--\n";
        canonical += string.Join("\n", approvedExtensions
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static StandaloneInventoryDelta Compare(
        StandaloneInventoryBaseline? baseline,
        string selectionPolicyHash,
        TaskManifest inventory,
        bool identityChangesRequireTransfer = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionPolicyHash);
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.FrozenAt is null || string.IsNullOrWhiteSpace(inventory.ManifestHash))
            throw new InvalidDataException("The metadata inventory must be frozen before baseline comparison.");

        ManifestEntry[] current = inventory.Entries.Where(entry => !entry.Excluded).ToArray();
        EnsureUniquePaths(current.Select(entry => entry.RelativePath), "metadata inventory");
        if (baseline is null)
            return new(false, false, [], [], [], []);

        EnsureValidBaseline(baseline);
        bool policyMatches = string.Equals(
            baseline.SelectionPolicyHash,
            selectionPolicyHash,
            StringComparison.Ordinal);
        var historical = baseline.Entries.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var observed = current.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var added = new List<ManifestEntry>();
        var modified = new List<ManifestEntry>();
        var identityChanged = new List<string>();

        foreach (ManifestEntry entry in current)
        {
            if (!historical.TryGetValue(entry.RelativePath, out StandaloneInventoryBaselineEntry? existing))
            {
                added.Add(entry);
                continue;
            }
            if (!MetadataMatches(existing, entry))
            {
                modified.Add(entry);
                continue;
            }
            if (!IdentityMatches(existing, entry))
            {
                identityChanged.Add(entry.RelativePath);
                if (identityChangesRequireTransfer)
                    modified.Add(entry);
            }
        }

        string[] missing = historical.Keys
            .Where(path => !observed.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(
            true,
            policyMatches,
            added.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            modified.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            identityChanged.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            missing);
    }

    public Task SaveInitialAsync(
        Guid cardInstanceId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest inventory,
        CancellationToken cancellationToken) =>
        SaveInitialAsync(
            cardInstanceId,
            cameraTemplateId: null,
            sourceIdentity,
            selectionPolicyHash,
            inventory,
            cancellationToken);

    public Task SaveInitialAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest inventory,
        CancellationToken cancellationToken) =>
        SaveAsync(
            cardInstanceId,
            cameraTemplateId,
            sourceIdentity,
            selectionPolicyHash,
            inventory,
            completedTaskId: null,
            preserveMissingEntries: false,
            preserveCompletedTaskId: true,
            allowCardRebind: false,
            cancellationToken);

    public Task AdvanceAfterCompletionAsync(
        Guid cardInstanceId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest currentInventory,
        Guid completedTaskId,
        CancellationToken cancellationToken) =>
        AdvanceAfterCompletionAsync(
            cardInstanceId,
            cameraTemplateId: null,
            sourceIdentity,
            selectionPolicyHash,
            currentInventory,
            completedTaskId,
            cancellationToken);

    public Task AdvanceAfterCompletionAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest currentInventory,
        Guid completedTaskId,
        CancellationToken cancellationToken) =>
        SaveAsync(
            cardInstanceId,
            cameraTemplateId,
            sourceIdentity,
            selectionPolicyHash,
            currentInventory,
            completedTaskId,
            preserveMissingEntries: false,
            preserveCompletedTaskId: true,
            allowCardRebind: false,
            cancellationToken);

    public Task MergeRecoveredCompletionAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest completedInventory,
        Guid completedTaskId,
        CancellationToken cancellationToken) =>
        SaveAsync(
            cardInstanceId,
            cameraTemplateId,
            sourceIdentity,
            selectionPolicyHash,
            completedInventory,
            completedTaskId,
            preserveMissingEntries: true,
            preserveCompletedTaskId: false,
            allowCardRebind: false,
            cancellationToken);
    public Task RefreshObservedAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest currentInventory,
        CancellationToken cancellationToken) =>
        SaveAsync(
            cardInstanceId,
            cameraTemplateId,
            sourceIdentity,
            selectionPolicyHash,
            currentInventory,
            completedTaskId: null,
            preserveMissingEntries: false,
            preserveCompletedTaskId: true,
            allowCardRebind: false,
            cancellationToken);

    public async Task AbandonPendingInitializationsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            StandaloneInventoryBaseline[] pending = document.Baselines
                .Where(value => value.InitializationPending)
                .ToArray();
            if (pending.Length == 0)
                return;
            StandaloneInventoryBaselineDocument updated = document with
            {
                Baselines = document.Baselines
                    .Where(value => !value.InitializationPending)
                    .ToArray(),
                AbandonedInitializations = document.AbandonedInitializations
                    .Concat(pending)
                    .ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReinitializePendingAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        CardIdentityEvidence initializationEvidence,
        bool abandonOtherPending,
        CancellationToken cancellationToken,
        bool archiveExistingCommittedBaseline = false,
        IReadOnlyCollection<Guid>? supersededCardInstanceIds = null)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        if (cameraTemplateId == Guid.Empty)
            throw new ArgumentException("Camera template identity must be non-empty when supplied.", nameof(cameraTemplateId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionPolicyHash);
        ArgumentNullException.ThrowIfNull(initializationEvidence);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            var pending = new StandaloneInventoryBaseline
            {
                SchemaVersion = cameraTemplateId is null ? 1 : 2,
                CardInstanceId = cardInstanceId,
                CameraTemplateId = cameraTemplateId,
                SourceIdentity = sourceIdentity,
                SelectionPolicyHash = selectionPolicyHash,
                Entries = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastCompletedTaskId = null,
                InitializationPending = true,
                InitializationEvidence = initializationEvidence,
            };
            var resetCardIds = new HashSet<Guid>(supersededCardInstanceIds ?? []);
            resetCardIds.Add(cardInstanceId);
            StandaloneInventoryBaseline[] abandoned = abandonOtherPending
                ? document.Baselines
                    .Where(value => value.InitializationPending &&
                        (resetCardIds.Contains(value.CardInstanceId) || value.CardInstanceId != cardInstanceId))
                    .ToArray()
                : document.Baselines
                    .Where(value => value.InitializationPending && resetCardIds.Contains(value.CardInstanceId))
                    .ToArray();
            StandaloneInventoryBaseline[] archivedCommitted = archiveExistingCommittedBaseline
                ? document.Baselines
                    .Where(value => !value.InitializationPending && resetCardIds.Contains(value.CardInstanceId))
                    .ToArray()
                : [];
            StandaloneInventoryBaselineDocument updated = document with
            {
                Baselines = document.Baselines
                    .Where(value => !resetCardIds.Contains(value.CardInstanceId) &&
                        (!abandonOtherPending || !value.InitializationPending))
                    .Append(pending)
                    .ToArray(),
                AbandonedInitializations = document.AbandonedInitializations
                    .Concat(abandoned)
                    .ToArray(),
                ArchivedBaselines = document.ArchivedBaselines
                    .Concat(archivedCommitted)
                    .ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ArchiveCurrentAsync(
        Guid cardInstanceId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            StandaloneInventoryBaseline? current = document.Baselines.SingleOrDefault(value =>
                value.CardInstanceId == cardInstanceId);
            if (current is null || current.InitializationPending)
                return;
            bool alreadyArchived = document.ArchivedBaselines.Any(value =>
                value.CardInstanceId == current.CardInstanceId &&
                value.UpdatedAtUtc == current.UpdatedAtUtc &&
                string.Equals(
                    value.SelectionPolicyHash,
                    current.SelectionPolicyHash,
                    StringComparison.Ordinal));
            if (alreadyArchived)
                return;

            await _store.SaveAsync(document with
            {
                ArchivedBaselines = document.ArchivedBaselines.Append(current).ToArray(),
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitInitializationAsync(
        Guid cardInstanceId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            StandaloneInventoryBaseline pending = document.Baselines.SingleOrDefault(value =>
                    value.CardInstanceId == cardInstanceId) ??
                throw new InvalidDataException("Pending card initialization baseline is unavailable.");
            if (!pending.InitializationPending)
                return;
            StandaloneInventoryBaselineDocument updated = document with
            {
                Baselines = document.Baselines.Select(value => value.CardInstanceId == cardInstanceId
                    ? value with { InitializationPending = false, UpdatedAtUtc = DateTimeOffset.UtcNow }
                    : value).ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ReinitializeAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest currentInventory,
        CancellationToken cancellationToken) =>
        SaveAsync(
            cardInstanceId,
            cameraTemplateId,
            sourceIdentity,
            selectionPolicyHash,
            currentInventory,
            completedTaskId: null,
            preserveMissingEntries: false,
            preserveCompletedTaskId: false,
            allowCardRebind: true,
            cancellationToken);

    private async Task SaveAsync(
        Guid cardInstanceId,
        Guid? cameraTemplateId,
        string sourceIdentity,
        string selectionPolicyHash,
        TaskManifest inventory,
        Guid? completedTaskId,
        bool preserveMissingEntries,
        bool preserveCompletedTaskId,
        bool allowCardRebind,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        if (cameraTemplateId == Guid.Empty)
            throw new ArgumentException("Camera template identity must be non-empty when supplied.", nameof(cameraTemplateId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionPolicyHash);
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.FrozenAt is null || string.IsNullOrWhiteSpace(inventory.ManifestHash))
            throw new InvalidDataException("The metadata inventory must be frozen before baseline persistence.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneInventoryBaselineDocument document =
                await _store.LoadAsync(cancellationToken) ?? new StandaloneInventoryBaselineDocument();
            EnsureValidDocument(document);
            StandaloneInventoryBaseline? previous = document.Baselines.SingleOrDefault(value =>
                value.CardInstanceId == cardInstanceId);
            if (previous is not null &&
                !string.Equals(previous.SourceIdentity, sourceIdentity, StringComparison.Ordinal) &&
                !allowCardRebind)
            {
                throw new InvalidDataException("The card baseline source identity changed without explicit reassociation.");
            }
            Guid? effectiveCameraTemplateId = cameraTemplateId ?? previous?.CameraTemplateId;

            var entries = new Dictionary<string, StandaloneInventoryBaselineEntry>(StringComparer.OrdinalIgnoreCase);
            if (preserveMissingEntries && previous is not null)
            {
                foreach (StandaloneInventoryBaselineEntry entry in previous.Entries)
                    entries.Add(entry.RelativePath, entry);
            }
            foreach (ManifestEntry entry in inventory.Entries.Where(entry => !entry.Excluded))
                entries[entry.RelativePath] = ToBaselineEntry(entry);

            var next = new StandaloneInventoryBaseline
            {
                SchemaVersion = effectiveCameraTemplateId is null ? 1 : 2,
                CardInstanceId = cardInstanceId,
                CameraTemplateId = effectiveCameraTemplateId,
                SourceIdentity = sourceIdentity,
                SelectionPolicyHash = selectionPolicyHash,
                Entries = entries.Values.OrderBy(
                    entry => entry.RelativePath,
                    StringComparer.OrdinalIgnoreCase).ToArray(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastCompletedTaskId = completedTaskId ??
                    (preserveCompletedTaskId ? previous?.LastCompletedTaskId : null),
                InitializationPending = false,
                InitializationEvidence = null,
            };
            StandaloneInventoryBaselineDocument updated = document with
            {
                Baselines = document.Baselines
                    .Where(value => value.CardInstanceId != cardInstanceId)
                    .Append(next)
                    .ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StandaloneInventoryBaselineEntry ToBaselineEntry(ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceFileId) || string.IsNullOrWhiteSpace(entry.SourceFileIdType))
            throw new InvalidDataException($"Metadata inventory file '{entry.RelativePath}' has no frozen file identity.");
        return new StandaloneInventoryBaselineEntry
        {
            RelativePath = entry.RelativePath,
            Length = entry.FileSize,
            LastModifiedUtc = entry.LastModifiedUtc,
            SourceFileIdentity = entry.SourceFileId,
            SourceFileIdentityType = entry.SourceFileIdType,
        };
    }

    private static bool MetadataMatches(StandaloneInventoryBaselineEntry baseline, ManifestEntry current) =>
        baseline.Length == current.FileSize &&
        baseline.LastModifiedUtc == current.LastModifiedUtc;

    private static bool IdentityMatches(StandaloneInventoryBaselineEntry baseline, ManifestEntry current) =>
        string.Equals(baseline.SourceFileIdentity, current.SourceFileId, StringComparison.Ordinal) &&
        string.Equals(baseline.SourceFileIdentityType, current.SourceFileIdType, StringComparison.Ordinal);

    private static StandaloneInventoryBaseline ValidateAndReturn(StandaloneInventoryBaseline baseline)
    {
        EnsureValidBaseline(baseline);
        return baseline;
    }

    private static void EnsureValidDocument(StandaloneInventoryBaselineDocument document)
    {
        if (document.SchemaVersion != 1 || document.Baselines is null ||
            document.AbandonedInitializations is null ||
            document.ArchivedBaselines is null ||
            document.Baselines.Any(baseline => baseline is null) ||
            document.AbandonedInitializations.Any(baseline => baseline is null) ||
            document.ArchivedBaselines.Any(baseline => baseline is null))
        {
            throw new InvalidDataException("The inventory baseline document is invalid.");
        }
        foreach (StandaloneInventoryBaseline baseline in document.Baselines)
            EnsureValidBaseline(baseline);
        foreach (StandaloneInventoryBaseline baseline in document.AbandonedInitializations)
        {
            EnsureValidBaseline(baseline);
            if (!baseline.InitializationPending)
                throw new InvalidDataException("The abandoned initialization archive contains a committed baseline.");
        }
        foreach (StandaloneInventoryBaseline baseline in document.ArchivedBaselines)
        {
            EnsureValidBaseline(baseline);
            if (baseline.InitializationPending)
                throw new InvalidDataException("The committed baseline archive contains a pending initialization.");
        }
        if (document.Baselines
            .Select(value => value.CardInstanceId)
            .Distinct()
            .Count() != document.Baselines.Count)
        {
            throw new InvalidDataException("The inventory baseline document contains duplicate card identities.");
        }
    }

    private static void EnsureValidBaseline(StandaloneInventoryBaseline baseline)
    {
        if (baseline.SchemaVersion is < 1 or > 2 ||
            baseline.CardInstanceId == Guid.Empty ||
            (baseline.SchemaVersion >= 2 && baseline.CameraTemplateId is null) ||
            baseline.CameraTemplateId == Guid.Empty ||
            string.IsNullOrWhiteSpace(baseline.SourceIdentity) ||
            !IsSha256Hex(baseline.SelectionPolicyHash) ||
            baseline.UpdatedAtUtc == default ||
            baseline.LastCompletedTaskId == Guid.Empty ||
            baseline.Entries is null)
        {
            throw new InvalidDataException("The inventory baseline is invalid.");
        }
        if (baseline.Entries.Any(entry => entry is null))
            throw new InvalidDataException("The inventory baseline contains a null file fact.");
        EnsureUniquePaths(baseline.Entries.Select(entry => entry.RelativePath), "inventory baseline");
        if (baseline.Entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.RelativePath) ||
                entry.Length < 0 ||
                entry.LastModifiedUtc == default ||
                string.IsNullOrWhiteSpace(entry.SourceFileIdentity) ||
                string.IsNullOrWhiteSpace(entry.SourceFileIdentityType)))
        {
            throw new InvalidDataException("The inventory baseline contains an invalid file fact.");
        }
    }

    private static bool IsSha256Hex(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void EnsureUniquePaths(IEnumerable<string> paths, string source)
    {
        string[] values = paths.ToArray();
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new InvalidDataException($"The {source} contains duplicate relative paths.");
    }
}
