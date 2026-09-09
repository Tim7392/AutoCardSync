using System.Text.Json;
using AutoCardSync.Application.Cards;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Cards;

public sealed record StandaloneCardIdentityBinding
{
    public required Guid CardInstanceId { get; init; }
    public required CardIdentityEvidence Evidence { get; init; }
    public CardIdentityEvidence? SafeCompletionEvidence { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
}

public sealed record StandaloneCardIdentityMap
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<StandaloneCardIdentityBinding> Bindings { get; init; } = [];
    public IReadOnlyList<StandaloneCardIdentityBinding> ArchivedBindings { get; init; } = [];
}

public sealed record StandaloneCardIdentityResolution(
    Guid? CardInstanceId,
    string Decision,
    bool NeedsConfirmation,
    IReadOnlyDictionary<string, string> EvidenceSummary);

public sealed class StandaloneCardIdentityResolver
{
    private readonly AtomicJsonFileStore<StandaloneCardIdentityMap> _store;
    private readonly CardIdentityEvaluator _evaluator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StandaloneCardIdentityResolver(
        string mappingPath,
        CardIdentityEvaluator? evaluator = null)
    {
        _store = new AtomicJsonFileStore<StandaloneCardIdentityMap>(mappingPath);
        _evaluator = evaluator ?? new CardIdentityEvaluator();
    }

    public async Task EnsureReadableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await LoadValidatedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StandaloneCardIdentityBinding>> ListBindingsAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            return map.Bindings
                .OrderBy(binding => binding.CardInstanceId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneCardIdentityBinding?> FindBindingAsync(
        Guid cardInstanceId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            return map.Bindings.SingleOrDefault(value => value.CardInstanceId == cardInstanceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshSafeCompletionEvidenceAsync(
        Guid cardInstanceId,
        CardIdentityEvidence observed,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        ValidateObserved(observed);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            StandaloneCardIdentityBinding existing = map.Bindings.SingleOrDefault(
                value => value.CardInstanceId == cardInstanceId) ??
                throw new InvalidDataException(
                    "素材卡历史基线缺少对应的身份绑定，不能确认当前安全状态。");
            StandaloneCardIdentityMap updated = map with
            {
                Bindings = map.Bindings.Select(value => value.CardInstanceId == cardInstanceId
                    ? value with
                    {
                        SafeCompletionEvidence = observed,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                    }
                    : value).ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task<StandaloneCardIdentityResolution> ResolveAsync(
        CardIdentityEvidence observed,
        CancellationToken cancellationToken)
    {
        ValidateObserved(observed);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            IReadOnlyList<(string CardInstanceId, CardIdentityEvidence Evidence)> candidates = map.Bindings
                .Select(binding => (binding.CardInstanceId.ToString("D"), binding.Evidence))
                .ToArray();
            CardIdentityResult evaluation = _evaluator.Evaluate(observed, candidates);

            if (evaluation.NeedsConfirmation)
            {
                Guid? candidateId = Guid.TryParse(evaluation.MatchedCardInstanceId, out Guid parsed) && parsed != Guid.Empty
                    ? parsed
                    : FindUniqueWeakVolumeCandidate(observed, map);
                return new(
                    candidateId,
                    evaluation.Decision,
                    true,
                    evaluation.EvidenceSummary);
            }

            if (string.Equals(evaluation.Decision, "AutoAssociate", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(evaluation.MatchedCardInstanceId, out Guid matched) || matched == Guid.Empty)
                    throw new InvalidDataException("The identity evaluator returned an invalid card binding.");

                StandaloneCardIdentityBinding? existing = map.Bindings.SingleOrDefault(
                    binding => binding.CardInstanceId == matched);
                if (existing is null)
                    throw new InvalidDataException("The matched card binding is not present in the atomic identity map.");

                StandaloneCardIdentityMap updated = map with
                {
                    Bindings = map.Bindings
                        .Select(binding => binding.CardInstanceId == matched
                            ? binding with { LastSeenUtc = DateTimeOffset.UtcNow }
                            : binding)
                        .ToArray(),
                };
                await _store.SaveAsync(updated, cancellationToken);
                return new(matched, evaluation.Decision, false, evaluation.EvidenceSummary);
            }

            if (!string.Equals(evaluation.Decision, "NewCard", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported card identity decision '{evaluation.Decision}'.");

            Guid createdId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var created = new StandaloneCardIdentityBinding
            {
                CardInstanceId = createdId,
                Evidence = observed,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            StandaloneCardIdentityMap next = map with
            {
                Bindings = map.Bindings.Append(created).ToArray(),
            };
            await _store.SaveAsync(next, cancellationToken);
            return new(createdId, evaluation.Decision, false, evaluation.EvidenceSummary);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneCardIdentityResolution> ResolveExpectedAsync(
        Guid expectedCardInstanceId,
        CardIdentityEvidence observed,
        CancellationToken cancellationToken,
        bool baselineContinuityProven = false,
        bool updateLastSeen = true,
        bool allowContentChangeOnProvenMount = false,
        bool allowVerifiedSafeCompletionEvidence = false)
    {
        if (expectedCardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(expectedCardInstanceId));
        ValidateObserved(observed);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            StandaloneCardIdentityBinding binding = map.Bindings.SingleOrDefault(
                value => value.CardInstanceId == expectedCardInstanceId) ??
                throw new InvalidDataException(
                    "素材卡历史基线缺少对应的身份绑定。系统不会依据共享存储端点直接套用旧基线。");
            CardIdentityResult evaluation = _evaluator.Evaluate(
                observed,
                [(expectedCardInstanceId.ToString("D"), binding.Evidence)]);
            bool evaluatorAutoAssociates = !evaluation.NeedsConfirmation &&
                string.Equals(evaluation.Decision, "AutoAssociate", StringComparison.Ordinal) &&
                (baselineContinuityProven || HasStrongExpectedContinuity(binding.Evidence, observed));
            string expectedVolumeSummaryKey = $"candidate:{expectedCardInstanceId:D}:V";
            bool baselineContinuityAutoAssociates = baselineContinuityProven &&
                evaluation.NeedsConfirmation &&
                evaluation.EvidenceSummary.ContainsKey("weak") &&
                evaluation.EvidenceSummary.TryGetValue(expectedVolumeSummaryKey, out string? volumeSummary) &&
                string.Equals(volumeSummary, "match", StringComparison.Ordinal);
            string expectedFingerprintConflictKey =
                $"candidate:{expectedCardInstanceId:D}:fingerprint_conflict";
            bool provenMountContentChangeAutoAssociates = allowContentChangeOnProvenMount &&
                evaluation.NeedsConfirmation &&
                evaluation.EvidenceSummary.Count == 1 &&
                evaluation.EvidenceSummary.ContainsKey(expectedFingerprintConflictKey);
            bool verifiedSafeCompletionAutoAssociates = allowVerifiedSafeCompletionEvidence &&
                evaluation.NeedsConfirmation &&
                evaluation.EvidenceSummary.Count == 1 &&
                evaluation.EvidenceSummary.ContainsKey(expectedFingerprintConflictKey) &&
                binding.SafeCompletionEvidence is not null &&
                SafeCompletionEvidenceMatches(binding.SafeCompletionEvidence, observed);
            if (evaluatorAutoAssociates ||
                baselineContinuityAutoAssociates ||
                provenMountContentChangeAutoAssociates ||
                verifiedSafeCompletionAutoAssociates)
            {
                if (updateLastSeen)
                {
                    StandaloneCardIdentityMap updated = map with
                    {
                        Bindings = map.Bindings.Select(value => value.CardInstanceId == expectedCardInstanceId
                            ? value with { LastSeenUtc = DateTimeOffset.UtcNow }
                            : value).ToArray(),
                    };
                    await _store.SaveAsync(updated, cancellationToken);
                }
                return new(
                    expectedCardInstanceId,
                    verifiedSafeCompletionAutoAssociates
                        ? "AutoAssociateVerifiedSafeCompletion"
                        : provenMountContentChangeAutoAssociates
                            ? "AutoAssociateProvenMountContentChange"
                            : baselineContinuityAutoAssociates
                                ? "AutoAssociateBaselineContinuity"
                                : evaluation.Decision,
                    false,
                    evaluation.EvidenceSummary);
            }

            return new(
                expectedCardInstanceId,
                "NeedsIdentityConfirmation",
                true,
                evaluation.EvidenceSummary);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneCardIdentityResolution> RegisterNewAsync(
        CardIdentityEvidence observed,
        CancellationToken cancellationToken)
    {
        ValidateObserved(observed);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            Guid createdId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StandaloneCardIdentityMap updated = map with
            {
                Bindings = map.Bindings.Append(new StandaloneCardIdentityBinding
                {
                    CardInstanceId = createdId,
                    Evidence = observed,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                }).ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
            return new(
                createdId,
                "UserConfirmedNewCard",
                false,
                new Dictionary<string, string> { ["confirmation"] = "user_confirmed_new_card" });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureExpectedMountedSnapshotAsync(
        Guid expectedCardInstanceId,
        string fileSystem,
        long capacity,
        CancellationToken cancellationToken)
    {
        if (expectedCardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(expectedCardInstanceId));
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map = await LoadValidatedAsync(cancellationToken);
            StandaloneCardIdentityBinding? binding = map.Bindings.SingleOrDefault(
                value => value.CardInstanceId == expectedCardInstanceId);
            if (binding is null)
            {
                throw new InvalidDataException(
                    "素材卡基线缺少对应的身份绑定。为避免把另一张卡误用旧基线，已停止并等待确认重新初始化。");
            }

            EnsureMountedSnapshotCompatible(binding.Evidence, fileSystem, capacity);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneCardIdentityResolution> ReinitializeAsync(
        CardIdentityEvidence observed,
        Guid? preferredCardInstanceId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? supersededCardInstanceIds = null)
    {
        ValidateObserved(observed);
        if (preferredCardInstanceId == Guid.Empty)
            throw new ArgumentException("Preferred card identity must be non-empty when supplied.", nameof(preferredCardInstanceId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneCardIdentityMap map;
            try
            {
                map = await LoadValidatedAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                _ = exception;
                if (File.Exists(_store.FilePath))
                    CorruptStateFileRecovery.Preserve(_store.FilePath);
                map = new StandaloneCardIdentityMap();
            }

            StandaloneCardIdentityBinding? preferred = preferredCardInstanceId is Guid preferredId
                ? map.Bindings.SingleOrDefault(value => value.CardInstanceId == preferredId)
                : null;
            // Reinitialization is an explicit user-confirmed replacement of the active
            // evidence boundary. Preserve the chosen logical card identity even when the
            // old capacity/filesystem snapshot is exactly what became stale.
            bool preservePreferred = preferredCardInstanceId is Guid;
            Guid selectedId = preservePreferred
                ? preferredCardInstanceId!.Value
                : Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StandaloneCardIdentityBinding replacement = new()
            {
                CardInstanceId = selectedId,
                Evidence = observed,
                FirstSeenUtc = preferred?.FirstSeenUtc ?? now,
                LastSeenUtc = now,
            };
            var resetCardIds = new HashSet<Guid>(supersededCardInstanceIds ?? []);
            resetCardIds.Add(selectedId);
            StandaloneCardIdentityBinding[] archived = map.Bindings
                .Where(value => resetCardIds.Contains(value.CardInstanceId))
                .ToArray();
            StandaloneCardIdentityMap updated = map with
            {
                Bindings = map.Bindings
                    .Where(value => !resetCardIds.Contains(value.CardInstanceId))
                    .Append(replacement)
                    .ToArray(),
                ArchivedBindings = map.ArchivedBindings.Concat(archived).ToArray(),
            };
            await _store.SaveAsync(updated, cancellationToken);
            return new(
                selectedId,
                preservePreferred ? "UserConfirmedCardStateRepair" : "UserConfirmedNewCard",
                false,
                new Dictionary<string, string>
                {
                    ["confirmation"] = preservePreferred
                        ? "user_confirmed_card_state_repair"
                        : "user_confirmed_new_card",
                });
        }
        finally
        {
            _gate.Release();
        }
    }

    private Guid? FindUniqueWeakVolumeCandidate(
        CardIdentityEvidence observed,
        StandaloneCardIdentityMap map)
    {
        StandaloneCardIdentityBinding[] weakMatches = map.Bindings
            .Where(binding =>
            {
                CardIdentityResult single = _evaluator.Evaluate(
                    observed,
                    [(binding.CardInstanceId.ToString("D"), binding.Evidence)]);
                return single.NeedsConfirmation && single.EvidenceSummary.ContainsKey("weak");
            })
            .ToArray();
        return weakMatches.Length == 1 ? weakMatches[0].CardInstanceId : null;
    }

    private static bool SafeCompletionEvidenceMatches(
        CardIdentityEvidence expected,
        CardIdentityEvidence observed) =>
        !string.IsNullOrWhiteSpace(expected.SampleFingerprint) &&
        string.Equals(expected.SampleFingerprint, observed.SampleFingerprint, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(expected.RootDirectoryHash) &&
        string.Equals(expected.RootDirectoryHash, observed.RootDirectoryHash, StringComparison.Ordinal);

    public static bool HasStrongExpectedContinuity(
        CardIdentityEvidence expected,
        CardIdentityEvidence observed)
    {
        bool deviceMatches =
            (!string.IsNullOrWhiteSpace(expected.DeviceSerialNumber) &&
             string.Equals(expected.DeviceSerialNumber, observed.DeviceSerialNumber, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(expected.PnpDeviceInstanceId) &&
             string.Equals(expected.PnpDeviceInstanceId, observed.PnpDeviceInstanceId,
                 StringComparison.OrdinalIgnoreCase));
        bool historicalBindingMatches =
            !string.IsNullOrWhiteSpace(expected.HistoricalCardInstanceId) &&
            string.Equals(expected.HistoricalCardInstanceId, observed.HistoricalCardInstanceId,
                StringComparison.Ordinal);
        bool exactNonEmptyContentMatches =
            !string.IsNullOrWhiteSpace(expected.SampleFingerprint) &&
            string.Equals(expected.SampleFingerprint, observed.SampleFingerprint, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(expected.RootDirectoryHash) &&
            string.Equals(expected.RootDirectoryHash, observed.RootDirectoryHash, StringComparison.Ordinal);
        return deviceMatches || historicalBindingMatches || exactNonEmptyContentMatches;
    }

    private async Task<StandaloneCardIdentityMap> LoadValidatedAsync(CancellationToken cancellationToken)
    {
        StandaloneCardIdentityMap map =
            await _store.LoadAsync(cancellationToken) ?? new StandaloneCardIdentityMap();
        ValidateMap(map);
        return map;
    }

    private static void ValidateMap(StandaloneCardIdentityMap map)
    {
        if (map.SchemaVersion != 1 || map.Bindings is null || map.ArchivedBindings is null ||
            map.Bindings.Any(value =>
                value is null ||
                value.CardInstanceId == Guid.Empty ||
                value.Evidence is null ||
                value.FirstSeenUtc == default ||
                value.LastSeenUtc == default ||
                value.LastSeenUtc < value.FirstSeenUtc) ||
            map.ArchivedBindings.Any(value =>
                value is null ||
                value.CardInstanceId == Guid.Empty ||
                value.Evidence is null ||
                value.FirstSeenUtc == default ||
                value.LastSeenUtc == default ||
                value.LastSeenUtc < value.FirstSeenUtc) ||
            map.Bindings.Select(value => value.CardInstanceId).Distinct().Count() != map.Bindings.Count)
        {
            throw new InvalidDataException("The card identity map is invalid.");
        }
        foreach (StandaloneCardIdentityBinding binding in map.Bindings)
        {
            ValidateObserved(binding.Evidence);
            if (binding.SafeCompletionEvidence is not null)
                ValidateObserved(binding.SafeCompletionEvidence);
        }
        foreach (StandaloneCardIdentityBinding binding in map.ArchivedBindings)
        {
            ValidateObserved(binding.Evidence);
            if (binding.SafeCompletionEvidence is not null)
                ValidateObserved(binding.SafeCompletionEvidence);
        }
    }

    private static void EnsureMountedSnapshotCompatible(
        CardIdentityEvidence expected,
        string fileSystem,
        long capacity)
    {
        if (!MountedSnapshotCompatible(expected, fileSystem, capacity))
        {
            throw new InvalidDataException(
                "当前介质的文件系统或容量与已绑定素材卡不一致。可能已更换为另一张卡；已停止使用旧基线，请确认重新初始化当前素材卡。");
        }
    }

    private static bool MountedSnapshotCompatible(
        CardIdentityEvidence expected,
        string fileSystem,
        long capacity)
    {
        bool fileSystemMatches = string.IsNullOrWhiteSpace(expected.FileSystem) ||
            string.IsNullOrWhiteSpace(fileSystem) ||
            string.Equals(expected.FileSystem, fileSystem, StringComparison.OrdinalIgnoreCase);
        bool capacityMatches = expected.Capacity is null or <= 0 || capacity <= 0 ||
            expected.Capacity.Value == capacity;
        return fileSystemMatches && capacityMatches;
    }

    private static void ValidateObserved(CardIdentityEvidence observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        bool hasVolumeEvidence = observed.VolumeGuid.HasValue ||
            observed.VolumeSerialNumber.HasValue ||
            observed.PartitionStartLba.HasValue ||
            observed.PartitionLength.HasValue ||
            !string.IsNullOrWhiteSpace(observed.FileSystem) ||
            observed.Capacity.HasValue;
        if (!hasVolumeEvidence)
            throw new InvalidDataException("Observed card identity does not contain volume evidence.");
        if (observed.Capacity is < 0 || observed.PartitionLength is < 0 || observed.PartitionStartLba is < 0)
            throw new InvalidDataException("Observed card identity contains invalid numeric evidence.");
    }
}
