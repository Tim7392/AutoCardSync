using System.Globalization;
using System.IO;
using System.Text.Json;
using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Application.Cards;
using AutoCardSync.Application.Copying;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Cards;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Safety;
using AutoCardSync.Standalone.Core.Storage;
using AutoCardSync.Standalone.Core.Transfer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoCardSync.Standalone.Services;

public sealed record StandaloneRuntimeOperationResult(
    object Status,
    string Message,
    bool ManagedCardInitializationCommitted = false);

public sealed class StandaloneRuntimeService : IHostedService, IAsyncDisposable
{
    private readonly StandaloneConfigurationService _configurationService;
    private readonly AtomicJsonFileStore<StandaloneConfiguration> _configurationStore;
    private readonly StandaloneDataPaths _paths;
    private readonly ILogger<StandaloneRuntimeService> _logger;
    private readonly VolumeNotificationListener _listener;
    private readonly VolumeReconciler _reconciler;
    private readonly ExternalSourceVolumeClassifier _sourceVolumeClassifier;
    private readonly StandaloneTransferStatusAggregator _transferStatus = new();
    private readonly SemaphoreSlim _mountedVolumeScan = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _taskCts;
    private Task? _activeTask;
    private Guid? _activeOperationId;
    private object _status = WaitingStatus(false, "请先完成首次设置");
    private VolumeEventArgs? _lastArrivedVolume;
    private string? _activeVolumeKey;
    private readonly HashSet<string> _safeCompletedVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _contentWitnessInvalidatedVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completionReverificationRequiredVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completionReverificationAuthorizedVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sourceCleanupReviewRequiredVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sourceCleanupReviewAuthorizedVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _sourceCleanupReviewCardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferredVolumeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingVolumeWork> _pendingVolumes = [];
    private readonly Dictionary<string, VolumeEventArgs> _knownVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StandaloneMediaItemDto> _mediaStates =
        new(StringComparer.OrdinalIgnoreCase);
    private long _mediaRevision;
    private string? _transferConfigurationFingerprint;
    private string? _cardReinitializationAuthorizedVolumeKey;
    private string? _cardReinitializationAuthorizedSourceIdentity;
    private Guid? _cardReassociationAuthorizedCardInstanceId;
    private string? _cardReassociationAuthorizedPreviousSourceIdentity;
    private bool _stopping;
    private bool _configurationChangeInProgress;
    private bool _stateRepairInProgress;
    private bool _currentVolumeBlocked;
    private long _lastPublishedProgressSequence;
    private StandaloneTargetMode _activeTargetMode = StandaloneTargetMode.LocalAndNas;

    public StandaloneRuntimeService(
        StandaloneConfigurationService configurationService,
        AtomicJsonFileStore<StandaloneConfiguration> configurationStore,
        StandaloneDataPaths paths,
        ILogger<StandaloneRuntimeService> logger)
    {
        _configurationService = configurationService;
        _configurationStore = configurationStore;
        _paths = paths;
        _logger = logger;
        _listener = new VolumeNotificationListener();
        _reconciler = new VolumeReconciler(new NativeVolumeSnapshotProvider(), TimeSpan.FromSeconds(10));
        _sourceVolumeClassifier = new ExternalSourceVolumeClassifier();
        _listener.VolumeArrived += OnVolumeArrived;
        _listener.VolumeRemoved += OnVolumeRemoved;
        _reconciler.ReconciliationComplete += OnReconciliationComplete;
    }

    public event EventHandler<object>? StatusChanged;
    public event EventHandler<StandaloneMediaStatusDto>? MediaStatusChanged;

    public object GetStatusSnapshot()
    {
        lock (_gate)
            return _status;
    }

    public StandaloneMediaStatusDto GetMediaStatusSnapshot()
    {
        lock (_gate)
            return CreateMediaStatusSnapshotNoLock();
    }

    public void EnsureConfigurationChangeAllowed()
    {
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "当前任务仍在复制或校验。为避免保存方式、目标路径或素材范围在任务中途改变，请等待任务结束或先停止任务。");
            }
            if (_stateRepairInProgress)
                throw new InvalidOperationException("素材卡恢复操作仍在进行，请等待完成后再修改设置。");
        }
    }

    public string CaptureManagedCardReinitializationVolumeKey(Guid cardInstanceId)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                throw new InvalidOperationException("当前任务仍在处理中，不能调整素材卡范围。");
            if (_configurationChangeInProgress || _stateRepairInProgress)
                throw new InvalidOperationException("另一项设置或素材卡恢复操作仍在进行，请稍后重试。");
            string[] mountedMatches = _mediaStates
                .Where(pair =>
                    string.Equals(pair.Value.PresenceState, "mounted", StringComparison.Ordinal) &&
                    Guid.TryParse(pair.Value.CardInstanceId, out Guid observedCardId) &&
                    observedCardId == cardInstanceId &&
                    _knownVolumes.ContainsKey(pair.Key))
                .Select(pair => pair.Key)
                .ToArray();
            if (mountedMatches.Length == 0)
                throw new InvalidOperationException("请先插入要调整的素材卡，再保存这张卡的新素材范围。");
            if (mountedMatches.Length > 1)
                throw new InvalidOperationException("同一素材卡出现了多个已插入端点，不能确定要调整哪一个。请只保留一个连接后重试。");
            return mountedMatches[0];
        }
    }

    public IDisposable BeginConfigurationChange()
    {
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "当前任务仍在复制或校验。为避免保存方式、目标路径或素材范围在任务中途改变，请等待任务结束或先停止任务。");
            }
            if (_stateRepairInProgress)
                throw new InvalidOperationException("素材卡恢复操作仍在进行，请等待完成后再修改设置。");
            if (_configurationChangeInProgress)
                throw new InvalidOperationException("另一项设置保存仍在进行，请稍后重试。");
            _configurationChangeInProgress = true;
        }
        return new ConfigurationChangeLease(this);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StandaloneConfigurationDto configuration = await _configurationService.GetAsync(cancellationToken);
        lock (_gate)
        {
            _stopping = false;
            _transferConfigurationFingerprint = configuration.Configured
                ? CreateTransferConfigurationFingerprint(configuration)
                : null;
        }
        SetStatus(WaitingStatus(configuration.Configured,
            configuration.Configured ? "等待插入素材卡" : "请先完成首次设置"));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                await _listener.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Immediate volume notifications are unavailable; the periodic mounted-volume reconciler remains active.");
            }
            await _reconciler.StartAsync(cancellationToken);
            if (configuration.Configured)
                await TryStartMountedExternalVolumeAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            _stopping = true;
            _pendingVolumes.Clear();
            _transferStatus.Stop();
            cts = _taskCts;
            task = _activeTask;
        }
        cts?.Cancel();
        if (task is not null)
        {
            try
            { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        await _listener.StopAsync();
        await _reconciler.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.VolumeArrived -= OnVolumeArrived;
        _listener.VolumeRemoved -= OnVolumeRemoved;
        _reconciler.ReconciliationComplete -= OnReconciliationComplete;
        await _listener.DisposeAsync();
        await _reconciler.DisposeAsync();
        _taskCts?.Dispose();
    }

    public async Task<StandaloneRuntimeOperationResult> RefreshAsync(CancellationToken cancellationToken)
    {
        StandaloneConfigurationDto configuration = await _configurationService.GetAsync(cancellationToken);
        if (!configuration.Configured)
        {
            object waiting = WaitingStatus(false, "请先完成首次设置");
            SetStatus(waiting);
            return new(waiting, "请先完成首次设置，当前没有可刷新的任务状态。");
        }

        VolumeEventArgs? volume;
        lock (_gate)
            volume = _lastArrivedVolume;
        if (volume is null)
            return new(GetStatusSnapshot(), "当前没有已挂载的素材卡可供重新读取。");

        Task? reevaluation = TryStartTransfer(volume, forceReevaluation: true);
        if (reevaluation is null)
            return new(GetStatusSnapshot(), "当前任务仍在处理中，未启动重复检查。");
        await reevaluation.WaitAsync(cancellationToken);
        object refreshed = GetStatusSnapshot();
        return new(refreshed, ReevaluationMessage(
            refreshed,
            "已重新读取配置、素材卡基线和持久化任务证据。"));
    }

    public async Task<StandaloneRuntimeOperationResult> RetryAsync(CancellationToken cancellationToken)
    {
        VolumeEventArgs? volume;
        lock (_gate)
            volume = _lastArrivedVolume;
        if (volume is null)
            return new(GetStatusSnapshot(), "当前没有可重新检查的素材卡。");

        Task? reevaluation = TryStartTransfer(volume, forceReevaluation: true);
        if (reevaluation is null)
            return new(GetStatusSnapshot(), "当前任务仍在处理中，未创建重复任务。");
        await reevaluation.WaitAsync(cancellationToken);
        object retried = GetStatusSnapshot();
        return new(retried, ReevaluationMessage(
            retried,
            "已完成持久化状态重新评估。"));
    }

    public async Task<StandaloneRuntimeOperationResult> RestartFreshAsync(
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new InvalidOperationException("重新开始任务需要明确确认。");

        VolumeEventArgs? volume;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                return new(_status, "当前任务仍在处理中，不能停用正在使用的任务记录。");
            if (_configurationChangeInProgress || _stateRepairInProgress)
                return new(_status, "另一项设置或素材卡恢复操作仍在进行，请稍后重试。");
            volume = _lastArrivedVolume;
            if (volume is not null)
                _stateRepairInProgress = true;
        }
        if (volume is null)
            return new(GetStatusSnapshot(), "当前没有可重新开始的素材卡。");

        bool handedOffToTransfer = false;
        try
        {
            string sourceRoot = Path.GetFullPath(volume.DriveLetter);
            FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
            string volumeKey = VolumeKey(volume);
            var catalog = new StandaloneCompletedTaskCatalog(_paths);
            var abandonedStore = new StandaloneAbandonedTaskStore(_paths.AbandonedTasksFile);
            Guid? reassociationCardInstanceId;
            string? reassociationPreviousSourceIdentity;
            bool completionReverificationRequired;
            lock (_gate)
            {
                bool authorizedForCurrentVolume = string.Equals(
                    _cardReinitializationAuthorizedVolumeKey,
                    VolumeKey(volume),
                    StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        _cardReinitializationAuthorizedSourceIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal);
                reassociationCardInstanceId = authorizedForCurrentVolume
                    ? _cardReassociationAuthorizedCardInstanceId
                    : null;
                reassociationPreviousSourceIdentity = authorizedForCurrentVolume
                    ? _cardReassociationAuthorizedPreviousSourceIdentity
                    : null;
                completionReverificationRequired =
                    _completionReverificationRequiredVolumeKeys.Contains(volumeKey);
                if (completionReverificationRequired)
                    _completionReverificationAuthorizedVolumeKeys.Add(volumeKey);
            }
            int abandonedCount = 0;
            while (await catalog.FindResumeCandidateAsync(
                       sourceDomain.StorageIdentity!, cancellationToken) is StandaloneTaskJournal candidate)
            {
                await abandonedStore.AbandonAsync(
                    candidate.TaskId,
                    candidate.SourceIdentity,
                    "user_confirmed_restart_fresh",
                    cancellationToken);
                abandonedCount++;
            }
            if (reassociationCardInstanceId is Guid cardInstanceId &&
                !string.IsNullOrWhiteSpace(reassociationPreviousSourceIdentity))
            {
                while (await catalog.FindResumeCandidateByCardInstanceIdAsync(
                           cardInstanceId, cancellationToken) is StandaloneTaskJournal candidate)
                {
                    await abandonedStore.AbandonAsync(
                        candidate.TaskId,
                        candidate.SourceIdentity,
                        "user_confirmed_cross_reader_restart_fresh",
                        cancellationToken);
                    abandonedCount++;
                }
                await RebindAuthorizedCardBaselineAsync(
                    cardInstanceId,
                    reassociationPreviousSourceIdentity,
                    sourceDomain.StorageIdentity!,
                    cancellationToken);
            }

            Task? restarted = CompleteStateRepairAndStartTransfer(volume, forceReevaluation: true);
            handedOffToTransfer = true;
            if (restarted is null)
                return new(GetStatusSnapshot(), "素材卡已移除或应用正在退出，未启动新任务。");
            await restarted.WaitAsync(cancellationToken);
            object status = GetStatusSnapshot();
            string message = completionReverificationRequired
                ? ReevaluationMessage(status, "旧完成记录已保留，当前全部批准素材已重新复制并完整校验。")
                : abandonedCount == 0
                    ? ReevaluationMessage(status, "没有需要停用的旧任务，已按当前持久化状态重新检查。")
                    : $"已保留并停用 {abandonedCount} 个旧任务记录，随后按当前卡片和设置重新检查。";
            return new(status, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            object failed = FailureStatus(
                "重新开始任务未完成",
                "操作已取消，旧任务记录和当前素材卡均未被标记为安全完成。",
                canRestartFresh: true);
            SetTerminalStatus(failed);
            return new(failed, "重新开始任务已取消，可保持卡片连接后重试。");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            object failed = FailureStatus(
                "重新开始任务未完成",
                exception.Message,
                canRestartFresh: true);
            SetTerminalStatus(failed);
            return new(failed, "未能完成旧任务停用与重新检查；旧记录仍保留。");
        }
        finally
        {
            if (!handedOffToTransfer)
                EndStateRepair(currentVolumeResolved: false);
        }
    }

    public Task<StandaloneRuntimeOperationResult> ReinitializeCurrentCardAsync(
        bool confirmed,
        CancellationToken cancellationToken) =>
        ReinitializeCurrentCardAsync(
            confirmed,
            managedCardInstanceId: null,
            managedCameraTemplateId: null,
            expectedManagedVolumeKey: null,
            waitForTransferCompletion: true,
            cancellationToken);

    public Task<StandaloneRuntimeOperationResult> ReinitializeManagedCardAsync(
        Guid cardInstanceId,
        Guid cameraTemplateId,
        string expectedVolumeKey,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        if (cameraTemplateId == Guid.Empty)
            throw new ArgumentException("Camera template identity is required.", nameof(cameraTemplateId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVolumeKey);
        return ReinitializeCurrentCardAsync(
            confirmed,
            cardInstanceId,
            cameraTemplateId,
            expectedVolumeKey,
            waitForTransferCompletion: false,
            cancellationToken);
    }

    public async Task<StandaloneRuntimeOperationResult> ConfirmSourceCleanupAsync(
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new InvalidOperationException("确认已主动清理素材需要明确确认。");

        VolumeEventArgs? volume;
        string? volumeKey = null;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                return new(_status, "当前任务仍在处理中，不能确认素材清理。");
            if (_configurationChangeInProgress || _stateRepairInProgress)
                return new(_status, "另一项设置或素材卡恢复操作仍在进行，请稍后重试。");
            volume = _lastArrivedVolume;
            if (volume is not null)
            {
                volumeKey = VolumeKey(volume);
                if (!_sourceCleanupReviewRequiredVolumeKeys.Contains(volumeKey) ||
                    !_sourceCleanupReviewCardIds.ContainsKey(volumeKey))
                    return new(_status, "当前状态没有待确认的素材清理差异。");
                _sourceCleanupReviewAuthorizedVolumeKeys.Add(volumeKey);
                _stateRepairInProgress = true;
            }
        }
        if (volume is null || volumeKey is null)
            return new(GetStatusSnapshot(), "当前没有可确认清理的素材卡。");

        bool handedOffToTransfer = false;
        try
        {
            Task? reevaluation = CompleteStateRepairAndStartTransfer(
                volume,
                forceReevaluation: true);
            if (reevaluation is null)
            {
                lock (_gate)
                    _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
                handedOffToTransfer = true; // The handoff helper already ended the repair lease.
                return new(GetStatusSnapshot(), "素材卡已移除或应用正在退出，未更新素材基线。");
            }
            handedOffToTransfer = true;
            await reevaluation.WaitAsync(cancellationToken);
            object status = GetStatusSnapshot();
            return new(status, ReevaluationMessage(
                status,
                "已保留旧基线快照，并按你确认的清理结果重新检查当前素材。"));
        }
        finally
        {
            if (!handedOffToTransfer)
            {
                lock (_gate)
                    _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
                EndStateRepair(currentVolumeResolved: false);
            }
        }
    }

    private async Task<StandaloneRuntimeOperationResult> ReinitializeCurrentCardAsync(
        bool confirmed,
        Guid? managedCardInstanceId,
        Guid? managedCameraTemplateId,
        string? expectedManagedVolumeKey,
        bool waitForTransferCompletion,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new InvalidOperationException("重新初始化素材卡需要明确确认。");
        bool managedRebind = managedCardInstanceId.HasValue && managedCameraTemplateId.HasValue;

        VolumeEventArgs? volume;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                return new(_status, "当前任务仍在处理中，不能重新初始化素材卡。");
            if (_configurationChangeInProgress || _stateRepairInProgress)
                return new(_status, "另一项设置或素材卡恢复操作仍在进行，请稍后重试。");
            volume = managedRebind && !string.IsNullOrWhiteSpace(expectedManagedVolumeKey)
                ? _knownVolumes.GetValueOrDefault(expectedManagedVolumeKey)
                : _lastArrivedVolume;
            if (managedRebind && volume is null)
            {
                throw new InvalidOperationException(
                    "要调整的素材卡已在确认后移除或发生变化。系统没有修改所选卡片档案；请重新插入该卡后再开始。");
            }
            if (volume is not null)
                _stateRepairInProgress = true;
        }
        if (volume is null)
            return new(GetStatusSnapshot(), "当前没有可重新初始化的素材卡。");

        bool reinitializationAuthorized = false;
        bool currentVolumeResolved = false;
        bool handedOffToTransfer = false;
        bool managedCardInitializationCommitted = false;
        try
        {
            StandaloneConfiguration? configuration = await _configurationStore.LoadAsync(cancellationToken);
            if (configuration is null || !configuration.Validate().IsValid)
                throw new InvalidDataException("首次设置未完成，不能重新初始化素材卡。");
            configuration = configuration.NormalizeForCurrentSchema();
            if (managedRebind)
            {
                if (configuration.EffectiveCameraTemplates.All(template =>
                        template.TemplateId != managedCameraTemplateId!.Value))
                {
                    throw new InvalidDataException("所选素材范围模板不存在。");
                }
            }

            string sourceRoot = Path.GetFullPath(volume.DriveLetter);
            EnsureNoOverlap(
                sourceRoot,
                configuration.TargetMode.RequiresLocal() ? configuration.LocalTargetPath : null,
                configuration.TargetMode.RequiresNas() ? configuration.NasMappedTargetPath : null);

            var faultDomains = new FaultDomainResolver();
            FaultDomainInfo sourceDomain = faultDomains.ResolveLocalDomain(sourceRoot);
            var taskCatalog = new StandaloneCompletedTaskCatalog(_paths);
            Guid? suspectedHistoricalCardId;
            lock (_gate)
            {
                suspectedHistoricalCardId = managedCardInstanceId ?? (string.Equals(
                        _cardReinitializationAuthorizedVolumeKey,
                        VolumeKey(volume),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        _cardReinitializationAuthorizedSourceIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal)
                    ? _cardReassociationAuthorizedCardInstanceId
                    : null);
            }
            StandaloneTaskJournal? crossReaderResume = suspectedHistoricalCardId is Guid historicalCardId
                ? await taskCatalog.FindResumeCandidateByCardInstanceIdAsync(
                    historicalCardId, cancellationToken)
                : null;
            if (crossReaderResume is not null)
            {
                object blocked = FailureStatus(
                    "仍有未完成任务",
                    "重新初始化会掩盖未完成任务中的素材差分，因此已拒绝。请先重新检查恢复；若旧任务无法恢复，可保留旧记录并重新开始。",
                    canRestartFresh: true);
                SetTerminalStatus(blocked);
                return new(blocked, "检测到未完成任务，未改动素材卡身份、档案或基线。");
            }

            string volumeKey = VolumeKey(volume);
            lock (_gate)
            {
                reinitializationAuthorized = string.Equals(
                        _cardReinitializationAuthorizedVolumeKey,
                        volumeKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        _cardReinitializationAuthorizedSourceIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal);
            }
            if (!managedRebind && !reinitializationAuthorized)
            {
                return new(
                    GetStatusSnapshot(),
                    "当前失败不属于素材卡身份、档案或基线冲突，未执行重新初始化。");
            }

            var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
            if (managedRebind &&
                managedCardInstanceId is Guid managedCardId &&
                configuration.CardProfiles.All(profile =>
                    profile.CardInstanceId != managedCardId) &&
                await baselineStore.FindByCardInstanceIdAsync(
                    managedCardId,
                    cancellationToken) is null)
            {
                throw new InvalidDataException(
                    "所选素材卡既没有可用档案，也没有历史基线，不能执行恢复绑定。");
            }
            _ = await baselineStore.FindAllForExplicitReinitializationAsync(
                sourceDomain.StorageIdentity!, cancellationToken);
            // Explicit reinitialization establishes a new card boundary, but a uniquely
            // identified historical profile may still select the intended recovery template.
            StandaloneCameraTemplate cameraTemplate = managedCameraTemplateId is Guid selectedTemplateId
                ? configuration.EffectiveCameraTemplates.Single(template =>
                    template.TemplateId == selectedTemplateId)
                : ResolveReinitializationCameraTemplate(configuration, baseline: null, sourceRoot);
            var selectionPolicy = new SourceSelectionPolicy(
                cameraTemplate.ApprovedSourceDirectories,
                cameraTemplate.NormalizedExtensions);
            string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                cameraTemplate.ApprovedSourceDirectories,
                cameraTemplate.NormalizedExtensions);
            SetStatus(PreparingStatus(
                "正在重新初始化素材卡",
                "只读取批准目录的文件元数据，不读取素材内容，也不写入保存目标。",
                configuration.TargetMode));
            var reinitializationManifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
            TaskManifest inventory = await reinitializationManifestBuilder.BuildAsync(
                Guid.NewGuid(),
                sourceRoot,
                "standalone-v1-user-reinitialize",
                SourceHashPolicy.MetadataOnly,
                selectionPolicy,
                cancellationToken);
            FaultDomainInfo prePersistSource = faultDomains.ResolveLocalDomain(sourceRoot);
            if (!string.Equals(
                    prePersistSource.StorageIdentity,
                    sourceDomain.StorageIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException("素材卡身份在重新初始化检查期间发生变化，未保存新的卡片状态。");
            }

            CardIdentityEvidence observed = StandaloneCardEvidenceBuilder.Build(
                sourceRoot,
                sourceDomain,
                inventory,
                volume.FileSystem,
                volume.Capacity);
            IReadOnlyList<StandaloneInventoryBaseline> pendingInitializations =
                await baselineStore.FindPendingInitializationsAsync(cancellationToken);
            StandaloneInventoryBaseline[] matchingPending = pendingInitializations
                .Where(value => value.InitializationEvidence is not null &&
                    StandaloneCardIdentityResolver.HasStrongExpectedContinuity(
                        value.InitializationEvidence, observed))
                .ToArray();
            if (managedRebind && matchingPending.Any(value =>
                    value.CardInstanceId != managedCardInstanceId!.Value))
            {
                throw new InvalidDataException(
                    "当前插入介质与另一项未完成的素材卡初始化事务连续。系统拒绝把它绑定到你选择的历史卡；请先恢复或隔离那项初始化事务。");
            }
            if (matchingPending.Length > 1)
            {
                throw new InvalidDataException(
                    "当前介质同时匹配多个未完成的初始化事务。系统没有覆盖或放弃其中任何一项；请保留状态文件并逐项恢复。");
            }
            var cardResolver = new StandaloneCardIdentityResolver(_paths.CardIdentityFile);
            var matchingCommittedCards = new List<Guid>();
            foreach (StandaloneInventoryBaseline candidateBaseline in
                     (await baselineStore.FindAllBaselinesAsync(cancellationToken))
                     .Where(value => !value.InitializationPending))
            {
                StandaloneCameraTemplate candidateTemplate;
                try
                {
                    candidateTemplate = ResolveCameraTemplate(
                        configuration, candidateBaseline, resumeCandidate: null, sourceRoot);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                // A template from another known card is not evidence about the mounted card
                // when its approved roots do not exist on this medium. Do not let an unrelated
                // card's missing folder block the explicitly selected card rebind.
                if (candidateTemplate.ApprovedSourceDirectories.Any(relativeRoot =>
                        !Directory.Exists(SafePathResolver.ResolveSafePath(sourceRoot, relativeRoot))))
                {
                    continue;
                }
                SourceSelectionPolicy candidatePolicy = new(
                    candidateTemplate.ApprovedSourceDirectories,
                    candidateTemplate.NormalizedExtensions);
                TaskManifest candidateInventory = await reinitializationManifestBuilder.BuildAsync(
                    Guid.NewGuid(),
                    sourceRoot,
                    "standalone-v1-reinitialize-existing-candidate",
                    SourceHashPolicy.MetadataOnly,
                    candidatePolicy,
                    cancellationToken);
                CardIdentityEvidence candidateObserved = StandaloneCardEvidenceBuilder.Build(
                    sourceRoot, sourceDomain, candidateInventory, volume.FileSystem, volume.Capacity);
                if (string.Equals(
                        candidateBaseline.SourceIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal) &&
                    HasBaselineObjectContinuity(candidateBaseline, candidateInventory))
                {
                    matchingCommittedCards.Add(candidateBaseline.CardInstanceId);
                    continue;
                }
                try
                {
                    await cardResolver.EnsureExpectedMountedSnapshotAsync(
                        candidateBaseline.CardInstanceId,
                        volume.FileSystem,
                        volume.Capacity,
                        cancellationToken);
                    StandaloneCardIdentityResolution candidate = await cardResolver.ResolveExpectedAsync(
                        candidateBaseline.CardInstanceId,
                        candidateObserved,
                        cancellationToken,
                        HasBaselineObjectContinuity(candidateBaseline, candidateInventory),
                        updateLastSeen: false);
                    if (!candidate.NeedsConfirmation && candidate.CardInstanceId is Guid candidateId)
                        matchingCommittedCards.Add(candidateId);
                }
                catch (InvalidDataException)
                {
                    // A malformed sibling card record must not block recovery of another valid card.
                }
            }

            Guid[] distinctCommittedCardIds = matchingCommittedCards.Distinct().ToArray();
            if (managedRebind && distinctCommittedCardIds.Any(cardId =>
                    cardId != managedCardInstanceId!.Value))
            {
                throw new InvalidDataException(
                    "当前插入介质已被可靠识别为另一张已初始化素材卡。系统拒绝把它绑定到你选择的卡片档案；请插入正确的卡后重试。");
            }
            int distinctCommittedMatches = managedRebind ? 0 : distinctCommittedCardIds.Length;
            if (distinctCommittedMatches > 1)
            {
                throw new InvalidDataException(
                    "当前素材卡同时匹配多个已提交的历史 CardId。系统没有新增、覆盖或隔离任何身份；请保留状态文件并选择具体历史卡后再继续。");
            }
            if (distinctCommittedMatches == 1)
            {
                Guid matchedCardId = distinctCommittedCardIds.Single();
                _ = await cardResolver.ReinitializeAsync(
                    observed,
                    matchedCardId,
                    cancellationToken);
                lock (_gate)
                {
                    _currentVolumeBlocked = false;
                    _cardReinitializationAuthorizedVolumeKey = null;
                    _cardReinitializationAuthorizedSourceIdentity = null;
                    _cardReassociationAuthorizedCardInstanceId = null;
                    _cardReassociationAuthorizedPreviousSourceIdentity = null;
                }
                Task? restartedKnownCard = CompleteStateRepairAndStartTransfer(
                    volume, forceReevaluation: true);
                handedOffToTransfer = true;
                if (restartedKnownCard is null)
                {
                    object unavailable = FailureStatus(
                        "素材卡当前不可用",
                        "已识别这张历史素材卡，但卡片已移除或应用正在退出；请重新插入后检查。其他卡的未完成初始化事务没有被改动。");
                    SetTerminalStatus(unavailable);
                    return new(unavailable, "未覆盖历史 CardId，也未改动其他卡的初始化事务；请重新插卡后继续。");
                }
                await restartedKnownCard.WaitAsync(cancellationToken);
                object restartedKnownStatus = GetStatusSnapshot();
                return new(restartedKnownStatus, ReevaluationMessage(
                    restartedKnownStatus,
                    "已按已知素材卡继续检查；其他卡的未完成初始化事务保持原样。"));
            }

            Guid initializationCardId = managedCardInstanceId ??
                (matchingPending.Length == 1
                    ? matchingPending[0].CardInstanceId
                    : Guid.NewGuid());
            // The pending baseline is the mutation intent. Unrelated cards' unfinished
            // initialization transactions remain untouched and can be recovered independently.
            await _configurationService.RebindCardProfileAsync(
                initializationCardId,
                cameraTemplate.TemplateId,
                cancellationToken);
            await baselineStore.ReinitializePendingAsync(
                initializationCardId,
                cameraTemplate.TemplateId,
                sourceDomain.StorageIdentity!,
                selectionPolicyHash,
                observed,
                abandonOtherPending: false,
                cancellationToken,
                archiveExistingCommittedBaseline: managedRebind);
            StandaloneCardIdentityResolution card = await cardResolver.ReinitializeAsync(
                observed,
                initializationCardId,
                cancellationToken);

            if (card.CardInstanceId != initializationCardId)
                throw new InvalidDataException("The card instance identity is unavailable.");
            await baselineStore.CommitInitializationAsync(initializationCardId, cancellationToken);
            managedCardInitializationCommitted = managedRebind;

            FaultDomainInfo persistedSource = faultDomains.ResolveLocalDomain(sourceRoot);
            if (!string.Equals(
                    persistedSource.StorageIdentity,
                    sourceDomain.StorageIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException("素材卡身份在重新初始化持久化期间发生变化，不能确认初始化完成。");
            }

            lock (_gate)
            {
                _currentVolumeBlocked = false;
                _cardReinitializationAuthorizedVolumeKey = null;
                _cardReinitializationAuthorizedSourceIdentity = null;
                _cardReassociationAuthorizedCardInstanceId = null;
                _cardReassociationAuthorizedPreviousSourceIdentity = null;
                _contentWitnessInvalidatedVolumeKeys.Remove(volumeKey);
            }
            if (inventory.TotalFiles > 0)
            {
                Task? restarted = CompleteStateRepairAndStartTransfer(volume, forceReevaluation: true);
                handedOffToTransfer = true;
                if (restarted is null)
                {
                    object unavailable = FailureStatus(
                        "素材卡当前不可用",
                        "新卡身份已保存，但素材卡已移除或应用正在退出；当前素材仍未进入基线，请重新插入后检查。");
                    SetTerminalStatus(unavailable);
                    return new(
                        unavailable,
                        "当前素材未被吞入基线；请重新插卡后继续复制。",
                        managedCardInitializationCommitted);
                }
                if (!waitForTransferCompletion)
                {
                    return new(
                        GetStatusSnapshot(),
                        "新素材范围和卡片绑定已保存，当前素材已在后台开始重新检查。",
                        managedCardInitializationCommitted);
                }
                await restarted.WaitAsync(cancellationToken);
                object restartedStatus = GetStatusSnapshot();
                return new(
                    restartedStatus,
                    ReevaluationMessage(
                        restartedStatus,
                        "新卡身份已建立，当前素材已按待复制差分重新检查。"),
                    managedCardInitializationCommitted);
            }

            lock (_gate)
                _safeCompletedVolumeKeys.Add(volumeKey);
            currentVolumeResolved = true;
            object status = BaselineReadyStatus(
                "空素材卡已初始化",
                $"已按相机模板“{cameraTemplate.Name}”建立空基线。未读取素材内容、未写入保存目标；以后出现的第一批素材仍会作为新增内容复制。");
            SetTerminalStatus(status);
            return new(
                status,
                "空素材卡初始化完成，可以继续使用。",
                managedCardInitializationCommitted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            object failed = FailureStatus(
                "素材卡重新初始化未完成",
                "操作已取消，素材卡未被确认安全完成。",
                canReinitializeCard: reinitializationAuthorized);
            SetTerminalStatus(failed);
            return new(
                failed,
                "重新初始化已取消；身份、档案与基线未被确认完成。",
                managedCardInitializationCommitted);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            object failed = FailureStatus(
                "素材卡重新初始化未完成",
                exception.Message,
                canReinitializeCard: reinitializationAuthorized);
            SetTerminalStatus(failed);
            return new(
                failed,
                "重新初始化未完成；当前素材卡仍可修复后重试。",
                managedCardInitializationCommitted);
        }
        finally
        {
            if (!handedOffToTransfer)
                EndStateRepair(currentVolumeResolved);
        }
    }

    public Task<object> CancelAsync(Guid? expectedOperationId)
    {
        object status;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_activeTask is not { IsCompleted: false } || _activeOperationId is null)
                return Task.FromResult(WithToast(_status, "当前任务已经结束，未改变现有完成或失败状态。"));
            if (expectedOperationId is null || expectedOperationId == Guid.Empty ||
                _activeOperationId != expectedOperationId)
            {
                return Task.FromResult(WithToast(
                    _status,
                    "停止请求属于旧执行或缺少当前执行编号，已忽略；当前任务继续运行。"));
            }

            cancellation = _taskCts;
            _activeOperationId = null;
            _transferStatus.Stop();
            status = FailureStatus("任务已停止", "任务没有完成所选目标的全部校验，不可以安全拔卡。");
            _status = status;
            ApplyMainStatusToActiveMediaNoLock(status);
        }
        cancellation?.Cancel();
        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
        return Task.FromResult(WithToast(status, "当前任务已停止。"));
    }

    public Task<StandaloneRuntimeOperationResult> DeferCurrentBlockedVolumeAsync(
        string expectedMountSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMountSessionId);
        cancellationToken.ThrowIfCancellationRequested();
        bool startPending;
        object status;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                return Task.FromResult(new StandaloneRuntimeOperationResult(
                    _status,
                    "当前卡仍在复制或校验，不能暂缓。请先停止当前任务。"));
            }
            if (!_currentVolumeBlocked || string.IsNullOrWhiteSpace(_activeVolumeKey))
            {
                return Task.FromResult(new StandaloneRuntimeOperationResult(
                    _status,
                    "当前没有阻塞其他素材卡的异常卡。"));
            }
            if (!_mediaStates.TryGetValue(_activeVolumeKey, out StandaloneMediaItemDto? activeMedia) ||
                !string.Equals(
                    activeMedia.MountSessionId,
                    expectedMountSessionId,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(new StandaloneRuntimeOperationResult(
                    _status,
                    "素材卡状态已变化，未暂缓其他卡；请刷新后重试。"));
            }

            string deferredVolumeKey = _activeVolumeKey;
            _deferredVolumeKeys.Add(deferredVolumeKey);
            _currentVolumeBlocked = false;
            _activeVolumeKey = null;
            _activeOperationId = null;
            _activeTask = null;
            if (_mediaStates.TryGetValue(deferredVolumeKey, out StandaloneMediaItemDto? media))
            {
                _mediaStates[deferredVolumeKey] = media with
                {
                    WorkState = "deferred",
                    QueuePosition = null,
                    SafetyConclusion = "no_backup_conclusion",
                    ReasonCode = "USER_DEFERRED_BLOCKED_CARD",
                    PrimaryAction = "retry",
                    AvailableActions = ["retry", "configure_card_scope"],
                    Detail = "这张卡仍未安全完成，已暂缓；其他已插入卡可以继续处理。",
                };
                _mediaRevision++;
            }
            status = WaitingStatus(
                true,
                "异常素材卡已暂缓",
                "这张卡仍未安全完成；系统会继续处理队列中的其他素材卡。");
            _status = status;
            startPending = !_stopping &&
                !_configurationChangeInProgress &&
                !_stateRepairInProgress &&
                _pendingVolumes.Count > 0;
        }

        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
        if (startPending)
            StartNextPendingVolume();
        return Task.FromResult(new StandaloneRuntimeOperationResult(
            GetStatusSnapshot(),
            "已暂缓当前异常卡；失败记录和恢复证据均已保留。"));
    }

    public async Task NotifyConfigurationChangedAsync(
        StandaloneConfigurationDto configuration,
        CancellationToken cancellationToken,
        bool startMountedVolume = true)
    {
        string? fingerprint = configuration.Configured
            ? CreateTransferConfigurationFingerprint(configuration)
            : null;
        lock (_gate)
        {
            if (!string.Equals(_transferConfigurationFingerprint, fingerprint, StringComparison.Ordinal))
                _safeCompletedVolumeKeys.Clear();
            _transferConfigurationFingerprint = fingerprint;
        }

        SetStatus(WaitingStatus(configuration.Configured,
            configuration.Configured ? "等待插入素材卡" : "请先完成首次设置"));
        if (startMountedVolume && configuration.Configured && OperatingSystem.IsWindows())
            await TryStartMountedExternalVolumeAsync(cancellationToken);
    }

    public async Task<StandaloneRuntimeOperationResult> ReassociateCurrentCardAsync(
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new InvalidOperationException("确认同一张素材卡需要明确确认。");

        VolumeEventArgs? volume;
        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
                return new(_status, "当前任务仍在处理中，不能迁移素材卡基线。");
            if (_configurationChangeInProgress || _stateRepairInProgress)
                return new(_status, "另一项设置或素材卡恢复操作仍在进行，请稍后重试。");
            volume = _lastArrivedVolume;
            if (volume is not null)
                _stateRepairInProgress = true;
        }
        if (volume is null)
            return new(GetStatusSnapshot(), "当前没有可确认的素材卡。");

        bool handedOffToTransfer = false;
        bool authorized = false;
        try
        {
            string sourceRoot = Path.GetFullPath(volume.DriveLetter);
            FaultDomainInfo sourceDomain = new FaultDomainResolver().ResolveLocalDomain(sourceRoot);
            string volumeKey = VolumeKey(volume);
            Guid? cardInstanceId;
            string? previousSourceIdentity;
            lock (_gate)
            {
                authorized = string.Equals(
                        _cardReinitializationAuthorizedVolumeKey,
                        volumeKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        _cardReinitializationAuthorizedSourceIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal) &&
                    _cardReassociationAuthorizedCardInstanceId is Guid;
                cardInstanceId = authorized ? _cardReassociationAuthorizedCardInstanceId : null;
                previousSourceIdentity = authorized
                    ? _cardReassociationAuthorizedPreviousSourceIdentity
                    : null;
            }
            if (!authorized || cardInstanceId is not Guid confirmedCardId ||
                string.IsNullOrWhiteSpace(previousSourceIdentity))
            {
                return new(GetStatusSnapshot(), "当前失败没有唯一的历史素材卡候选，未迁移旧基线。");
            }

            var catalog = new StandaloneCompletedTaskCatalog(_paths);
            StandaloneTaskJournal? unfinished = await catalog.FindResumeCandidateByCardInstanceIdAsync(
                confirmedCardId, cancellationToken);
            if (unfinished is not null)
            {
                object blocked = FailureStatus(
                    "同一张卡仍有未完成任务",
                    "已找到这张卡的旧任务记录。为避免跳过尚未确认完成的素材，暂不迁移基线；可换回原读卡器恢复，或明确保留旧记录并重新开始。",
                    canRestartFresh: true,
                    canReinitializeCard: true,
                    canReassociateCard: true);
                SetTerminalStatus(blocked);
                return new(blocked, "未完成任务仍保留，素材卡身份和基线未改动。");
            }

            StandaloneConfiguration? configuration = await _configurationStore.LoadAsync(cancellationToken);
            if (configuration is null || !configuration.Validate().IsValid)
                throw new InvalidDataException("首次设置未完成，不能确认同一张素材卡。");
            configuration = configuration.NormalizeForCurrentSchema();
            SetStatus(PreparingStatus(
                "正在确认同一张素材卡",
                "正在更新当前介质的身份连续性证据并迁移旧基线，然后会重新检查新增或被改写的素材。",
                configuration.TargetMode));
            var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
            StandaloneInventoryBaseline confirmedBaseline = await baselineStore.FindByCardInstanceIdAsync(
                confirmedCardId, cancellationToken) ??
                throw new InvalidDataException("历史素材卡基线不存在，不能确认同一张卡。");
            StandaloneCameraTemplate confirmedTemplate = ResolveCameraTemplate(
                configuration, confirmedBaseline, resumeCandidate: null, sourceRoot);
            TaskManifest identityInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
                Guid.NewGuid(),
                sourceRoot,
                "standalone-v1-user-reassociate",
                SourceHashPolicy.MetadataOnly,
                new SourceSelectionPolicy(
                    confirmedTemplate.ApprovedSourceDirectories,
                    confirmedTemplate.NormalizedExtensions),
                cancellationToken);
            CardIdentityEvidence confirmedEvidence = StandaloneCardEvidenceBuilder.Build(
                sourceRoot, sourceDomain, identityInventory, volume.FileSystem, volume.Capacity);
            await RebindAuthorizedCardBaselineAsync(
                confirmedCardId,
                previousSourceIdentity,
                sourceDomain.StorageIdentity!,
                cancellationToken);
            StandaloneCardIdentityResolution confirmedIdentity = await new StandaloneCardIdentityResolver(
                _paths.CardIdentityFile).ReinitializeAsync(
                    confirmedEvidence,
                    confirmedCardId,
                    cancellationToken);
            if (confirmedIdentity.CardInstanceId != confirmedCardId)
                throw new IOException("同卡确认未能保留预期素材卡身份，已停止。");

            Task? restarted = CompleteStateRepairAndStartTransfer(volume, forceReevaluation: true);
            handedOffToTransfer = true;
            if (restarted is null)
            {
                object unavailable = FailureStatus(
                    "素材卡当前不可用",
                    "确认过程中素材卡已移除或应用正在退出，未启动复制检查。",
                    canReassociateCard: true);
                SetTerminalStatus(unavailable);
                return new(unavailable, "素材卡未保持连接，请重新插入后再检查。");
            }
            await restarted.WaitAsync(cancellationToken);
            object status = GetStatusSnapshot();
            return new(status, ReevaluationMessage(
                status,
                "已保留历史基线并按当前读卡器重新检查新增素材。"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            object failed = FailureStatus(
                "同卡确认未完成",
                "操作已取消，未把当前状态标记为安全完成。",
                canReassociateCard: authorized);
            SetTerminalStatus(failed);
            return new(failed, "同卡确认已取消，可保持卡片连接后重试。");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            object failed = FailureStatus(
                "同卡确认未完成",
                exception.Message,
                canReassociateCard: authorized);
            SetTerminalStatus(failed);
            return new(failed, "历史基线未被静默替换；修复问题后可以重试。");
        }
        finally
        {
            if (!handedOffToTransfer)
                EndStateRepair(currentVolumeResolved: false);
        }
    }

    private void OnVolumeArrived(object? sender, VolumeEventArgs volume)
    {
        if (!IsExternalSourceVolume(volume))
            return;
        lock (_gate)
        {
            _knownVolumes[VolumeKey(volume)] = volume;
            EnsureMediaStateNoLock(volume);
        }
        RaiseMediaStatusChanged();
        TryStartTransfer(volume);
        RaiseMediaStatusChanged();
    }

    private async Task TryStartMountedExternalVolumeAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<VolumeEventArgs>? reconciledVolumes = null,
        bool skipIfScanBusy = false,
        bool revisitKnownVolumes = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool entered = skipIfScanBusy
            ? await _mountedVolumeScan.WaitAsync(0, cancellationToken)
            : await WaitForMountedVolumeScanAsync(cancellationToken);
        if (!entered)
            return;

        try
        {
            lock (_gate)
            {
                if (_activeTask is { IsCompleted: false })
                    return;
            }

            StandaloneConfiguration? configuration = await _configurationStore.LoadAsync(cancellationToken);
            if (configuration is null || !configuration.Validate().IsValid)
                return;
            configuration = configuration.NormalizeForCurrentSchema();

            IReadOnlyList<VolumeEventArgs> mounted = reconciledVolumes ??
                await _reconciler.GetCurrentVolumesAsync();
            foreach (VolumeEventArgs volume in mounted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExternalSourceVolume(volume))
                    continue;
                bool wasAlreadyKnown;
                lock (_gate)
                {
                    string volumeKey = VolumeKey(volume);
                    wasAlreadyKnown = _knownVolumes.ContainsKey(volumeKey);
                    _knownVolumes[volumeKey] = volume;
                    EnsureMediaStateNoLock(volume);
                }

                bool safeCompletedVolume = IsSafeCompletedVolume(volume);
                // The reconciler runs every ten seconds to recover missed insert/remove
                // notifications. A mounted non-safe card that is already known is one
                // continuous user-visible session, not a new insertion. Re-running it
                // would overwrite an attention/setup screen and silently retry blocked
                // work forever. Safe-completed cards remain eligible for periodic proof
                // revalidation; explicit refresh/configuration changes also opt in.
                if (!revisitKnownVolumes && wasAlreadyKnown && !safeCompletedVolume)
                    continue;
                RaiseMediaStatusChanged();

                bool hasApprovedSourceDirectory = false;
                bool approvedDirectoryProbeCompleted = false;
                try
                {
                    hasApprovedSourceDirectory = ContainsApprovedSourceDirectory(
                        volume.DriveLetter, configuration);
                    approvedDirectoryProbeCompleted = true;
                    if (!safeCompletedVolume && !hasApprovedSourceDirectory)
                    {
                        OnVolumeArrived(this, volume);
                        continue;
                    }

                    EnsureNoOverlap(
                        Path.GetFullPath(volume.DriveLetter),
                        configuration.TargetMode.RequiresLocal()
                            ? Path.GetFullPath(configuration.LocalTargetPath)
                            : null,
                        configuration.TargetMode.RequiresNas()
                            ? Path.GetFullPath(configuration.NasMappedTargetPath)
                            : null);
                    if (safeCompletedVolume && !await ShouldReevaluateSafeVolumeAsync(
                            volume, configuration, cancellationToken))
                    {
                        lock (_gate)
                        {
                            if (_activeTask is not { IsCompleted: false } && !_currentVolumeBlocked)
                                _lastArrivedVolume = volume;
                            UpdateMediaNoLock(VolumeKey(volume), media => media with
                            {
                                WorkState = "completed",
                                SafetyConclusion = "approved_material_verified",
                                ReasonCode = "SAFE_COMPLETION_REUSED",
                                OverallPercent = 100,
                                Detail = "已重新验证最近完成记录；本卡当前素材范围可以安全拔卡。",
                            });
                        }
                        RaiseMediaStatusChanged();
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (safeCompletedVolume || hasApprovedSourceDirectory || !approvedDirectoryProbeCompleted)
                    {
                        lock (_gate)
                        {
                            _safeCompletedVolumeKeys.Remove(VolumeKey(volume));
                            _lastArrivedVolume = volume;
                        }
                        SetTerminalStatus(FailureStatus(
                            safeCompletedVolume ? "素材卡重新检查失败" : "素材卡检查失败",
                            "无法读取已挂载素材卡的批准目录或验证路径安全性：" + exception.Message));
                        return;
                    }

                    _logger.LogDebug(exception,
                        "Mounted volume {VolumeGuid} is not an eligible configured source.",
                        volume.VolumeGuid);
                    continue;
                }

                if (safeCompletedVolume)
                {
                    lock (_gate)
                        _lastArrivedVolume = volume;
                    _ = TryStartTransfer(volume, forceReevaluation: true);
                }
                else
                {
                    OnVolumeArrived(this, volume);
                }
                continue;
            }
        }
        finally
        {
            _mountedVolumeScan.Release();
        }
    }

    private async Task<bool> WaitForMountedVolumeScanAsync(CancellationToken cancellationToken)
    {
        await _mountedVolumeScan.WaitAsync(cancellationToken);
        return true;
    }

    private static string VolumeKey(VolumeEventArgs volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        string endpoint = !string.IsNullOrWhiteSpace(volume.VolumeGuid)
            ? volume.VolumeGuid.Trim()
            : volume.DriveLetter.Trim();
        if (endpoint.Length == 0)
            throw new InvalidDataException("Mounted volume identity is unavailable.");
        return string.Join(
            "|",
            endpoint.ToUpperInvariant(),
            volume.DriveLetter.Trim().ToUpperInvariant(),
            volume.FileSystem.Trim().ToUpperInvariant(),
            volume.Capacity.ToString(CultureInfo.InvariantCulture),
            volume.MountIdentity?.Trim().ToUpperInvariant() ?? "NO-MOUNT-IDENTITY",
            volume.MountContinuityProven ? "STRONG" : "WEAK");
    }

    private bool IsSafeCompletedVolume(VolumeEventArgs volume)
    {
        if (!CanReuseSafeCompletion(volume))
            return false;
        lock (_gate)
        {
            return _safeCompletedVolumeKeys.Contains(VolumeKey(volume));
        }
    }

    private static bool CanReuseSafeCompletion(VolumeEventArgs volume) =>
        volume.MountContinuityProven && !string.IsNullOrWhiteSpace(volume.MountIdentity);

    private void RememberSafeCompletion(VolumeEventArgs volume)
    {
        string volumeKey = VolumeKey(volume);
        lock (_gate)
        {
            _contentWitnessInvalidatedVolumeKeys.Remove(volumeKey);
            _completionReverificationRequiredVolumeKeys.Remove(volumeKey);
            _completionReverificationAuthorizedVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewRequiredVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewCardIds.Remove(volumeKey);
            if (CanReuseSafeCompletion(volume))
                _safeCompletedVolumeKeys.Add(volumeKey);
        }
    }

    private async Task<bool> ShouldReevaluateSafeVolumeAsync(
        VolumeEventArgs volume,
        StandaloneConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!IsSafeCompletedVolume(volume))
            return true;

        string sourceRoot = Path.GetFullPath(volume.DriveLetter);
        var faultDomains = new FaultDomainResolver();
        FaultDomainInfo sourceDomain = faultDomains.ResolveLocalDomain(sourceRoot);
        var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
        IReadOnlyList<StandaloneInventoryBaseline> endpointBaselines =
            await baselineStore.FindAllBySourceIdentityAsync(
                sourceDomain.StorageIdentity!, cancellationToken);
        if (endpointBaselines.Count != 1)
            return true;
        StandaloneInventoryBaseline baseline = endpointBaselines[0];

        StandaloneCameraTemplate cameraTemplate = ResolveCameraTemplate(
            configuration.NormalizeForCurrentSchema(), baseline, resumeCandidate: null, sourceRoot);
        string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
            cameraTemplate.ApprovedSourceDirectories,
            cameraTemplate.NormalizedExtensions);
        var manifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
        TaskManifest inventory = await manifestBuilder.BuildAsync(
            Guid.NewGuid(),
            sourceRoot,
            "standalone-v1-mounted-delta-check",
            SourceHashPolicy.MetadataOnly,
            new SourceSelectionPolicy(
                cameraTemplate.ApprovedSourceDirectories,
                cameraTemplate.NormalizedExtensions),
            cancellationToken);
        StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
            baseline, selectionPolicyHash, inventory);
        if (!delta.SelectionPolicyMatches ||
            delta.AddedEntries.Count > 0 ||
            delta.ChangedPaths.Count > 0 ||
            delta.IdentityChangedPaths.Count > 0 ||
            delta.MissingPaths.Count > 0)
        {
            return true;
        }

        var cardResolver = new StandaloneCardIdentityResolver(_paths.CardIdentityFile);
        StandaloneCardIdentityBinding? binding = await cardResolver.FindBindingAsync(
            baseline.CardInstanceId, cancellationToken);
        if (binding is null)
            return true;

        CardIdentityEvidence currentEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot, sourceDomain, inventory, volume.FileSystem, volume.Capacity);
        if (!string.Equals(
                binding.SafeCompletionEvidence?.RootDirectoryHash,
                currentEvidence.RootDirectoryHash,
                StringComparison.Ordinal))
        {
            return true;
        }

        bool hasSelectedFiles = inventory.Entries.Any(entry => !entry.Excluded);
        if (!hasSelectedFiles)
            return false;
        if (string.IsNullOrWhiteSpace(binding.SafeCompletionEvidence?.SampleFingerprint) ||
            string.IsNullOrWhiteSpace(currentEvidence.SampleFingerprint))
        {
            return true;
        }

        bool contentWitnessMismatch = !string.Equals(
            binding.SafeCompletionEvidence.SampleFingerprint,
            currentEvidence.SampleFingerprint,
            StringComparison.Ordinal);
        if (contentWitnessMismatch)
        {
            lock (_gate)
                _contentWitnessInvalidatedVolumeKeys.Add(VolumeKey(volume));
        }
        return contentWitnessMismatch;
    }

    private static async Task RefreshCardSafetyEvidenceAsync(
        StandaloneCardIdentityResolver cardResolver,
        Guid cardInstanceId,
        string sourceRoot,
        FaultDomainInfo expectedSourceDomain,
        TaskManifest inventory,
        VolumeEventArgs volume,
        CancellationToken cancellationToken)
    {
        var faultDomains = new FaultDomainResolver();
        FaultDomainInfo currentSourceDomain = faultDomains.ResolveLocalDomain(sourceRoot);
        if (!string.Equals(
                currentSourceDomain.StorageIdentity,
                expectedSourceDomain.StorageIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException("素材卡身份在安全状态确认期间发生变化。");
        }

        CardIdentityEvidence safetyEvidence = StandaloneCardEvidenceBuilder.Build(
            sourceRoot,
            currentSourceDomain,
            inventory,
            volume.FileSystem,
            volume.Capacity);
        await cardResolver.RefreshSafeCompletionEvidenceAsync(
            cardInstanceId,
            safetyEvidence,
            cancellationToken);
    }

    private static bool HasBaselineObjectContinuity(
        StandaloneInventoryBaseline baseline,
        TaskManifest inventory)
    {
        if (baseline.Entries.Count < 2)
            return false;
        var current = inventory.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        return baseline.Entries.All(previous =>
            current.TryGetValue(previous.RelativePath, out ManifestEntry? observed) &&
            previous.Length == observed.FileSize &&
            previous.LastModifiedUtc == observed.LastModifiedUtc &&
            string.Equals(previous.SourceFileIdentity, observed.SourceFileId, StringComparison.Ordinal) &&
            string.Equals(previous.SourceFileIdentityType, observed.SourceFileIdType, StringComparison.Ordinal));
    }

    private static bool HasDeletionSubsetContinuity(
        StandaloneInventoryBaseline baseline,
        TaskManifest inventory)
    {
        if (baseline.Entries.Count < 2)
            return false;
        var historical = baseline.Entries.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var current = inventory.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        int unchangedHistoricalObjects = 0;
        int missingHistoricalObjects = 0;
        foreach (StandaloneInventoryBaselineEntry previous in historical.Values)
        {
            if (!current.TryGetValue(previous.RelativePath, out ManifestEntry? observed))
            {
                missingHistoricalObjects++;
                continue;
            }
            if (previous.Length != observed.FileSize ||
                previous.LastModifiedUtc != observed.LastModifiedUtc ||
                !string.Equals(previous.SourceFileIdentity, observed.SourceFileId, StringComparison.Ordinal) ||
                !string.Equals(previous.SourceFileIdentityType, observed.SourceFileIdType, StringComparison.Ordinal))
            {
                return false;
            }
            unchangedHistoricalObjects++;
        }
        return missingHistoricalObjects > 0 && unchangedHistoricalObjects >= 2;
    }

    private static IReadOnlyList<ManifestEntry> IncludeContentWitnessTransferEntries(
        TaskManifest fullInventory,
        IReadOnlyList<ManifestEntry> deltaEntries,
        bool contentWitnessInvalidated,
        bool forceFullReverification)
    {
        if (forceFullReverification)
        {
            return fullInventory.Entries
                .Where(entry => !entry.Excluded)
                .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (!contentWitnessInvalidated)
            return deltaEntries;
        HashSet<string> sampledPaths = StandaloneCardEvidenceBuilder
            .GetSampledRelativePaths(fullInventory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return deltaEntries
            .Concat(fullInventory.Entries.Where(entry =>
                !entry.Excluded && sampledPaths.Contains(entry.RelativePath)))
            .GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    private static bool IdentityChangesRequireTransfer(string fileSystem)
    {
        _ = fileSystem;
        // A changed FileId is not content proof on any filesystem. Until a verified
        // content fingerprint exists, refreshing would be able to swallow a replacement.
        return true;
    }

    private static bool ContainsApprovedSourceDirectory(
        string volumeRoot,
        StandaloneConfiguration configuration) =>
        configuration.EffectiveCameraTemplates.Any(template =>
            ContainsApprovedSourceDirectoryForTemplate(volumeRoot, template));

    private static bool ContainsApprovedSourceDirectoryForTemplate(
        string volumeRoot,
        StandaloneCameraTemplate cameraTemplate)
    {
        string root = Path.GetFullPath(volumeRoot);
        foreach (string relativeRoot in cameraTemplate.ApprovedSourceDirectories)
        {
            string candidate = SafePathResolver.ResolveSafePath(root, relativeRoot);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            if ((attributes & FileAttributes.Directory) == 0)
                continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"批准源目录不能是重解析点：'{candidate}'。");
            return true;
        }
        return false;
    }

    private static bool ContainsApprovedCandidateFile(
        string volumeRoot,
        StandaloneConfiguration configuration) =>
        configuration.EffectiveCameraTemplates.Any(template =>
            ContainsApprovedCandidateFileForTemplate(volumeRoot, template));

    private static bool ContainsApprovedCandidateFileForTemplate(
        string volumeRoot,
        StandaloneCameraTemplate cameraTemplate)
    {
        string root = Path.GetFullPath(volumeRoot);
        HashSet<string> approved = cameraTemplate.NormalizedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeRoot in cameraTemplate.ApprovedSourceDirectories)
        {
            string scanRoot = SafePathResolver.ResolveSafePath(root, relativeRoot);
            if (!Directory.Exists(scanRoot))
                continue;
            if ((File.GetAttributes(scanRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"批准源目录不能是重解析点：'{scanRoot}'。");
            foreach (string file in Directory.EnumerateFiles(scanRoot, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"批准源目录包含重解析点：'{file}'。");
                if (approved.Contains(Path.GetExtension(file)))
                    return true;
            }
        }
        return false;
    }

    private bool IsExternalSourceVolume(VolumeEventArgs volume)
    {
        try
        {
            return _sourceVolumeClassifier.IsExternalStorage(volume);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Unable to prove that mounted volume {VolumeGuid} is external storage; ignoring it.",
                volume.VolumeGuid);
            return false;
        }
    }

    private void OnVolumeRemoved(object? sender, VolumeEventArgs volume)
    {
        string volumeKey = VolumeKey(volume);
        CancellationTokenSource? cancellation = null;
        object? removalStatus = null;
        bool removedKnownVolume = false;
        bool startPending = false;
        bool showWaiting = false;
        lock (_gate)
        {
            bool matchesActive = string.Equals(
                _activeVolumeKey, volumeKey, StringComparison.OrdinalIgnoreCase);
            bool matchesSafe = _safeCompletedVolumeKeys.Contains(volumeKey);
            bool matchesLast = _lastArrivedVolume is not null && string.Equals(
                VolumeKey(_lastArrivedVolume), volumeKey, StringComparison.OrdinalIgnoreCase);
            int removedPending = _pendingVolumes.RemoveAll(value => string.Equals(
                VolumeKey(value.Volume), volumeKey, StringComparison.OrdinalIgnoreCase));
            bool wasKnown = _knownVolumes.Remove(volumeKey);
            removedKnownVolume = wasKnown || matchesActive || matchesSafe || matchesLast || removedPending > 0;
            if (matchesActive && !matchesSafe && _activeTask is { IsCompleted: false })
            {
                cancellation = _taskCts;
                if (_activeOperationId is not null)
                {
                    _activeOperationId = null;
                    _transferStatus.Stop();
                    removalStatus = FailureStatus(
                        "素材卡已移除",
                        "复制没有确认安全完成。请重新插入同一张卡后重试。");
                    _status = removalStatus;
                }
            }
            if (matchesActive)
            {
                _activeVolumeKey = null;
                _currentVolumeBlocked = false;
                startPending = _activeTask is not { IsCompleted: false } &&
                    !_configurationChangeInProgress &&
                    !_stateRepairInProgress &&
                    !_stopping &&
                    _pendingVolumes.Count > 0;
            }
            if (matchesSafe)
                _safeCompletedVolumeKeys.Remove(volumeKey);
            _contentWitnessInvalidatedVolumeKeys.Remove(volumeKey);
            _completionReverificationRequiredVolumeKeys.Remove(volumeKey);
            _completionReverificationAuthorizedVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewRequiredVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
            _sourceCleanupReviewCardIds.Remove(volumeKey);
            _deferredVolumeKeys.Remove(volumeKey);
            UpdateMediaNoLock(volumeKey, media => media with
            {
                PresenceState = "removed",
                WorkState = matchesActive && !matchesSafe ? "blocked" : "removed",
                QueuePosition = null,
                SafetyConclusion = matchesSafe
                    ? media.SafetyConclusion
                    : "no_backup_conclusion",
                ReasonCode = matchesActive && !matchesSafe
                    ? "ACTIVE_CARD_REMOVED"
                    : "CARD_REMOVED",
                PrimaryAction = matchesActive && !matchesSafe ? "retry" : string.Empty,
                AvailableActions = matchesActive && !matchesSafe ? ["retry"] : [],
                Detail = matchesActive && !matchesSafe
                    ? "素材卡在任务安全完成前被移除；重新插入同一张卡后可以继续。"
                    : "素材卡已移除。",
            });
            ReindexQueuedMediaNoLock();
            if (matchesLast)
                _lastArrivedVolume = null;
            if (string.Equals(
                    _cardReinitializationAuthorizedVolumeKey,
                    volumeKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                _cardReinitializationAuthorizedVolumeKey = null;
                _cardReinitializationAuthorizedSourceIdentity = null;
                _cardReassociationAuthorizedCardInstanceId = null;
                _cardReassociationAuthorizedPreviousSourceIdentity = null;
            }
            showWaiting = removalStatus is null && !startPending && removedKnownVolume &&
                _activeTask is not { IsCompleted: false } && _activeOperationId is null;
        }

        if (removalStatus is not null)
            StatusChanged?.Invoke(this, removalStatus);
        cancellation?.Cancel();
        if (removalStatus is null && startPending)
            StartNextPendingVolume();
        else if (removalStatus is null && showWaiting)
            SetStatus(WaitingStatus(true, "等待插入素材卡"));
        RaiseMediaStatusChanged();
    }

    private async void OnReconciliationComplete(object? sender, VolumeReconciliationResult result)
    {
        try
        {
            HashSet<string> mountedKeys = result.CurrentVolumes
                .Select(VolumeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            VolumeEventArgs[] missing;
            lock (_gate)
            {
                missing = _knownVolumes
                    .Where(pair => !mountedKeys.Contains(pair.Key))
                    .Select(pair => pair.Value)
                    .ToArray();
            }
            foreach (VolumeEventArgs removed in missing)
                OnVolumeRemoved(this, removed);

            await TryStartMountedExternalVolumeAsync(
                CancellationToken.None,
                result.CurrentVolumes,
                skipIfScanBusy: true,
                revisitKnownVolumes: false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Mounted-volume reconciliation did not start a transfer.");
        }
    }

    private Task? TryStartTransfer(VolumeEventArgs volume, bool forceReevaluation = false)
    {
        string volumeKey = VolumeKey(volume);
        lock (_gate)
        {
            if (_stopping)
                return null;
            _knownVolumes[volumeKey] = volume;
            EnsureMediaStateNoLock(volume);
            if (forceReevaluation)
                _deferredVolumeKeys.Remove(volumeKey);
            else if (_deferredVolumeKeys.Contains(volumeKey))
                return null;
            if (!forceReevaluation && CanReuseSafeCompletion(volume) && _safeCompletedVolumeKeys.Contains(volumeKey))
                return null;
            bool sameCurrentVolume = string.Equals(
                _activeVolumeKey, volumeKey, StringComparison.OrdinalIgnoreCase);
            if (_configurationChangeInProgress || _stateRepairInProgress)
            {
                if (_activeTask is not { IsCompleted: false } && !sameCurrentVolume)
                    QueuePendingVolumeNoLock(volume, forceReevaluation);
                return null;
            }
            if (_activeTask is { IsCompleted: false } ||
                (_currentVolumeBlocked && !sameCurrentVolume))
            {
                if (!sameCurrentVolume)
                    QueuePendingVolumeNoLock(volume, forceReevaluation);
                return null;
            }
            return StartTransferNoLock(volume, forceReevaluation);
        }
    }

    private void QueuePendingVolumeNoLock(VolumeEventArgs volume, bool forceReevaluation)
    {
        string volumeKey = VolumeKey(volume);
        int existingIndex = _pendingVolumes.FindIndex(value => string.Equals(
            VolumeKey(value.Volume), volumeKey, StringComparison.OrdinalIgnoreCase));
        if (existingIndex < 0)
        {
            _pendingVolumes.Add(new PendingVolumeWork(volume, forceReevaluation));
        }
        else
        {
            PendingVolumeWork existing = _pendingVolumes[existingIndex];
            _pendingVolumes[existingIndex] = new PendingVolumeWork(
                volume, existing.ForceReevaluation || forceReevaluation);
        }
        ReindexQueuedMediaNoLock();
    }

    private Task StartTransferNoLock(VolumeEventArgs volume, bool forceReevaluation)
    {
        string volumeKey = VolumeKey(volume);
        if (forceReevaluation)
            _safeCompletedVolumeKeys.Remove(volumeKey);
        _transferStatus.Stop();
        _lastPublishedProgressSequence = 0;
        _taskCts?.Dispose();
        _taskCts = new CancellationTokenSource();
        Guid operationId = Guid.NewGuid();
        _activeOperationId = operationId;
        _lastArrivedVolume = volume;
        _activeVolumeKey = volumeKey;
        _currentVolumeBlocked = false;
        UpdateMediaNoLock(volumeKey, media => media with
        {
            WorkState = "scanning",
            QueuePosition = null,
            SafetyConclusion = "keep_inserted",
            ReasonCode = "IDENTIFYING_CARD",
            PrimaryAction = string.Empty,
            AvailableActions = [],
            Detail = "已检测到介质，正在识别素材卡。",
        });
        _activeTask = RunTransferAsync(volume, operationId, _taskCts.Token);
        return _activeTask;
    }

    private void StartNextPendingVolume()
    {
        lock (_gate)
        {
            _activeTask = null;
            _activeOperationId = null;
            _activeVolumeKey = null;
            _currentVolumeBlocked = false;
            if (_stopping || _configurationChangeInProgress || _stateRepairInProgress ||
                _pendingVolumes.Count == 0)
                return;

            PendingVolumeWork pending = _pendingVolumes[0];
            _pendingVolumes.RemoveAt(0);
            ReindexQueuedMediaNoLock();
            _lastArrivedVolume = pending.Volume;
            StartTransferNoLock(pending.Volume, pending.ForceReevaluation);
        }
        RaiseMediaStatusChanged();
    }

    private async Task RunTransferAsync(VolumeEventArgs volume, Guid operationId, CancellationToken cancellationToken)
    {
        Guid? resumeTaskId = null;
        bool taskJournalInitialized = false;
        bool cardReinitializationAvailable = false;
        bool cardReassociationAvailable = false;
        bool completionReverificationAvailable = false;
        bool sourceCleanupReviewAvailable = false;
        Guid? sourceCleanupReviewCardInstanceId = null;
        Guid? cardReassociationCardInstanceId = null;
        string? cardReassociationPreviousSourceIdentity = null;
        string? observedSourceIdentity = null;
        string volumeKey = VolumeKey(volume);
        bool contentWitnessInvalidated;
        bool fullReverificationAuthorized;
        bool sourceCleanupAuthorized;
        lock (_gate)
        {
            contentWitnessInvalidated = _contentWitnessInvalidatedVolumeKeys.Contains(volumeKey);
            fullReverificationAuthorized =
                _completionReverificationAuthorizedVolumeKeys.Contains(volumeKey);
            sourceCleanupAuthorized =
                _sourceCleanupReviewAuthorizedVolumeKeys.Contains(volumeKey);
        }
        bool currentVolumeSafe = false;
        // Publish the non-safe handoff synchronously before the first asynchronous
        // configuration or storage read, so a previous card's green completion page
        // cannot remain visible after this card becomes the active operation.
        SetOperationStatus(operationId, PreparingStatus(
            "正在确认当前素材卡",
            "已切换到当前素材卡；完成本卡的复制与校验前，不会沿用其他卡的安全状态。",
            StandaloneTargetMode.LocalAndNas,
            operationId));
        try
        {
            lock (_gate)
            {
                _activeVolumeKey = volumeKey;
                _safeCompletedVolumeKeys.Remove(volumeKey);
                if (string.Equals(
                        _cardReinitializationAuthorizedVolumeKey,
                        volumeKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _cardReinitializationAuthorizedVolumeKey = null;
                    _cardReinitializationAuthorizedSourceIdentity = null;
                    _cardReassociationAuthorizedCardInstanceId = null;
                    _cardReassociationAuthorizedPreviousSourceIdentity = null;
                }
            }

            StandaloneConfiguration? configuration = await _configurationStore.LoadAsync(cancellationToken);
            if (configuration is null || !configuration.Validate().IsValid)
            {
                SetOperationTerminalStatus(operationId, FailureStatus("首次设置未完成", "请先完成素材范围和保存方式设置。"));
                return;
            }
            configuration = configuration.NormalizeForCurrentSchema();

            StandaloneTargetMode targetMode = configuration.TargetMode;
            string sourceRoot = Path.GetFullPath(volume.DriveLetter);
            var faultDomains = new FaultDomainResolver();
            FaultDomainInfo sourceDomain = faultDomains.ResolveLocalDomain(sourceRoot);
            observedSourceIdentity = sourceDomain.StorageIdentity;

            var taskCatalog = new StandaloneCompletedTaskCatalog(_paths);
            var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
            IReadOnlyList<StandaloneInventoryBaseline> sourceBaselines;
            try
            {
                sourceBaselines = await baselineStore.FindAllBySourceIdentityAsync(
                    sourceDomain.StorageIdentity!, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                cardReinitializationAvailable = true;
                throw;
            }

            StandaloneInventoryBaseline? tentativeBaseline = sourceBaselines.Count == 1
                ? sourceBaselines[0]
                : null;
            StandaloneCardProfile? tentativeProfile = tentativeBaseline is null
                ? null
                : configuration.CardProfiles.SingleOrDefault(profile =>
                    profile.CardInstanceId == tentativeBaseline.CardInstanceId);
            StandaloneCameraTemplate? tentativeKnownTemplate =
                tentativeProfile?.CameraTemplateId is Guid profileTemplateId
                    ? configuration.EffectiveCameraTemplates.SingleOrDefault(template =>
                        template.TemplateId == profileTemplateId)
                    : tentativeBaseline?.CameraTemplateId is Guid baselineTemplateId
                        ? configuration.EffectiveCameraTemplates.SingleOrDefault(template =>
                            template.TemplateId == baselineTemplateId)
                        : null;
            IReadOnlyList<string> expectedDirectories =
                tentativeKnownTemplate?.ApprovedSourceDirectories ??
                configuration.DefaultCameraTemplate.ApprovedSourceDirectories;
            IReadOnlyList<string> observedCandidateDirectories = ObservedTopLevelDirectories(sourceRoot);
            string volumeKeyForMedia = VolumeKey(volume);
            UpdateMedia(volumeKeyForMedia, media => media with
            {
                PresenceState = "mounted",
                EligibilityState = "checking",
                IdentityState = tentativeBaseline is not null
                    ? tentativeProfile is null ? "legacy_card_needs_profile" : "known_card"
                    : sourceBaselines.Count > 1 ? "needs_confirmation" : "unknown",
                CardInstanceId = tentativeBaseline?.CardInstanceId.ToString("D") ?? string.Empty,
                CardDisplayName = tentativeProfile?.DisplayName ??
                    (tentativeBaseline is null
                        ? string.Empty
                        : $"历史素材卡 {tentativeBaseline.CardInstanceId:N}"[..13]),
                CameraTemplateId = tentativeKnownTemplate?.TemplateId.ToString("D") ?? string.Empty,
                ExpectedDirectories = expectedDirectories.ToArray(),
                ObservedCandidateDirectories = observedCandidateDirectories,
                WorkState = "scanning",
                SafetyConclusion = "keep_inserted",
                ReasonCode = "CHECKING_APPROVED_SOURCE_RANGE",
                Detail = tentativeBaseline is null
                    ? "已检测到外接介质，正在确认素材范围和卡片身份。"
                    : "已找到这张卡的历史记录，正在确认当前素材范围。",
            });
            if (!ContainsApprovedSourceDirectory(sourceRoot, configuration))
            {
                bool knownCard = tentativeBaseline is not null;
                string headline = knownCard
                    ? tentativeProfile is null
                        ? "检测到一张需要恢复档案的历史素材卡"
                        : $"已识别“{tentativeProfile.DisplayName}”，但原素材目录不存在"
                    : $"已检测到 {volume.DriveLetter}，但未找到已批准素材目录";
                string description = knownCard
                    ? $"当前卡中没有原素材范围 {string.Join("、", expectedDirectories)}。这通常发生在格式化、换相机或目录结构变化后；AutoCardSync 尚未处理当前内容。"
                    : $"当前卡中没有素材范围 {string.Join("、", expectedDirectories)}。你可以把它设为新素材卡、恢复到历史卡，或忽略本次插入。";
                UpdateMedia(volumeKeyForMedia, media => media with
                {
                    EligibilityState = "approved_range_missing",
                    WorkState = "awaiting_action",
                    SafetyConclusion = "no_backup_conclusion",
                    ReasonCode = knownCard
                        ? tentativeProfile is null
                            ? "LEGACY_CARD_PROFILE_MISSING"
                            : "KNOWN_CARD_APPROVED_DIRECTORY_MISSING"
                        : "APPROVED_DIRECTORY_MISSING",
                    PrimaryAction = "configure_card_scope",
                    AvailableActions =
                    [
                        "configure_card_scope",
                        "treat_as_new_card",
                        "ignore_this_mount",
                    ],
                    Detail = description,
                });
                SetOperationStatus(operationId, WaitingStatus(true, headline, description));
                currentVolumeSafe = true; // No active reads; this volume must not block other cards.
                return;
            }

            string? configuredLocalRoot = targetMode.RequiresLocal()
                ? Path.GetFullPath(configuration.LocalTargetPath)
                : null;
            string? configuredNasRoot = targetMode.RequiresNas()
                ? Path.GetFullPath(configuration.NasMappedTargetPath)
                : null;
            EnsureNoOverlap(sourceRoot, configuredLocalRoot, configuredNasRoot);

            StandaloneCameraTemplate? preliminaryTemplate = null;
            SourceSelectionPolicy preliminaryPolicy;
            if (tentativeBaseline is not null || configuration.EffectiveCameraTemplates.Count == 1)
            {
                try
                {
                    preliminaryTemplate = ResolveCameraTemplate(
                        configuration, tentativeBaseline, resumeCandidate: null, sourceRoot);
                }
                catch (InvalidDataException)
                {
                    cardReinitializationAvailable = true;
                    throw;
                }
                preliminaryPolicy = new SourceSelectionPolicy(
                    preliminaryTemplate.ApprovedSourceDirectories,
                    preliminaryTemplate.NormalizedExtensions);
            }
            else
            {
                preliminaryPolicy = IdentityCandidateSelectionPolicy(configuration);
            }

            Guid inventoryTaskId = CreateTaskId(volume.VolumeGuid, DateTimeOffset.UtcNow);
            var manifestBuilder = new ManifestBuilder(new FileSystemSourceEnumerator());
            SetOperationStatus(operationId, PreparingStatus("正在检查新增素材", "正在快速读取批准目录的文件元数据。", targetMode, operationId));
            TaskManifest fullInventory = await manifestBuilder.BuildAsync(
                inventoryTaskId,
                sourceRoot,
                "standalone-v1-metadata",
                SourceHashPolicy.MetadataOnly,
                preliminaryPolicy,
                cancellationToken);
            UpdateMedia(volumeKey, media => media with
            {
                EligibilityState = fullInventory.TotalFiles == 0
                    ? "no_matching_files"
                    : "matching_files_found",
                SelectedFileCount = fullInventory.TotalFiles,
                WorkState = "scanning",
                SafetyConclusion = "keep_inserted",
                ReasonCode = "IDENTIFYING_CARD_FROM_SELECTED_CONTENT",
                Detail = fullInventory.TotalFiles == 0
                    ? "已找到素材范围，当前没有匹配文件；正在确认卡片身份。"
                    : $"已找到 {fullInventory.TotalFiles} 个匹配素材，正在确认卡片身份。",
            });

            CardIdentityEvidence observed = StandaloneCardEvidenceBuilder.Build(
                sourceRoot, sourceDomain, fullInventory, volume.FileSystem, volume.Capacity);
            var cardResolver = new StandaloneCardIdentityResolver(_paths.CardIdentityFile);
            StandaloneCardIdentityResolution card;
            Guid? expectedHistoricalCardId = tentativeBaseline?.CardInstanceId;
            try
            {
                await cardResolver.EnsureReadableAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                cardReinitializationAvailable = true;
                if (expectedHistoricalCardId is Guid damagedMapCardId && tentativeBaseline is not null)
                {
                    cardReassociationAvailable = true;
                    cardReassociationCardInstanceId = damagedMapCardId;
                    cardReassociationPreviousSourceIdentity = tentativeBaseline.SourceIdentity;
                }
                throw new InvalidDataException(
                    "本地素材卡身份记录已损坏。原文件不会被静默删除；请使用失败页的重新初始化入口保留损坏副本并重建当前卡片绑定。",
                    exception);
            }
            if (expectedHistoricalCardId is Guid expectedCardId)
            {
                try
                {
                    await cardResolver.EnsureExpectedMountedSnapshotAsync(
                        expectedCardId, volume.FileSystem, volume.Capacity, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    cardReinitializationAvailable = true;
                    throw;
                }
                bool baselineContinuityProven = tentativeBaseline is not null &&
                    (HasBaselineObjectContinuity(tentativeBaseline, fullInventory) ||
                        HasDeletionSubsetContinuity(tentativeBaseline, fullInventory));
                card = await cardResolver.ResolveExpectedAsync(
                    expectedCardId,
                    observed,
                    cancellationToken,
                    baselineContinuityProven,
                    allowContentChangeOnProvenMount: contentWitnessInvalidated &&
                        CanReuseSafeCompletion(volume));
                if (card.NeedsConfirmation && CanReuseSafeCompletion(volume))
                {
                    StandaloneCardIdentityBinding? binding = await cardResolver.FindBindingAsync(
                        expectedCardId, cancellationToken);
                    string fingerprintConflictKey =
                        $"candidate:{expectedCardId:D}:fingerprint_conflict";
                    bool exactVerifiedWitnessCandidate = binding?.SafeCompletionEvidence is not null &&
                        card.EvidenceSummary.Count == 1 &&
                        card.EvidenceSummary.ContainsKey(fingerprintConflictKey) &&
                        string.Equals(
                            binding.SafeCompletionEvidence.SampleFingerprint,
                            observed.SampleFingerprint,
                            StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(observed.SampleFingerprint) &&
                        string.Equals(
                            binding.SafeCompletionEvidence.RootDirectoryHash,
                            observed.RootDirectoryHash,
                            StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(observed.RootDirectoryHash);
                    if (exactVerifiedWitnessCandidate)
                    {
                        try
                        {
                            object? verifiedCompletion = await TryRestoreCompletedStatusAsync(
                                configuration,
                                sourceRoot,
                                tentativeBaseline,
                                sourceDomain,
                                faultDomains,
                                cancellationToken);
                            if (verifiedCompletion is not null)
                            {
                                card = await cardResolver.ResolveExpectedAsync(
                                    expectedCardId,
                                    observed,
                                    cancellationToken,
                                    baselineContinuityProven: true,
                                    allowVerifiedSafeCompletionEvidence: true);
                            }
                        }
                        catch (Exception exception) when (
                            exception is IOException or InvalidDataException or UnauthorizedAccessException)
                        {
                            completionReverificationAvailable = true;
                            throw new IOException(
                                "最近完成任务的持久化证据无法重新验证，不能确认安全拔卡。请保留旧记录并重新开始；AutoCardSync 将重新复制并完整校验当前全部批准素材。",
                                exception);
                        }
                    }
                }
            }
            else if (sourceBaselines.Count > 1)
            {
                var autoCandidates = new List<(StandaloneCardIdentityResolution Resolution,
                    StandaloneCameraTemplate Template, TaskManifest Inventory)>();
                int rejectedCandidates = 0;
                foreach (StandaloneInventoryBaseline candidateBaseline in sourceBaselines)
                {
                    StandaloneCameraTemplate candidateTemplate;
                    try
                    {
                        candidateTemplate = ResolveCameraTemplate(
                            configuration, candidateBaseline, resumeCandidate: null, sourceRoot);
                    }
                    catch (InvalidDataException)
                    {
                        rejectedCandidates++;
                        continue;
                    }

                    // A template from another known card is not evidence about the mounted card
                // when its approved roots do not exist on this medium. Do not let an unrelated
                // card's missing folder block the explicitly selected card rebind.
                if (candidateTemplate.ApprovedSourceDirectories.Any(relativeRoot =>
                        !Directory.Exists(SafePathResolver.ResolveSafePath(sourceRoot, relativeRoot))))
                {
                    continue;
                }
                SourceSelectionPolicy candidatePolicy = new(
                        candidateTemplate.ApprovedSourceDirectories,
                        candidateTemplate.NormalizedExtensions);
                    TaskManifest candidateInventory = await manifestBuilder.BuildAsync(
                        inventoryTaskId,
                        sourceRoot,
                        "standalone-v1-card-candidate",
                        SourceHashPolicy.MetadataOnly,
                        candidatePolicy,
                        cancellationToken);
                    CardIdentityEvidence candidateObserved = StandaloneCardEvidenceBuilder.Build(
                        sourceRoot, sourceDomain, candidateInventory, volume.FileSystem, volume.Capacity);
                    try
                    {
                        await cardResolver.EnsureExpectedMountedSnapshotAsync(
                            candidateBaseline.CardInstanceId,
                            volume.FileSystem,
                            volume.Capacity,
                            cancellationToken);
                        StandaloneCardIdentityResolution candidate = await cardResolver.ResolveExpectedAsync(
                            candidateBaseline.CardInstanceId,
                            candidateObserved,
                            cancellationToken,
                            HasBaselineObjectContinuity(candidateBaseline, candidateInventory) ||
                                HasDeletionSubsetContinuity(candidateBaseline, candidateInventory));
                        if (!candidate.NeedsConfirmation && candidate.CardInstanceId is not null)
                            autoCandidates.Add((candidate, candidateTemplate, candidateInventory));
                    }
                    catch (InvalidDataException)
                    {
                        rejectedCandidates++;
                    }
                }

                if (autoCandidates.Count == 1)
                {
                    (card, preliminaryTemplate, fullInventory) = autoCandidates[0];
                }
                else
                {
                    card = new StandaloneCardIdentityResolution(
                        null,
                        "NeedsIdentityConfirmation",
                        true,
                        new Dictionary<string, string>
                        {
                            ["sameEndpointCandidates"] = sourceBaselines.Count.ToString(
                                CultureInfo.InvariantCulture),
                            ["autoCandidates"] = autoCandidates.Count.ToString(
                                CultureInfo.InvariantCulture),
                            ["rejectedCandidates"] = rejectedCandidates.ToString(
                                CultureInfo.InvariantCulture),
                        });
                }
            }
            else
            {
                card = await cardResolver.ResolveAsync(observed, cancellationToken);
            }
            if (card.NeedsConfirmation || card.CardInstanceId is null)
            {
                cardReinitializationAvailable = true;
                Guid? candidateCardId = card.CardInstanceId ??
                    tentativeBaseline?.CardInstanceId;
                bool contentFingerprintConflict = card.EvidenceSummary.Keys.Any(key =>
                    key.EndsWith(":fingerprint_conflict", StringComparison.Ordinal));
                if (contentFingerprintConflict && candidateCardId is Guid)
                {
                    lock (_gate)
                        _contentWitnessInvalidatedVolumeKeys.Add(volumeKey);
                }
                if (candidateCardId is Guid candidateId)
                {
                    StandaloneInventoryBaseline? historicalBaseline =
                        await baselineStore.FindByCardInstanceIdAsync(candidateId, cancellationToken);
                    if (historicalBaseline is not null)
                    {
                        cardReassociationAvailable = true;
                        cardReassociationCardInstanceId = candidateId;
                        cardReassociationPreviousSourceIdentity = historicalBaseline.SourceIdentity;
                        StandaloneTaskJournal? crossReaderResume =
                            await taskCatalog.FindResumeCandidateByCardInstanceIdAsync(
                                candidateId, cancellationToken);
                        resumeTaskId ??= crossReaderResume?.TaskId;
                        throw new IOException(string.Equals(
                                historicalBaseline.SourceIdentity,
                                sourceDomain.StorageIdentity,
                                StringComparison.Ordinal)
                            ? "当前介质与历史素材卡共享相同存储端点，但内容连续性不足以自动确认。请仅在确实是同一张卡时确认同卡；若是新卡，请明确重新初始化。系统没有使用旧基线，也没有吞掉当前素材差分。"
                            : "当前介质与一张历史素材卡高度相似，但读卡器或卷端点已变化。请确认“这是同一张卡”以保留旧基线并检查新增素材，或仅在确认为新卡时重新初始化；系统不会自动吞掉当前素材差分。");
                    }
                }
                UpdateMedia(volumeKey, media => media with
                {
                    IdentityState = "needs_confirmation",
                    CardInstanceId = candidateCardId?.ToString("D") ?? string.Empty,
                    WorkState = "awaiting_action",
                    SafetyConclusion = "no_backup_conclusion",
                    ReasonCode = "CARD_IDENTITY_CONFIRMATION_REQUIRED",
                    PrimaryAction = cardReassociationAvailable ? "reassociate_card" : "treat_as_new_card",
                    AvailableActions = cardReassociationAvailable
                        ? ["reassociate_card", "treat_as_new_card", "defer_current_card"]
                        : ["treat_as_new_card", "defer_current_card"],
                    Detail = "无法安全自动确认卡片身份，因此没有开始复制。",
                });
                throw new IOException("这张素材卡需要人工确认后才能自动复制。当前任务已停止。");
            }

            Guid cardInstanceId = card.CardInstanceId.Value;
            StandaloneCardProfile? resolvedProfile = configuration.CardProfiles.SingleOrDefault(profile =>
                profile.CardInstanceId == cardInstanceId);
            UpdateMedia(volumeKey, media => media with
            {
                IdentityState = resolvedProfile is null ? "legacy_card_needs_profile" : "known_card",
                CardInstanceId = cardInstanceId.ToString("D"),
                CardDisplayName = resolvedProfile?.DisplayName ??
                    $"素材卡 {cardInstanceId:N}"[..12],
                EligibilityState = "approved_range_found",
                WorkState = "scanning",
                SafetyConclusion = "keep_inserted",
                ReasonCode = string.Equals(card.Decision, "NewCard", StringComparison.Ordinal)
                    ? "NEW_CARD_DETECTED"
                    : "KNOWN_CARD_RECOGNIZED",
                Detail = string.Equals(card.Decision, "NewCard", StringComparison.Ordinal)
                    ? "发现新素材卡；当前素材将先复制并完整校验，再建立完成基线。"
                    : "已识别历史素材卡，正在检查新增或变化的素材。",
            });
            StandaloneInventoryBaseline? baseline = await baselineStore.FindByCardInstanceIdAsync(
                cardInstanceId, cancellationToken);
            StandaloneTaskJournal? resumeCandidate = await taskCatalog.FindResumeCandidateByCardInstanceIdAsync(
                cardInstanceId, cancellationToken);
            resumeTaskId = resumeCandidate?.TaskId;
            if (baseline?.InitializationPending == true)
            {
                cardReinitializationAvailable = true;
                throw new InvalidDataException(
                    "上次素材卡初始化在提交完成前中断。当前内容仍未进入基线；请明确重新初始化以恢复同一初始化事务。");
            }
            if (baseline is not null && !string.Equals(
                    baseline.SourceIdentity,
                    sourceDomain.StorageIdentity,
                    StringComparison.Ordinal))
            {
                cardReinitializationAvailable = true;
                cardReassociationAvailable = true;
                cardReassociationCardInstanceId = cardInstanceId;
                cardReassociationPreviousSourceIdentity = baseline.SourceIdentity;
                throw new IOException(
                    "素材卡身份已匹配历史记录，但当前读卡器或卷端点与旧基线不同。请确认这是同一张卡后再迁移基线；系统不会自动吞掉当前素材差分。");
            }
            if (resumeCandidate is not null && resumeCandidate.TargetMode != targetMode)
            {
                throw new IOException(
                    "上次未完成任务的保存方式与当前设置不一致。请恢复原保存方式继续任务，或确认保留旧记录并重新开始；当前素材差分和基线均未改动。");
            }
            if (baseline is null && resumeCandidate is null &&
                configuration.CardProfiles.Any(profile => profile.CardInstanceId == cardInstanceId))
            {
                cardReinitializationAvailable = true;
                throw new InvalidDataException(
                    "已识别出这张历史素材卡，但其元数据基线缺失或已被隔离。系统不会把当前内容静默当成首次基线。请检查基线备份；如确认接受重新建立边界，可明确重新初始化当前卡。 ");
            }
            if (baseline is not null && resumeCandidate is not null &&
                baseline.CardInstanceId != resumeCandidate.CardInstanceId)
            {
                throw new IOException("素材卡基线与恢复日志的卡片身份不一致，已失败关闭。");
            }

            StandaloneCameraTemplate cameraTemplate;
            try
            {
                cameraTemplate = ResolveCameraTemplate(
                    configuration, baseline, resumeCandidate, sourceRoot);
            }
            catch (InvalidDataException)
            {
                cardReinitializationAvailable = true;
                throw;
            }
            Guid taskId = resumeCandidate?.TaskId ?? inventoryTaskId;
            var selectionPolicy = new SourceSelectionPolicy(
                cameraTemplate.ApprovedSourceDirectories,
                cameraTemplate.NormalizedExtensions);
            string selectionPolicyHash = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                cameraTemplate.ApprovedSourceDirectories,
                cameraTemplate.NormalizedExtensions);
            if (taskId != fullInventory.TaskId ||
                preliminaryTemplate is null ||
                cameraTemplate.TemplateId != preliminaryTemplate.TemplateId)
            {
                fullInventory = await manifestBuilder.BuildAsync(
                    taskId,
                    sourceRoot,
                    "standalone-v1-metadata",
                    SourceHashPolicy.MetadataOnly,
                    selectionPolicy,
                    cancellationToken);
            }
            bool resumeFrozenContent = resumeCandidate?.ContentManifestFrozen == true;
            await _configurationService.EnsureCardProfileAsync(
                cardInstanceId,
                cameraTemplate.TemplateId,
                cancellationToken);

            StandaloneInventoryDelta delta = StandaloneInventoryBaselineStore.Compare(
                baseline,
                selectionPolicyHash,
                fullInventory,
                IdentityChangesRequireTransfer(volume.FileSystem));
            UpdateMedia(volumeKey, media => media with
            {
                EligibilityState = fullInventory.TotalFiles == 0
                    ? "no_matching_files"
                    : "matching_files_found",
                SelectedFileCount = fullInventory.TotalFiles,
                DeltaFileCount = delta.TransferEntries.Count,
                WorkState = "scanning",
                SafetyConclusion = "keep_inserted",
                ReasonCode = delta.TransferEntries.Count > 0
                    ? "CARD_DELTA_FOUND"
                    : "CARD_DELTA_CHECKING_COMPLETION_EVIDENCE",
                Detail = delta.TransferEntries.Count > 0
                    ? $"已识别素材卡，发现 {delta.TransferEntries.Count} 个新增或变化素材。"
                    : "已识别素材卡，没有发现新增素材；正在重新验证最近完成记录。",
            });
            if (resumeCandidate is null && baseline is not null && delta.TransferEntries.Count > 0)
            {
                RecoveredBaselineState? recovered = await TryRecoverUnadvancedCompletionsAsync(
                    configuration,
                    sourceRoot,
                    baselineStore,
                    baseline,
                    sourceDomain,
                    faultDomains,
                    cameraTemplate,
                    selectionPolicyHash,
                    selectionPolicy,
                    fullInventory,
                    delta,
                    IdentityChangesRequireTransfer(volume.FileSystem),
                    cancellationToken);
                if (recovered is not null)
                {
                    baseline = recovered.Baseline;
                    delta = recovered.Delta;
                }
            }
            if (resumeCandidate is null && baseline is null)
            {
                // A non-empty new card must not be silently absorbed into a historical
                // baseline. Commit an empty initialization boundary first so every
                // currently selected object remains a transfer delta after a restart.
                await baselineStore.ReinitializePendingAsync(
                    cardInstanceId,
                    cameraTemplate.TemplateId,
                    sourceDomain.StorageIdentity!,
                    selectionPolicyHash,
                    observed,
                    abandonOtherPending: false,
                    cancellationToken);
                await baselineStore.CommitInitializationAsync(cardInstanceId, cancellationToken);
                FaultDomainInfo baselineSource = faultDomains.ResolveLocalDomain(sourceRoot);
                if (!string.Equals(
                        baselineSource.StorageIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal))
                {
                    throw new IOException("素材卡身份在基线持久化期间发生变化，不能确认初始化完成。");
                }

                baseline = await baselineStore.FindByCardInstanceIdAsync(
                    cardInstanceId,
                    cancellationToken) ??
                    throw new InvalidDataException("新素材卡的初始化边界未能持久化。");
                delta = StandaloneInventoryBaselineStore.Compare(
                    baseline,
                    selectionPolicyHash,
                    fullInventory,
                    IdentityChangesRequireTransfer(volume.FileSystem));
                UpdateMedia(volumeKey, media => media with
                {
                    IdentityState = "known_card",
                    CardInstanceId = cardInstanceId.ToString("D"),
                    CardDisplayName = resolvedProfile?.DisplayName ??
                        $"素材卡 {cardInstanceId:N}"[..12],
                    CameraTemplateId = cameraTemplate.TemplateId.ToString("D"),
                    ExpectedDirectories = cameraTemplate.ApprovedSourceDirectories.ToArray(),
                    EligibilityState = fullInventory.TotalFiles == 0
                        ? "no_matching_files"
                        : "matching_files_found",
                    SelectedFileCount = fullInventory.TotalFiles,
                    DeltaFileCount = fullInventory.TotalFiles,
                    WorkState = fullInventory.TotalFiles == 0 ? "completed" : "scanning",
                    SafetyConclusion = fullInventory.TotalFiles == 0
                        ? "no_backup_conclusion"
                        : "keep_inserted",
                    ReasonCode = fullInventory.TotalFiles == 0
                        ? "EMPTY_CARD_INITIALIZED"
                        : "NEW_CARD_FULL_IMPORT_REQUIRED",
                    Detail = fullInventory.TotalFiles == 0
                        ? "已建立空卡档案；当前没有需要备份的批准素材。"
                        : $"发现新素材卡，当前 {fullInventory.TotalFiles} 个批准素材将全部复制并完整校验。",
                });
                if (fullInventory.TotalFiles == 0)
                {
                    await RefreshCardSafetyEvidenceAsync(
                        cardResolver,
                        cardInstanceId,
                        sourceRoot,
                        sourceDomain,
                        fullInventory,
                        volume,
                        cancellationToken);
                    RememberSafeCompletion(volume);
                    currentVolumeSafe = true;
                    SetOperationTerminalStatus(operationId, BaselineReadyStatus(
                        "空素材卡已初始化",
                        $"已按素材范围“{cameraTemplate.Name}”建立空基线；当前没有素材被复制。"));
                    return;
                }
            }
            if (!delta.SelectionPolicyMatches)
            {
                if (resumeCandidate is not null)
                {
                    throw new IOException(
                        "批准目录或扩展名策略已变化，但仍有未完成任务。请先恢复原设置并完成任务。");
                }

                cardReinitializationAvailable = true;
                throw new IOException(
                    "批准目录或扩展名策略与这张卡的历史基线不一致。系统没有更新基线，也没有把当前新增或被改写的素材吞入历史。请恢复原模板设置继续同步；如确需更换模板，请先在设置中选定新的默认模板，再明确确认重新初始化。");
            }

            bool sourceCleanupDetected = resumeCandidate is null && delta.MissingPaths.Count > 0;
            if (sourceCleanupDetected && sourceCleanupAuthorized)
            {
                Guid? expectedCleanupCardId;
                lock (_gate)
                {
                    expectedCleanupCardId = _sourceCleanupReviewCardIds.TryGetValue(
                        volumeKey,
                        out Guid rememberedCardId)
                        ? rememberedCardId
                        : null;
                    if (expectedCleanupCardId != card.CardInstanceId)
                    {
                        sourceCleanupAuthorized = false;
                        _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
                    }
                }
            }
            if (sourceCleanupDetected && !sourceCleanupAuthorized)
            {
                sourceCleanupReviewAvailable = true;
                sourceCleanupReviewCardInstanceId = card.CardInstanceId;
                throw new IOException(
                    $"检测到 {delta.MissingPaths.Count} 个历史素材已从卡中删除。系统没有把删除结果写入基线，也不会因此锁死这张卡；如果这些文件确实由你主动清理，请确认后继续。");
            }
            if (sourceCleanupDetected &&
                !fullReverificationAuthorized &&
                baseline is { LastCompletedTaskId: not null } cleanupBaseline)
            {
                try
                {
                    object? completedStatus = await TryRestoreCompletedStatusAsync(
                        configuration,
                        sourceRoot,
                        cleanupBaseline,
                        sourceDomain,
                        faultDomains,
                        cancellationToken);
                    if (completedStatus is null)
                        throw new InvalidDataException("找不到最近完成任务的可验证回执。");
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    completionReverificationAvailable = true;
                    throw new IOException(
                        "已收到素材清理确认，但最近完成任务的目标证据无法重新验证。系统没有更新基线，也没有显示安全完成；可保留旧记录并按当前素材重新开始完整复制与校验。",
                        exception);
                }
            }

            // A cleanup plus new/changed material must produce one fresh receipt that covers
            // every currently retained object. Otherwise the new delta receipt would replace
            // the historical task id while proving only the newly copied subset.
            bool cleanupRequiresFullReverification =
                sourceCleanupDetected &&
                sourceCleanupAuthorized &&
                resumeCandidate is null &&
                delta.TransferEntries.Count > 0;
            IReadOnlyList<ManifestEntry> transferEntries = IncludeContentWitnessTransferEntries(
                fullInventory,
                delta.TransferEntries,
                contentWitnessInvalidated && resumeCandidate is null,
                (fullReverificationAuthorized || cleanupRequiresFullReverification) &&
                    resumeCandidate is null);
            if (resumeCandidate is null && transferEntries.Count == 0)
            {
                bool baselineNeedsRefresh = delta.MissingPaths.Count > 0 ||
                    delta.IdentityChangedPaths.Count > 0;
                if (!baselineNeedsRefresh)
                {
                    try
                    {
                        object? completedStatus = await TryRestoreCompletedStatusAsync(
                            configuration,
                            sourceRoot,
                            baseline,
                            sourceDomain,
                            faultDomains,
                            cancellationToken);
                        if (completedStatus is not null)
                        {
                            await RefreshCardSafetyEvidenceAsync(
                                cardResolver,
                                card.CardInstanceId.Value,
                                sourceRoot,
                                sourceDomain,
                                fullInventory,
                                volume,
                                cancellationToken);
                            RememberSafeCompletion(volume);
                            currentVolumeSafe = true;
                            SetOperationTerminalStatus(operationId, completedStatus);
                            return;
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        completionReverificationAvailable = true;
                        throw new IOException(
                            "最近完成任务的持久化证据无法重新验证，不能确认安全拔卡。请保留旧记录并重新开始；AutoCardSync 将重新复制并完整校验当前全部批准素材。",
                            exception);
                    }
                }
                FaultDomainInfo unchangedSource = faultDomains.ResolveLocalDomain(sourceRoot);
                if (!string.Equals(
                        unchangedSource.StorageIdentity,
                        sourceDomain.StorageIdentity,
                        StringComparison.Ordinal))
                {
                    throw new IOException("素材卡身份在新增素材检查期间发生变化。");
                }

                if (baselineNeedsRefresh)
                {
                    if (sourceCleanupDetected)
                        await baselineStore.ArchiveCurrentAsync(card.CardInstanceId.Value, cancellationToken);
                    await baselineStore.RefreshObservedAsync(
                        card.CardInstanceId.Value,
                        cameraTemplate.TemplateId,
                        sourceDomain.StorageIdentity!,
                        selectionPolicyHash,
                        fullInventory,
                        cancellationToken);
                    FaultDomainInfo refreshedSource = faultDomains.ResolveLocalDomain(sourceRoot);
                    if (!string.Equals(
                            refreshedSource.StorageIdentity,
                            sourceDomain.StorageIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new IOException("素材卡身份在元数据基线整理期间发生变化。");
                    }
                }

                await RefreshCardSafetyEvidenceAsync(
                    cardResolver,
                    card.CardInstanceId.Value,
                    sourceRoot,
                    sourceDomain,
                    fullInventory,
                    volume,
                    cancellationToken);
                RememberSafeCompletion(volume);
                string headline = sourceCleanupDetected
                    ? "已确认素材清理"
                    : baselineNeedsRefresh
                        ? "素材卡基线已自动整理"
                        : "没有发现新增素材";
                string description = sourceCleanupDetected
                    ? $"已保留清理前的历史基线快照，并按你的确认移除 {delta.MissingPaths.Count} 个不再存在的历史路径。未读取已删除内容，也未写入保存目标。"
                    : baselineNeedsRefresh
                        ? $"已接受 {delta.IdentityChangedPaths.Count} 个仅文件系统标识变化的条目。未读取素材内容，也未写入保存目标。"
                    : "批准目录中的文件元数据与已保存基线一致，可以安全拔卡。";
                currentVolumeSafe = true;
                SetOperationTerminalStatus(operationId, BaselineReadyStatus(headline, description));
                return;
            }

            cardReinitializationAvailable = false;
            TaskManifest manifest;
            if (resumeCandidate is null)
            {
                manifest = ManifestBuilder.CreateDeltaInventoryManifest(
                    fullInventory,
                    transferEntries.Select(entry => entry.RelativePath).ToArray());
            }
            else
            {
                string[] recoveryPaths = resumeCandidate.Files
                    .Select(file => file.RelativePath)
                    .ToArray();
                if (baseline is not null)
                {
                    var addedPaths = new HashSet<string>(
                        transferEntries.Select(entry => entry.RelativePath),
                        StringComparer.OrdinalIgnoreCase);
                    if (recoveryPaths.Any(path => !addedPaths.Contains(path)))
                        throw new IOException("恢复日志包含不属于当前新增素材差分的文件，已失败关闭。");
                }
                TaskManifest recoveryInventory = ManifestBuilder.CreateDeltaInventoryManifest(
                    fullInventory,
                    recoveryPaths);
                EnsureFrozenRecoveryInventoryBinding(recoveryInventory, resumeCandidate);
                if (resumeFrozenContent)
                {
                    manifest = ManifestBuilder.CreateContentManifest(
                        recoveryInventory,
                        resumeCandidate.Files.ToDictionary(
                            file => file.FileId,
                            file => file.SourceSha256));
                    ManifestJournalBindingValidator.EnsureValid(manifest, resumeCandidate);
                }
                else
                {
                    manifest = recoveryInventory;
                    EnsureInventoryJournalBinding(manifest, resumeCandidate);
                }
            }

            ResolvedTargetRoots targetRoots = ResolveTargetRoots(configuration, manifest, resumeCandidate);
            FaultDomainInfo? localDomain = null;
            ResolvedSecondaryTarget? secondaryTarget = null;
            if (targetMode.RequiresLocal())
            {
                localDomain = PrepareLocalTargetRoot(
                    faultDomains,
                    sourceDomain,
                    configuration.LocalTargetPath,
                    targetRoots.LocalRoot!);
            }
            if (targetMode.RequiresNas())
            {
                secondaryTarget = ResolveSecondaryTarget(
                    faultDomains,
                    configuration.NasMappedTargetPath,
                    targetRoots.NasRoot!,
                    isResume: resumeCandidate is not null);
            }
            EnsureNoOverlap(sourceRoot, targetRoots.LocalRoot, secondaryTarget?.CopyRoot);
            EnsureIndependentStorageSet(
                faultDomains,
                sourceDomain,
                localDomain,
                secondaryTarget?.Domain);

            Guid localTargetId = DeterministicGuid("local", manifest.TaskId);
            Guid nasTargetId = DeterministicGuid("nas", manifest.TaskId);
            string journalLocation = GetJournalLocation(manifest.TaskId);
            var journalStore = new StandaloneTaskJournalStore(journalLocation);
            StandaloneTaskJournal? existingJournal = await journalStore.LoadAsync(cancellationToken);
            if (existingJournal is null)
            {
                await journalStore.InitializeAsync(new StandaloneTaskJournal
                {
                    SchemaVersion = 2,
                    TaskId = manifest.TaskId,
                    CardInstanceId = card.CardInstanceId.Value,
                    SourceIdentity = sourceDomain.StorageIdentity!,
                    TargetMode = targetMode,
                    InventoryManifestHash = manifest.ManifestHash,
                    ManifestHash = string.Empty,
                    ContentManifestFrozen = false,
                    LocalTargetIdentity = localDomain?.StorageIdentity ?? string.Empty,
                    LocalTargetRoot = targetRoots.LocalRoot ?? string.Empty,
                    NasTargetIdentity = secondaryTarget?.Domain.StorageIdentity ?? string.Empty,
                    NasTargetRoot = secondaryTarget?.CopyRoot ?? string.Empty,
                    Files = manifest.Entries.Where(entry => !entry.Excluded).Select(entry => NewFileJournal(
                        entry,
                        targetMode,
                        localTargetId,
                        localDomain?.StorageIdentity,
                        nasTargetId,
                        secondaryTarget?.Domain.StorageIdentity)).ToArray(),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                }, cancellationToken);
            }
            else
            {
                if (existingJournal.ContentManifestFrozen)
                {
                    ManifestJournalBindingValidator.EnsureValid(manifest, existingJournal);
                }
                else
                {
                    EnsureInventoryJournalBinding(manifest, existingJournal);
                }
                RecoveryEligibility eligibility = RecoveryGuard.Evaluate(
                    existingJournal,
                    new RecoveryIdentitySnapshot(
                        sourceDomain.StorageIdentity!,
                        card.CardInstanceId.Value,
                        existingJournal.ContentManifestFrozen ? manifest.ManifestHash : string.Empty,
                        localDomain?.StorageIdentity ?? string.Empty,
                        secondaryTarget?.Domain.StorageIdentity ?? string.Empty,
                        targetMode,
                        existingJournal.ContentManifestFrozen ? existingJournal.InventoryManifestHash : manifest.ManifestHash));
                if (!eligibility.CanResume)
                    throw new IOException("上次任务的恢复身份不一致，已失败关闭：" + string.Join(", ", eligibility.Reasons));
                if ((targetMode.RequiresLocal() &&
                     !string.Equals(existingJournal.LocalTargetRoot, targetRoots.LocalRoot, StringComparison.OrdinalIgnoreCase)) ||
                    (targetMode.RequiresNas() &&
                     !string.Equals(existingJournal.NasTargetRoot, secondaryTarget?.CopyRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new IOException("上次任务的目标路径和当前配置不一致，已失败关闭。");
                }
            }

            taskJournalInitialized = true;
            lock (_gate)
            {
                _activeTargetMode = targetMode;
                _transferStatus.StartTask(
                    manifest.TaskId,
                    localTargetId,
                    nasTargetId,
                    manifest.TotalBytes,
                    manifest.TotalFiles,
                    targetMode);
                _lastPublishedProgressSequence = 0;
            }

            TaskManifest contentManifest;
            StandaloneSafetyResult safety;
            if (!resumeFrozenContent)
            {
                var plans = new List<FreshTargetPlan>(2);
                if (targetMode.RequiresLocal())
                {
                    plans.Add(new FreshTargetPlan(
                        localTargetId,
                        "local",
                        targetRoots.LocalRoot!,
                        localDomain!.FaultDomainId,
                        localDomain.StorageIdentity!,
                        NasIdentity: null,
                        new Progress<TargetCopyStatus>(status => PublishTransferProgress(isLocal: true, status))));
                }
                if (targetMode.RequiresNas())
                {
                    plans.Add(new FreshTargetPlan(
                        nasTargetId,
                        "nas",
                        secondaryTarget!.CopyRoot,
                        secondaryTarget.Domain.FaultDomainId,
                        secondaryTarget.Domain.StorageIdentity!,
                        secondaryTarget.NasSystemId,
                        new Progress<TargetCopyStatus>(status => PublishTransferProgress(isLocal: false, status))));
                }

                var coordinator = new FreshTransferCoordinator();
                await using FreshTransferExecution execution = await coordinator.ExecuteAsync(
                    manifest,
                    sourceRoot,
                    plans,
                    journalStore,
                    resumeExistingTemps: resumeCandidate is not null,
                    cancellationToken,
                    sourceProgress: new Progress<SourceReadStatus>(PublishSourceProgress));
                contentManifest = execution.ContentManifest;
                _logger.LogInformation(
                    "Fresh transfer I/O: payload={Payload}, source={Source}, relayReads={RelayReads}, writes={Writes}, tempReads={TempReads}, finalReads={FinalReads}, amplification={Amplification:F3}, journalBytes={JournalBytes}, peakLeases={PeakLeases}, peakReadAhead={PeakReadAhead}.",
                    execution.Metrics.PayloadBytes,
                    execution.Metrics.SourceContentBytesRead,
                    execution.Metrics.RelayBytesRead,
                    execution.Metrics.TargetBytesWritten,
                    execution.Metrics.TargetTemporaryBytesRead,
                    execution.Metrics.TargetFinalBytesRead,
                    execution.Metrics.FreshIoAmplification,
                    execution.Metrics.JournalBytesWritten,
                    execution.Metrics.PeakIdentityLeaseCount,
                    execution.Metrics.PeakReadAheadBeyondDurableCheckpointBytes);
                safety = await PersistCompletionAsync(
                    contentManifest,
                    journalStore,
                    card.CardInstanceId.Value,
                    sourceDomain.StorageIdentity!,
                    localDomain?.StorageIdentity ?? string.Empty,
                    secondaryTarget?.Domain.StorageIdentity ?? string.Empty,
                    _ =>
                    {
                        execution.RevalidateContinuity();
                        return Task.CompletedTask;
                    },
                    cancellationToken);
            }
            else
            {
                contentManifest = manifest;
                var plans = new List<FreshTargetPlan>(2);
                if (targetMode.RequiresLocal())
                {
                    plans.Add(new FreshTargetPlan(
                        localTargetId,
                        "local",
                        targetRoots.LocalRoot!,
                        localDomain!.FaultDomainId,
                        localDomain.StorageIdentity!,
                        NasIdentity: null,
                        new Progress<TargetCopyStatus>(status => PublishTransferProgress(isLocal: true, status))));
                }
                if (targetMode.RequiresNas())
                {
                    plans.Add(new FreshTargetPlan(
                        nasTargetId,
                        "nas",
                        secondaryTarget!.CopyRoot,
                        secondaryTarget.Domain.FaultDomainId,
                        secondaryTarget.Domain.StorageIdentity!,
                        secondaryTarget.NasSystemId,
                        new Progress<TargetCopyStatus>(status => PublishTransferProgress(isLocal: false, status))));
                }

                var finalizer = new FrozenTransferFinalizer();
                await using FreshTransferExecution execution = await finalizer.FinalizeAsync(
                    contentManifest,
                    sourceRoot,
                    plans,
                    journalStore,
                    cancellationToken);
                if (execution.Results.Any(result => !result.Verified))
                    throw new IOException("至少一个必需目标没有完成恢复校验，不能安全拔卡。");
                _logger.LogInformation(
                    "Frozen recovery I/O: payload={Payload}, source={Source}, writes={Writes}, tempReads={TempReads}, finalReads={FinalReads}, journalBytes={JournalBytes}.",
                    execution.Metrics.PayloadBytes,
                    execution.Metrics.SourceContentBytesRead,
                    execution.Metrics.TargetBytesWritten,
                    execution.Metrics.TargetTemporaryBytesRead,
                    execution.Metrics.TargetFinalBytesRead,
                    execution.Metrics.JournalBytesWritten);
                safety = await PersistCompletionAsync(
                    contentManifest,
                    journalStore,
                    card.CardInstanceId.Value,
                    sourceDomain.StorageIdentity!,
                    localDomain?.StorageIdentity ?? string.Empty,
                    secondaryTarget?.Domain.StorageIdentity ?? string.Empty,
                    _ =>
                    {
                        execution.RevalidateContinuity();
                        return Task.CompletedTask;
                    },
                    cancellationToken);
            }

            if (baseline is not null && delta.MissingPaths.Count > 0)
                await baselineStore.ArchiveCurrentAsync(card.CardInstanceId.Value, cancellationToken);
            await baselineStore.AdvanceAfterCompletionAsync(
                card.CardInstanceId.Value,
                cameraTemplate.TemplateId,
                sourceDomain.StorageIdentity!,
                selectionPolicyHash,
                fullInventory,
                contentManifest.TaskId,
                cancellationToken);
            await RefreshCardSafetyEvidenceAsync(
                cardResolver,
                card.CardInstanceId.Value,
                sourceRoot,
                sourceDomain,
                fullInventory,
                volume,
                cancellationToken);
            RememberSafeCompletion(volume);
            currentVolumeSafe = true;
            SetOperationTerminalStatus(operationId, CompleteStatusForMode(contentManifest, safety, targetMode));
        }
        catch (OperationCanceledException)
        {
            SetOperationTerminalStatus(operationId, FailureStatus("任务已停止", "任务没有完成所选目标的全部校验，不可以安全拔卡。"));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Standalone transfer failed closed.");
            lock (_gate)
            {
                if (completionReverificationAvailable)
                {
                    _safeCompletedVolumeKeys.Remove(volumeKey);
                    _completionReverificationRequiredVolumeKeys.Add(volumeKey);
                }
                if (sourceCleanupReviewAvailable)
                {
                    _safeCompletedVolumeKeys.Remove(volumeKey);
                    _sourceCleanupReviewRequiredVolumeKeys.Add(volumeKey);
                    _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
                    if (sourceCleanupReviewCardInstanceId is Guid cleanupCardId)
                        _sourceCleanupReviewCardIds[volumeKey] = cleanupCardId;
                }
                else if (sourceCleanupAuthorized && !completionReverificationAvailable)
                {
                    _sourceCleanupReviewAuthorizedVolumeKeys.Remove(volumeKey);
                }
                _cardReinitializationAuthorizedVolumeKey = cardReinitializationAvailable &&
                    !string.IsNullOrWhiteSpace(observedSourceIdentity)
                    ? volumeKey
                    : null;
                _cardReinitializationAuthorizedSourceIdentity =
                    _cardReinitializationAuthorizedVolumeKey is null ? null : observedSourceIdentity;
                _cardReassociationAuthorizedCardInstanceId = cardReassociationAvailable
                    ? cardReassociationCardInstanceId
                    : null;
                _cardReassociationAuthorizedPreviousSourceIdentity = cardReassociationAvailable
                    ? cardReassociationPreviousSourceIdentity
                    : null;
            }
            SetOperationTerminalStatus(operationId, FailureStatus(
                "复制未安全完成",
                UserFacingFailureMessage(exception),
                canRestartFresh: completionReverificationAvailable ||
                    resumeTaskId.HasValue || taskJournalInitialized,
                canReinitializeCard: cardReinitializationAvailable,
                canReassociateCard: cardReassociationAvailable,
                canConfirmSourceCleanup: sourceCleanupReviewAvailable));
        }
        finally
        {
            if (currentVolumeSafe)
                StartNextPendingVolume();
            else
                CompleteFailedCurrentVolume(volumeKey);
        }
    }

    private void CompleteFailedCurrentVolume(string failedVolumeKey)
    {
        bool startPending;
        lock (_gate)
        {
            _activeTask = null;
            bool failedVolumeStillMounted = string.Equals(
                _activeVolumeKey, failedVolumeKey, StringComparison.OrdinalIgnoreCase);
            _currentVolumeBlocked = failedVolumeStillMounted;
            startPending = !failedVolumeStillMounted &&
                !_configurationChangeInProgress &&
                !_stopping &&
                _pendingVolumes.Count > 0;
        }
        if (startPending)
            StartNextPendingVolume();
    }

    private async Task<RecoveredBaselineState?> TryRecoverUnadvancedCompletionsAsync(
        StandaloneConfiguration configuration,
        string sourceRoot,
        StandaloneInventoryBaselineStore baselineStore,
        StandaloneInventoryBaseline baseline,
        FaultDomainInfo sourceDomain,
        FaultDomainResolver faultDomains,
        StandaloneCameraTemplate cameraTemplate,
        string selectionPolicyHash,
        SourceSelectionPolicy selectionPolicy,
        TaskManifest fullInventory,
        StandaloneInventoryDelta delta,
        bool identityChangesRequireTransfer,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StandaloneCompletedTaskCandidate> candidates;
        try
        {
            candidates = await new StandaloneCompletedTaskCatalog(_paths)
                .FindCompletedCandidatesAsync(sourceDomain.StorageIdentity!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsCandidateRecoveryException(exception))
        {
            _logger.LogWarning(
                exception,
                "Completed-task catalog could not be inspected for an unadvanced baseline; normal delta copy remains available.");
            return null;
        }

        StandaloneInventoryBaseline currentBaseline = baseline;
        StandaloneInventoryDelta currentDelta = delta;
        bool recoveredAny = false;
        foreach (StandaloneCompletedTaskCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandaloneTaskJournal journal = candidate.Journal;
            if (journal.TaskId == currentBaseline.LastCompletedTaskId ||
                journal.TargetMode != configuration.TargetMode ||
                journal.CardInstanceId != currentBaseline.CardInstanceId ||
                journal.Files.Count == 0)
            {
                continue;
            }

            HashSet<string> pendingPaths = currentDelta.TransferEntries
                .Select(entry => entry.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] completedPaths = journal.Files
                .Select(file => file.RelativePath)
                .ToArray();
            if (completedPaths.Any(path => !pendingPaths.Contains(path)))
                continue;

            TaskManifest completedInventory;
            try
            {
                TaskManifest candidateInventory = await new ManifestBuilder(new FileSystemSourceEnumerator()).BuildAsync(
                    journal.TaskId,
                    sourceRoot,
                    "standalone-v1-completed-baseline-recovery",
                    SourceHashPolicy.MetadataOnly,
                    selectionPolicy,
                    cancellationToken);
                completedInventory = ManifestBuilder.CreateDeltaInventoryManifest(
                    candidateInventory,
                    completedPaths);
                EnsureFrozenRecoveryInventoryBinding(completedInventory, journal);
                TaskManifest contentManifest = ManifestBuilder.CreateContentManifest(
                    completedInventory,
                    journal.Files.ToDictionary(file => file.FileId, file => file.SourceSha256));
                ManifestJournalBindingValidator.EnsureValid(contentManifest, journal);
                _ = RebuildCompletedStatus(
                    configuration,
                    sourceRoot,
                    currentBaseline.CardInstanceId,
                    sourceDomain,
                    faultDomains,
                    candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsCandidateRecoveryException(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Completed task {TaskId} was not eligible to advance the missing baseline commit; normal delta copy remains available.",
                    journal.TaskId);
                continue;
            }

            await baselineStore.MergeRecoveredCompletionAsync(
                currentBaseline.CardInstanceId,
                cameraTemplate.TemplateId,
                sourceDomain.StorageIdentity!,
                selectionPolicyHash,
                completedInventory,
                journal.TaskId,
                cancellationToken);
            FaultDomainInfo persistedSource = faultDomains.ResolveLocalDomain(sourceRoot);
            if (!string.Equals(
                    persistedSource.StorageIdentity,
                    sourceDomain.StorageIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException("素材卡身份在恢复已完成任务的基线提交期间发生变化。");
            }

            currentBaseline = await baselineStore.FindByCardInstanceIdAsync(
                currentBaseline.CardInstanceId, cancellationToken) ??
                throw new IOException("已完成任务的基线提交恢复后无法按素材卡身份重新读取。");
            currentDelta = StandaloneInventoryBaselineStore.Compare(
                currentBaseline,
                selectionPolicyHash,
                fullInventory,
                identityChangesRequireTransfer);
            recoveredAny = true;
        }

        return recoveredAny ? new(currentBaseline, currentDelta) : null;
    }

    private static bool IsCandidateRecoveryException(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException or
            System.Security.SecurityException or System.Text.Json.JsonException;
    private async Task<object?> TryRestoreCompletedStatusAsync(
        StandaloneConfiguration configuration,
        string sourceRoot,
        StandaloneInventoryBaseline? baseline,
        FaultDomainInfo sourceDomain,
        FaultDomainResolver faultDomains,
        CancellationToken cancellationToken)
    {
        if (baseline?.LastCompletedTaskId is not Guid completedTaskId)
            return null;

        var catalog = new StandaloneCompletedTaskCatalog(_paths);
        StandaloneCompletedTaskCandidate? candidate = await catalog.FindCompletedCandidateAsync(
            sourceDomain.StorageIdentity!,
            completedTaskId,
            cancellationToken);
        if (candidate is null)
            throw new IOException("最近完成任务的 Journal 或 Receipt 缺失，不能恢复安全完成状态。");

        return RebuildCompletedStatus(
            configuration,
            sourceRoot,
            baseline.CardInstanceId,
            sourceDomain,
            faultDomains,
            candidate);
    }

    private static object RebuildCompletedStatus(
        StandaloneConfiguration configuration,
        string sourceRoot,
        Guid cardInstanceId,
        FaultDomainInfo sourceDomain,
        FaultDomainResolver faultDomains,
        StandaloneCompletedTaskCandidate candidate)
    {
        StandaloneTaskJournal journal = candidate.Journal;
        if (journal.TargetMode != configuration.TargetMode)
            throw new IOException("最近完成任务的保存方式与当前配置不一致。");

        string localRoot = string.Empty;
        string nasRoot = string.Empty;
        FaultDomainInfo? localDomain = null;
        ResolvedSecondaryTarget? secondaryTarget = null;
        if (journal.TargetMode.RequiresLocal())
        {
            localRoot = Path.GetFullPath(journal.LocalTargetRoot);
            EnsureConfiguredLocalTargetContains(configuration.LocalTargetPath, localRoot);
            if (!Directory.Exists(localRoot))
                throw new IOException("最近完成任务的本地保存位置当前不可用。");
            localDomain = faultDomains.ResolveLocalDomain(localRoot);
        }
        if (journal.TargetMode.RequiresNas())
        {
            secondaryTarget = ResolveSecondaryTarget(
                faultDomains,
                configuration.NasMappedTargetPath,
                journal.NasTargetRoot,
                isResume: true);
            nasRoot = secondaryTarget.CopyRoot;
        }
        EnsureNoOverlap(
            sourceRoot,
            journal.TargetMode.RequiresLocal() ? localRoot : null,
            journal.TargetMode.RequiresNas() ? nasRoot : null);
        EnsureIndependentStorageSet(faultDomains, sourceDomain, localDomain, secondaryTarget?.Domain);

        FaultDomainInfo currentSource = faultDomains.ResolveLocalDomain(sourceRoot);
        if (!string.Equals(
                currentSource.StorageIdentity,
                sourceDomain.StorageIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException("素材卡身份在持久化状态重建期间发生变化。");
        }

        var current = new RecoveryIdentitySnapshot(
            currentSource.StorageIdentity!,
            cardInstanceId,
            journal.ManifestHash,
            localDomain?.StorageIdentity ?? string.Empty,
            secondaryTarget?.Domain.StorageIdentity ?? string.Empty,
            journal.TargetMode,
            journal.InventoryManifestHash);
        return RestoreCompletedStatus(candidate, current, localRoot, nasRoot);
    }

    private static object RestoreCompletedStatus(
        StandaloneCompletedTaskCandidate candidate,
        RecoveryIdentitySnapshot current,
        string currentLocalTargetRoot,
        string currentNasTargetRoot)
    {
        CompletedTaskReuseEligibility eligibility = CompletedTaskReuseGuard.EvaluatePersisted(
            candidate.Journal,
            candidate.Receipt,
            current,
            currentLocalTargetRoot,
            currentNasTargetRoot);
        if (!eligibility.CanReuse)
        {
            throw new IOException(
                "最近完成任务的持久化证据不再一致：" + string.Join(", ", eligibility.Reasons));
        }

        return CompleteStatusFromPersisted(
            candidate.Journal.TaskId,
            candidate.Journal.Files.Count,
            new StandaloneSafetyResult(true, []),
            candidate.Journal.TargetMode);
    }

    private async Task<StandaloneSafetyResult> PersistCompletionAsync(
        TaskManifest manifest,
        StandaloneTaskJournalStore journalStore,
        Guid cardInstanceId,
        string sourceIdentity,
        string localIdentity,
        string nasIdentity,
        Func<CancellationToken, Task> revalidateContinuity,
        CancellationToken cancellationToken)
    {
        await revalidateContinuity(cancellationToken);
        StandaloneTaskJournal journal = await journalStore.LoadAsync(cancellationToken) ??
            throw new IOException("任务恢复日志无法重新读取。");
        ManifestJournalBindingValidator.EnsureValid(manifest, journal);
        StandaloneSafetyResult preReceiptSafety = BuildSafety(
            manifest,
            journal,
            sourceIdentity,
            localIdentity,
            nasIdentity,
            completionReceiptPersisted: false);
        if (preReceiptSafety.SafeToRemoveCard ||
            preReceiptSafety.UnmetConditions.Count != 1 ||
            !string.Equals(
                preReceiptSafety.UnmetConditions[0],
                "LOCAL_COMPLETION_RECEIPT_PERSISTED",
                StringComparison.Ordinal))
        {
            throw new IOException(
                "完成收据写入前仍有其他安全条件未满足，不能安全拔卡：" +
                string.Join(", ", preReceiptSafety.UnmetConditions));
        }

        string receiptPath = _paths.GetCompletionReceiptPath(manifest.TaskId);
        var receiptStore = new AtomicJsonFileStore<StandaloneCompletionReceipt>(receiptPath);
        await receiptStore.SaveAsync(
            CreateReceipt(manifest, journal, cardInstanceId, safeToRemoveCard: true),
            cancellationToken);
        StandaloneCompletionReceipt receipt = await receiptStore.LoadAsync(cancellationToken) ??
            throw new IOException("完成收据写入后无法重新读取，不能安全拔卡。");
        StandaloneCompletionReceiptValidation validation =
            StandaloneCompletionReceiptValidator.Evaluate(receipt, journal);
        if (!validation.IsValid)
        {
            throw new IOException(
                "完成收据与最终校验事实不一致，不能安全拔卡：" +
                string.Join(", ", validation.Reasons));
        }

        await journalStore.MarkCompletionReceiptPersistedAsync(cancellationToken);
        journal = await journalStore.LoadAsync(cancellationToken) ??
            throw new IOException("完成收据状态写入后无法重新读取。");
        ManifestJournalBindingValidator.EnsureValid(manifest, journal);
        await revalidateContinuity(cancellationToken);
        validation = StandaloneCompletionReceiptValidator.Evaluate(receipt, journal);
        StandaloneSafetyResult safety = BuildSafety(
            manifest,
            journal,
            sourceIdentity,
            localIdentity,
            nasIdentity,
            completionReceiptPersisted: journal.LocalCompletionReceiptPersisted && validation.IsValid);
        if (!safety.SafeToRemoveCard)
            throw new IOException("完成收据未能绑定到最终校验事实，不能安全拔卡。");
        return safety;
    }

    private string GetJournalLocation(Guid taskId)
    {
        string sharded = _paths.GetTaskJournalDirectory(taskId);
        string legacy = _paths.GetTaskJournalPath(taskId);
        if (Directory.Exists(sharded))
            return sharded;
        if (File.Exists(legacy))
            return legacy;
        return sharded;
    }

    private static void EnsureFrozenRecoveryInventoryBinding(
        TaskManifest inventoryManifest,
        StandaloneTaskJournal journal)
    {
        if (journal.SchemaVersion < 2 ||
            inventoryManifest.TaskId != journal.TaskId ||
            !string.Equals(
                inventoryManifest.ManifestHash,
                journal.InventoryManifestHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("恢复日志与当前新增素材元数据清单不一致。");
        }

        Dictionary<Guid, ManifestEntry> inventory = inventoryManifest.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.Id);
        if (inventory.Count != journal.Files.Count)
            throw new InvalidDataException("恢复日志文件数量与当前新增素材清单不一致。");
        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (!inventory.TryGetValue(file.FileId, out ManifestEntry? entry) ||
                entry.FileSize != file.Length ||
                !string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.SourceFileId, file.SourceFileIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException("恢复日志中的源文件元数据身份已变化。");
            }
            if (string.IsNullOrWhiteSpace(file.SourceSha256))
                throw new InvalidDataException("冻结恢复日志缺少源 SHA-256 事实。");
        }
    }

    private static void EnsureInventoryJournalBinding(
        TaskManifest inventoryManifest,
        StandaloneTaskJournal journal)
    {
        if (inventoryManifest.TaskId != journal.TaskId ||
            !string.Equals(
                inventoryManifest.ManifestHash,
                journal.InventoryManifestHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("库存清单与恢复日志不一致。");
        }

        Dictionary<Guid, ManifestEntry> inventory = inventoryManifest.Entries
            .Where(entry => !entry.Excluded)
            .ToDictionary(entry => entry.Id);
        if (inventory.Count != journal.Files.Count)
            throw new InvalidDataException("库存清单文件数量与恢复日志不一致。");
        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (!inventory.TryGetValue(file.FileId, out ManifestEntry? entry) ||
                entry.FileSize != file.Length ||
                !string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("库存清单文件事实与恢复日志不一致。");
            }
        }
    }

    private static StandaloneFileJournal NewFileJournal(
        ManifestEntry entry,
        StandaloneTargetMode targetMode,
        Guid localTargetId,
        string? localIdentity,
        Guid nasTargetId,
        string? nasIdentity) => new()
        {
            FileId = entry.Id,
            RelativePath = entry.RelativePath,
            Length = entry.FileSize,
            SourceSha256 = entry.SourceHash ?? string.Empty,
            State = StandaloneFileState.Pending,
            LocalTarget = NewTarget(
                localTargetId,
                "local",
                localIdentity,
                targetMode.RequiresLocal()),
            NasTarget = NewTarget(
                nasTargetId,
                "nas",
                nasIdentity,
                targetMode.RequiresNas()),
        };

    private static StandaloneTargetFileJournal NewTarget(
        Guid targetId,
        string role,
        string? identity,
        bool required) => new()
        {
            TargetId = targetId,
            TargetRole = role,
            TargetIdentity = required ? identity ?? string.Empty : string.Empty,
            TemporaryPath = string.Empty,
            State = required ? StandaloneTargetState.Pending : StandaloneTargetState.NotRequired,
        };

    private static FaultDomainInfo PrepareLocalTargetRoot(
        FaultDomainResolver faultDomains,
        FaultDomainInfo sourceDomain,
        string configuredRoot,
        string desiredRoot)
    {
        string frozenRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        string finalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(desiredRoot));
        if (!IsSameOrChild(finalRoot, frozenRoot))
            throw new IOException("本地任务目录超出已配置的保存位置。");

        string existingAncestor = frozenRoot;
        while (!Directory.Exists(existingAncestor))
        {
            string? parent = Directory.GetParent(existingAncestor)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, existingAncestor, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"本地保存磁盘不可用：{frozenRoot}。系统没有创建任务目录。");
            }
            existingAncestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        }

        FaultDomainInfo? openedDomain = null;
        string sentinel = Path.Combine(finalRoot, ".autocardsync-preflight");
        using TargetDirectoryContinuityLease lease = TargetDirectoryContinuityLease.Acquire(
            existingAncestor,
            sentinel,
            (handle, path) =>
            {
                openedDomain = faultDomains.ResolveLocalDomain(handle, path);
                EnsureIndependentStorageSet(
                    faultDomains,
                    sourceDomain,
                    openedDomain,
                    nas: null);
            });
        lease.EnsureContinuous();
        return openedDomain ?? throw new IOException("无法冻结本地保存位置的物理身份。");
    }

    private static ResolvedTargetRoots ResolveTargetRoots(
        StandaloneConfiguration configuration,
        TaskManifest manifest,
        StandaloneTaskJournal? resumeCandidate)
    {
        if (resumeCandidate is not null)
        {
            return new(
                configuration.TargetMode.RequiresLocal() ? resumeCandidate.LocalTargetRoot : null,
                configuration.TargetMode.RequiresNas() ? resumeCandidate.NasTargetRoot : null);
        }

        string suffix = configuration.TargetNamingRule switch
        {
            TargetNamingRule.PreserveRelativePath => string.Empty,
            TargetNamingRule.ImportDate => DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + manifest.TaskId.ToString("N")[..8],
            TargetNamingRule.CaptureDate => CaptureDateFolder(manifest) + "-" + manifest.TaskId.ToString("N")[..8],
            _ => throw new InvalidDataException("Unsupported target naming rule."),
        };

        string? local = configuration.TargetMode.RequiresLocal()
            ? AppendSuffix(Path.GetFullPath(configuration.LocalTargetPath), suffix)
            : null;
        string? nas = configuration.TargetMode.RequiresNas()
            ? AppendSuffix(Path.GetFullPath(configuration.NasMappedTargetPath), suffix)
            : null;
        return new(local, nas);
    }

    private static string AppendSuffix(string root, string suffix) =>
        string.IsNullOrEmpty(suffix) ? root : Path.Combine(root, suffix);

    private static string CaptureDateFolder(TaskManifest manifest)
    {
        DateTimeOffset value = manifest.Entries
            .Where(entry => !entry.Excluded)
            .Select(entry => entry.LastModifiedUtc)
            .DefaultIfEmpty(DateTimeOffset.UtcNow)
            .Min();
        return value.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    private sealed record ResolvedTargetRoots(string? LocalRoot, string? NasRoot);

    private sealed record ResolvedSecondaryTarget(string CopyRoot, FaultDomainInfo Domain, string? NasSystemId);

    private static ResolvedSecondaryTarget ResolveSecondaryTarget(
        FaultDomainResolver faultDomains,
        string configuredMappedRoot,
        string desiredCopyRoot,
        bool isResume)
    {
        var resolver = new MappedNetworkTargetResolver();
        MappedNetworkTargetIdentity mapped;
        if (isResume)
        {
            mapped = resolver.CaptureBoundPath(configuredMappedRoot, desiredCopyRoot);
        }
        else
        {
            MappedNetworkTargetIdentity frozenBase = resolver.Capture(configuredMappedRoot);
            string configuredBase = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(configuredMappedRoot));
            string configuredCopyRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(desiredCopyRoot));
            if (!IsSameOrChild(configuredCopyRoot, configuredBase))
                throw new IOException("NAS 目标超出已冻结的映射盘目录。");

            string relative = Path.GetRelativePath(configuredBase, configuredCopyRoot);
            string logicalCopyRoot = relative == "."
                ? frozenBase.LogicalUncPath
                : SafePathResolver.ResolveUncPath(
                    Path.Combine(frozenBase.LogicalUncPath, relative));
            Directory.CreateDirectory(logicalCopyRoot);
            mapped = resolver.CaptureBoundPath(configuredMappedRoot, logicalCopyRoot);
        }

        FaultDomainInfo domain = faultDomains.ResolveNasDomain(
            mapped.LogicalUncPath, mapped.PhysicalServer);
        return new(mapped.LogicalUncPath, domain, domain.NasSystemId);
    }

    private static StandaloneSafetyResult BuildSafety(
        TaskManifest manifest,
        StandaloneTaskJournal journal,
        string sourceIdentity,
        string localIdentity,
        string nasIdentity,
        bool completionReceiptPersisted)
    {
        ManifestJournalBindingValidationResult binding =
            ManifestJournalBindingValidator.Validate(manifest, journal);
        bool sourceUnchanged = string.Equals(journal.SourceIdentity, sourceIdentity, StringComparison.Ordinal);
        bool targetsUnchanged =
            (!journal.TargetMode.RequiresLocal() ||
             string.Equals(journal.LocalTargetIdentity, localIdentity, StringComparison.Ordinal)) &&
            (!journal.TargetMode.RequiresNas() ||
             string.Equals(journal.NasTargetIdentity, nasIdentity, StringComparison.Ordinal));
        bool hasIncludedFiles = manifest.TotalFiles > 0 && journal.Files.Count > 0;
        return StandaloneSafetyDecision.Evaluate(new StandaloneSafetyFacts
        {
            TargetMode = journal.TargetMode,
            ManifestFrozen = journal.ContentManifestFrozen &&
                !string.IsNullOrWhiteSpace(manifest.ManifestHash),
            SourceReadOnly = true,
            AllIncludedFilesAccountedFor = hasIncludedFiles && binding.IsValid,
            LocalTargetFullRereadSha256Passed = !journal.TargetMode.RequiresLocal() ||
                (hasIncludedFiles && journal.Files.All(file => file.LocalTarget.FullRereadSha256Passed)),
            NasTargetFullRereadSha256Passed = !journal.TargetMode.RequiresNas() ||
                (hasIncludedFiles && journal.Files.All(file => file.NasTarget.FullRereadSha256Passed)),
            FinalObjectsSafelyPublishedOrReused = hasIncludedFiles && journal.Files.All(file =>
                (!journal.TargetMode.RequiresLocal() || file.LocalTarget.AtomicallyPublished || file.LocalTarget.ReusedExisting) &&
                (!journal.TargetMode.RequiresNas() || file.NasTarget.AtomicallyPublished || file.NasTarget.ReusedExisting)),
            SourceIdentityUnchanged = sourceUnchanged,
            TargetIdentitiesUnchanged = targetsUnchanged,
            FailedIncludedFiles = journal.Files.Count(file => file.State == StandaloneFileState.Failed),
            PendingIncludedFiles = journal.Files.Count(file => file.State != StandaloneFileState.Verified),
            LocalCompletionReceiptPersisted = completionReceiptPersisted,
        });
    }

    private static SourceSelectionPolicy IdentityCandidateSelectionPolicy(
        StandaloneConfiguration configuration)
    {
        string[] directories = configuration.EffectiveCameraTemplates
            .SelectMany(template => template.ApprovedSourceDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] extensions = configuration.EffectiveCameraTemplates
            .SelectMany(template => template.NormalizedExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SourceSelectionPolicy(directories, extensions);
    }

    private static StandaloneCameraTemplate ResolveReinitializationCameraTemplate(
        StandaloneConfiguration configuration,
        StandaloneInventoryBaseline? baseline,
        string sourceRoot)
    {
        StandaloneCameraTemplate[] templates = configuration.EffectiveCameraTemplates.ToArray();
        StandaloneCameraTemplate defaultTemplate = configuration.DefaultCameraTemplate;
        if (baseline is not null)
        {
            StandaloneCardProfile? profile = configuration.CardProfiles.SingleOrDefault(
                candidate => candidate.CardInstanceId == baseline.CardInstanceId);
            StandaloneCameraTemplate? profiledTemplate = profile is null
                ? null
                : templates.SingleOrDefault(template => template.TemplateId == profile.CameraTemplateId);
            if (profiledTemplate is not null)
                return profiledTemplate;

            StandaloneCameraTemplate? baselineTemplate = baseline.CameraTemplateId is Guid baselineTemplateId
                ? templates.SingleOrDefault(template => template.TemplateId == baselineTemplateId)
                : null;
            if (baselineTemplate is not null)
                return baselineTemplate;

            StandaloneCameraTemplate? policyTemplate = templates.FirstOrDefault(template => string.Equals(
                StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                    template.ApprovedSourceDirectories,
                    template.NormalizedExtensions),
                baseline.SelectionPolicyHash,
                StringComparison.Ordinal));
            if (policyTemplate is not null)
                return policyTemplate;
        }

        StandaloneCameraTemplate[] candidateMatches = templates
            .Where(template => ContainsApprovedCandidateFileForTemplate(sourceRoot, template))
            .ToArray();
        if (candidateMatches.Length == 1)
            return candidateMatches[0];
        return defaultTemplate;
    }

    private static StandaloneCameraTemplate ResolveCameraTemplate(
        StandaloneConfiguration configuration,
        StandaloneInventoryBaseline? baseline,
        StandaloneTaskJournal? resumeCandidate,
        string sourceRoot)
    {
        StandaloneCameraTemplate[] templates = configuration.EffectiveCameraTemplates.ToArray();
        StandaloneCameraTemplate defaultTemplate = configuration.DefaultCameraTemplate;
        Guid? cardInstanceId = baseline?.CardInstanceId ?? resumeCandidate?.CardInstanceId;
        StandaloneCardProfile? profile = cardInstanceId is Guid value
            ? configuration.CardProfiles.SingleOrDefault(candidate => candidate.CardInstanceId == value)
            : null;

        Guid? requiredTemplateId = profile?.CameraTemplateId;
        if (baseline?.CameraTemplateId is Guid baselineTemplateId)
        {
            if (requiredTemplateId is Guid profileTemplateId && profileTemplateId != baselineTemplateId)
                throw new InvalidDataException("素材卡基线与素材卡档案引用了不同的相机模板。");
            requiredTemplateId = baselineTemplateId;
        }
        if (requiredTemplateId is Guid selectedTemplateId)
        {
            return templates.SingleOrDefault(template => template.TemplateId == selectedTemplateId)
                ?? throw new InvalidDataException("素材卡引用的相机模板不存在。");
        }

        if (baseline is not null)
        {
            StandaloneCameraTemplate[] policyMatches = templates.Where(template => string.Equals(
                StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                    template.ApprovedSourceDirectories,
                    template.NormalizedExtensions),
                baseline.SelectionPolicyHash,
                StringComparison.Ordinal)).ToArray();
            if (policyMatches.Length > 0)
            {
                return policyMatches.SingleOrDefault(template => template.TemplateId == defaultTemplate.TemplateId)
                    ?? policyMatches[0];
            }
        }

        StandaloneCameraTemplate[] candidateMatches = templates
            .Where(template => ContainsApprovedCandidateFileForTemplate(sourceRoot, template))
            .ToArray();
        if (candidateMatches.Length == 1)
            return candidateMatches[0];
        if (templates.Length == 1)
            return defaultTemplate;
        throw new InvalidDataException(
            candidateMatches.Length == 0
                ? "当前素材卡无法唯一匹配相机模板。请在设置中选择正确模板并设为默认，然后明确确认重新初始化；系统尚未写入素材卡档案或基线。"
                : "当前素材卡同时匹配多个相机模板。请在设置中消除重叠或选择正确模板并设为默认，然后明确确认重新初始化；系统尚未写入素材卡档案或基线。");
    }

    private static string CreateTransferConfigurationFingerprint(StandaloneConfigurationDto configuration)
    {
        static string JoinNormalized(IEnumerable<string> values) => string.Join(
            "\u001e",
            values.Select(value => value.Trim().ToUpperInvariant())
                .OrderBy(value => value, StringComparer.Ordinal));

        string templates = string.Join("\u001d", configuration.CameraTemplates
            .OrderBy(value => value.TemplateId, StringComparer.Ordinal)
            .Select(value => string.Join("\u001c",
                value.TemplateId.ToUpperInvariant(),
                value.Name.Trim().ToUpperInvariant(),
                JoinNormalized(value.ApprovedSourceDirectories),
                JoinNormalized(value.ApprovedExtensions))));
        string profiles = string.Join("\u001d", configuration.CardProfiles
            .OrderBy(value => value.CardInstanceId, StringComparer.Ordinal)
            .Select(value => string.Join("\u001c",
                value.CardInstanceId.ToUpperInvariant(),
                value.DisplayName.Trim().ToUpperInvariant(),
                value.CameraTemplateId.ToUpperInvariant())));
        return string.Join("\u001f",
            configuration.DefaultCameraTemplateId.ToUpperInvariant(),
            templates,
            profiles,
            configuration.TargetMode,
            configuration.LocalTarget.Trim().ToUpperInvariant(),
            configuration.NasMappedTarget.Trim().ToUpperInvariant(),
            configuration.TargetNamingRule);
    }

    private static void EnsureManifestContainsIncludedFiles(TaskManifest manifest)
    {
        if (manifest.TotalFiles == 0)
        {
            throw new InvalidDataException(
                "所选素材文件夹中没有符合已批准文件类型的文件。请打开设置并勾选实际素材类型（例如 .xml）后重试。");
        }
    }

    private static StandaloneCompletionReceipt CreateReceipt(
        TaskManifest manifest,
        StandaloneTaskJournal journal,
        Guid cardInstanceId,
        bool safeToRemoveCard) => new()
        {
            SchemaVersion = 2,
            TaskId = journal.TaskId,
            CardInstanceId = cardInstanceId,
            TargetMode = journal.TargetMode,
            ManifestHash = manifest.ManifestHash,
            SourceIdentity = journal.SourceIdentity,
            LocalTargetIdentity = journal.LocalTargetIdentity,
            NasTargetIdentity = journal.NasTargetIdentity,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            SafeToRemoveCard = safeToRemoveCard,
            Files = journal.Files.Select(file => new StandaloneCompletionFileFact
            {
                FileId = file.FileId,
                RelativePath = file.RelativePath,
                Length = file.Length,
                SourceSha256 = file.SourceSha256,
                LocalFinalObjectId = file.LocalTarget.FinalObjectIdentity ?? string.Empty,
                LocalFinalSha256 = file.LocalTarget.FinalSha256 ?? string.Empty,
                NasFinalObjectId = file.NasTarget.FinalObjectIdentity ?? string.Empty,
                NasFinalSha256 = file.NasTarget.FinalSha256 ?? string.Empty,
            }).ToArray(),
        };

    private static Guid CreateTaskId(string volumeGuid, DateTimeOffset timestamp)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{volumeGuid}:{timestamp:O}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid DeterministicGuid(string role, Guid taskId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{taskId:N}:{role}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void EnsureIndependentStorageSet(
        FaultDomainResolver resolver,
        FaultDomainInfo source,
        FaultDomainInfo? local,
        FaultDomainInfo? nas)
    {
        if (local is not null)
            EnsureIndependentPair(resolver, source, "素材卡", local, "本地目标");
        if (nas is not null)
            EnsureIndependentPair(resolver, source, "素材卡", nas, "NAS 目标");
        if (local is not null && nas is not null)
            EnsureIndependentPair(resolver, local, "本地目标", nas, "NAS 目标");
        if (local is null && nas is null)
            throw new IOException("当前任务没有选择保存目标。");
    }

    private static void EnsureIndependentPair(
        FaultDomainResolver resolver,
        FaultDomainInfo first,
        string firstLabel,
        FaultDomainInfo second,
        string secondLabel)
    {
        DualTargetValidationResult result = resolver.ValidateIndependence(
            first,
            firstLabel,
            second,
            secondLabel);
        if (!result.IsValid)
            throw new IOException(result.Reason ?? $"{firstLabel}和{secondLabel}不能证明相互独立。");
    }

    private static void EnsureConfiguredLocalTargetContains(string configuredRoot, string completedRoot)
    {
        string normalizedConfiguredRoot = Path.GetFullPath(configuredRoot);
        string normalizedCompletedRoot = Path.GetFullPath(completedRoot);
        if (!IsSameOrChild(normalizedCompletedRoot, normalizedConfiguredRoot))
            throw new IOException("已完成任务的本地目标超出当前配置范围，不能复用安全完成状态。");
    }

    private static void EnsureNoOverlap(
        string sourceRoot,
        string? localTarget,
        string? nasTarget)
    {
        var paths = new List<(string Path, string Label)> { (sourceRoot, "素材卡") };
        if (!string.IsNullOrWhiteSpace(localTarget))
            paths.Add((localTarget, "本地目标"));
        if (!string.IsNullOrWhiteSpace(nasTarget))
            paths.Add((nasTarget, "NAS 目标"));
        for (int firstIndex = 0; firstIndex < paths.Count; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1; secondIndex < paths.Count; secondIndex++)
            {
                string first = Path.GetFullPath(paths[firstIndex].Path);
                string second = Path.GetFullPath(paths[secondIndex].Path);
                if (IsSameOrChild(first, second) || IsSameOrChild(second, first))
                {
                    throw new IOException(
                        $"{paths[firstIndex].Label}和{paths[secondIndex].Label}不能互相包含或重叠。");
                }
            }
        }
    }

    private static bool IsSameOrChild(string child, string parent)
    {
        string normalizedChild = Path.TrimEndingDirectorySeparator(child);
        string normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        return string.Equals(normalizedChild, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedChild.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }


    private static object PreparingStatus(
        string headline,
        string message,
        StandaloneTargetMode targetMode,
        Guid? operationId = null) => new
        {
            view = "copying",
            configured = true,
            targetMode = TargetModeKey(targetMode),
            operationId = operationId?.ToString("D") ?? string.Empty,
            phase = "preparing",
            headline,
            description = message,
            safeToRemoveCard = false,
            overallPercent = 0,
            currentFile = string.Empty,
            currentBytesPerSecond = 0,
            averageBytesPerSecond = 0,
            etaSeconds = 0,
            completedFiles = 0,
            totalFiles = 0,
            safety = PendingSafety(),
            targets = StatusTargets(targetMode, null, null),
        };

    private static object ToStatus(
        StandaloneTransferStatusSnapshot progress,
        object safety) => ToStatusForMode(progress, safety, StandaloneTargetMode.LocalAndNas);

    private static object ToStatusForMode(
        StandaloneTransferStatusSnapshot progress,
        object safety,
        StandaloneTargetMode targetMode,
        Guid? operationId = null) => new
        {
            view = "copying",
            configured = true,
            targetMode = TargetModeKey(targetMode),
            operationId = operationId?.ToString("D") ?? string.Empty,
            taskId = progress.TaskId.ToString("D"),
            phase = CurrentPhase(
            targetMode.RequiresLocal() ? progress.Local : null,
            targetMode.RequiresNas() ? progress.Nas : null),
            headline = "正在处理素材",
            description = "请保持素材卡和所选保存目标连接。",
            safeToRemoveCard = false,
            overallPercent = progress.OverallPercent,
            currentFile = progress.CurrentFile,
            sourceBytesRead = progress.SourceBytesRead,
            sourceBytesPerSecond = progress.SourceBytesPerSecond,
            sourceAverageBytesPerSecond = progress.SourceAverageBytesPerSecond,
            localBytesPerSecond = progress.LocalBytesPerSecond,
            nasBytesPerSecond = progress.NasBytesPerSecond,
            localVerificationBytesPerSecond = progress.LocalVerificationBytesPerSecond,
            nasVerificationBytesPerSecond = progress.NasVerificationBytesPerSecond,
            currentBytesPerSecond = progress.SourceBytesPerSecond,
            averageBytesPerSecond = progress.SourceAverageBytesPerSecond,
            etaSeconds = progress.EtaSeconds,
            completedFiles = progress.CompletedFiles,
            totalFiles = progress.TotalFiles,
            safety,
            targets = StatusTargets(
                targetMode,
                progress.Local,
                progress.Nas,
                progress.LocalBytesPerSecond,
                progress.NasBytesPerSecond,
                progress.LocalVerificationBytesPerSecond,
                progress.NasVerificationBytesPerSecond),
        };

    private static object[] StatusTargets(
        StandaloneTargetMode targetMode,
        TargetCopyStatus? local,
        TargetCopyStatus? nas,
        double localBytesPerSecond = 0,
        double nasBytesPerSecond = 0,
        double localVerificationBytesPerSecond = 0,
        double nasVerificationBytesPerSecond = 0)
    {
        var targets = new List<object>(2);
        if (targetMode.RequiresLocal())
            targets.Add(TargetStatus(
                "local", "本地目标", local, localBytesPerSecond,
                localVerificationBytesPerSecond));
        if (targetMode.RequiresNas())
            targets.Add(TargetStatus(
                "mappedNas", "NAS 目标", nas, nasBytesPerSecond,
                nasVerificationBytesPerSecond));
        return targets.ToArray();
    }

    private static string CurrentPhase(TargetCopyStatus? local, TargetCopyStatus? secondary)
    {
        if (local?.Phase == CopyPhase.FinalVerifying || secondary?.Phase == CopyPhase.FinalVerifying ||
            local?.Phase == CopyPhase.Verifying || secondary?.Phase == CopyPhase.Verifying)
            return "verifying-final";
        if (local?.Phase == CopyPhase.TemporaryVerifying || secondary?.Phase == CopyPhase.TemporaryVerifying)
            return "verifying-temporary";
        if (local?.Phase == CopyPhase.Publishing || secondary?.Phase == CopyPhase.Publishing)
            return "publishing";
        return "copying";
    }

    private static object TargetStatus(
        string kind,
        string label,
        TargetCopyStatus? value,
        double bytesPerSecond = 0,
        double verificationBytesPerSecond = 0)
    {
        long totalBytes = value?.TotalBytes ?? 0;
        long verificationBytes = value is null
            ? 0
            : checked(value.TemporaryBytesVerified + value.FinalBytesVerified);
        double verificationTotalBytes = totalBytes * 2d;
        return new
        {
            kind,
            label,
            bytesPerSecond,
            verificationBytesPerSecond,
            copyEtaSeconds = RemainingSeconds(value?.BytesCopied ?? 0, totalBytes, bytesPerSecond),
            verificationEtaSeconds = RemainingSeconds(
                verificationBytes, verificationTotalBytes, verificationBytesPerSecond),
            verificationBytes,
            verificationTotalBytes,
            currentFile = value?.CurrentFile ?? string.Empty,
            state = value?.Phase switch
            {
                CopyPhase.Copying => "copying",
                CopyPhase.Verifying or CopyPhase.TemporaryVerifying or CopyPhase.FinalVerifying => "verifying",
                CopyPhase.Publishing => "preparing",
                CopyPhase.Completed => "complete",
                CopyPhase.Failed => "failed",
                _ => "waiting",
            },
            copyPercent = Percent(value?.BytesCopied ?? 0, totalBytes),
            verificationPercent = Percent(verificationBytes, verificationTotalBytes),
            detail = value?.Phase switch
            {
                CopyPhase.TemporaryVerifying => "正在完整读取临时文件",
                CopyPhase.Publishing => "临时文件已验证，正在原子发布",
                CopyPhase.FinalVerifying or CopyPhase.Verifying => "正在从最终路径完整读取校验",
                _ => value?.CurrentFile ?? "等待开始",
            },
        };
    }

    private static object CompleteStatus(
        TaskManifest manifest,
        StandaloneSafetyResult safety) =>
        CompleteStatusForMode(manifest, safety, StandaloneTargetMode.LocalAndNas);

    private static object CompleteStatusForMode(
        TaskManifest manifest,
        StandaloneSafetyResult safety,
        StandaloneTargetMode targetMode) =>
        CompleteStatusFromPersisted(manifest.TaskId, manifest.TotalFiles, safety, targetMode);

    private static object CompleteStatusFromPersisted(
        Guid taskId,
        int totalFiles,
        StandaloneSafetyResult safety,
        StandaloneTargetMode targetMode) => new
        {
            view = "complete",
            configured = true,
            targetMode = TargetModeKey(targetMode),
            phase = "complete",
            headline = "可以安全拔卡",
            description = targetMode == StandaloneTargetMode.LocalAndNas
            ? "两个保存目标都已完成发布和完整校验。"
            : "所选保存目标已完成发布和完整校验。",
            taskId,
            taskLabel = $"任务 {taskId:N}",
            safeToRemoveCard = safety.SafeToRemoveCard,
            overallPercent = 100,
            currentFile = string.Empty,
            currentBytesPerSecond = 0,
            averageBytesPerSecond = 0,
            etaSeconds = 0,
            completedFiles = totalFiles,
            totalFiles,
            completionReceiptLabel = "本次完成记录已安全保存到本机",
            safety = ToSafetyPayload(safety, totalFiles, 0, targetMode),
            targets = CompleteTargets(targetMode),
        };

    private static string TargetModeKey(StandaloneTargetMode targetMode) => targetMode switch
    {
        StandaloneTargetMode.NasOnly => "nas-only",
        StandaloneTargetMode.LocalOnly => "local-only",
        StandaloneTargetMode.LocalAndNas => "local-and-nas",
        _ => throw new InvalidDataException("Unsupported target mode."),
    };

    private static object[] CompleteTargets(StandaloneTargetMode targetMode)
    {
        var targets = new List<object>(2);
        if (targetMode.RequiresLocal())
        {
            targets.Add(new
            {
                kind = "local",
                label = "本地目标",
                bytesPerSecond = 0,
                verificationBytesPerSecond = 0,
                copyEtaSeconds = 0,
                verificationEtaSeconds = 0,
                verificationBytes = 0,
                verificationTotalBytes = 0,
                currentFile = string.Empty,
                state = "complete",
                copyPercent = 100,
                verificationPercent = 100,
                detail = "已完成最终校验",
            });
        }
        if (targetMode.RequiresNas())
        {
            targets.Add(new
            {
                kind = "mappedNas",
                label = "NAS 目标",
                bytesPerSecond = 0,
                verificationBytesPerSecond = 0,
                copyEtaSeconds = 0,
                verificationEtaSeconds = 0,
                verificationBytes = 0,
                verificationTotalBytes = 0,
                currentFile = string.Empty,
                state = "complete",
                copyPercent = 100,
                verificationPercent = 100,
                detail = "已完成最终校验",
            });
        }
        return targets.ToArray();
    }

    private static object FailureStatus(
        string title,
        string message,
        bool canRestartFresh = false,
        bool canReinitializeCard = false,
        bool canReassociateCard = false,
        bool canConfirmSourceCleanup = false) => new
        {
            view = "failure",
            configured = true,
            phase = "failed",
            safeToRemoveCard = false,
            failure = new
            {
                title,
                what = message,
                safety = "未满足安全完成条件，请不要依据当前进度拔卡。",
                next = canConfirmSourceCleanup
                    ? "如果这些历史素材确实由你主动删除或已在相机中清理，请确认清理；系统会保留清理前快照，再继续使用这张卡。"
                    : canReassociateCard
                    ? "若只是更换了读卡器，请确认这是同一张卡以保留旧基线并检查新增素材；只有确认为新卡时才重新初始化。"
                    : canRestartFresh
                        ? "先重新检查；若旧任务证据已无法恢复，可保留旧记录并按当前素材重新开始。"
                        : canReinitializeCard
                            ? "先重新检查；若卡片身份、档案或基线持续冲突，可明确确认后重新初始化这张卡。"
                            : "保持当前连接并重新检查；如果错误持续出现，请检查素材范围和保存位置设置。",
                canRestartFresh,
                canReinitializeCard,
                canReassociateCard,
                canConfirmSourceCleanup,
            },
            safety = PendingSafety(),
            targets = Array.Empty<object>(),
        };

    private static object BaselineReadyStatus(string headline, string description) => new
    {
        view = "baseline",
        configured = true,
        phase = "baseline-ready",
        headline,
        description,
        safeToRemoveCard = true,
        baselinePersisted = true,
        currentBytesPerSecond = 0,
        averageBytesPerSecond = 0,
        etaSeconds = 0,
        targets = Array.Empty<object>(),
    };

    private static object WaitingStatus(
        bool configured,
        string headline,
        string? description = null) => new
    {
        view = "waiting",
        configured,
        phase = "waiting",
        headline,
        description = description ??
            (configured ? "插卡后会自动开始复制。" : "请先完成首次图形设置。"),
        safeToRemoveCard = false,
        safety = PendingSafety(),
        targets = Array.Empty<object>(),
    };

    private static object PendingSafety() => new
    {
        manifestFrozen = false,
        sourceReadOnly = false,
        allIncludedFilesAccountedFor = false,
        localTargetFullRereadSha256 = "PENDING",
        nasTargetFullRereadSha256 = "PENDING",
        finalObjectsSafelyAvailable = false,
        sourceIdentityUnchanged = false,
        targetIdentitiesUnchanged = false,
        failedIncludedFiles = 0,
        pendingIncludedFiles = 0,
        localCompletionReceiptPersisted = false,
        safeToRemoveCard = false,
    };

    private static object ToSafetyPayload(
        StandaloneSafetyResult safety,
        int totalFiles,
        int failedFiles,
        StandaloneTargetMode targetMode) => new
        {
            manifestFrozen = safety.SafeToRemoveCard,
            sourceReadOnly = safety.SafeToRemoveCard,
            allIncludedFilesAccountedFor = safety.SafeToRemoveCard,
            localTargetFullRereadSha256 = targetMode.RequiresLocal()
                ? safety.SafeToRemoveCard ? "PASS" : "PENDING"
                : "NOT_REQUIRED",
            nasTargetFullRereadSha256 = targetMode.RequiresNas()
                ? safety.SafeToRemoveCard ? "PASS" : "PENDING"
                : "NOT_REQUIRED",
            finalObjectsSafelyAvailable = safety.SafeToRemoveCard,
            sourceIdentityUnchanged = safety.SafeToRemoveCard,
            targetIdentitiesUnchanged = safety.SafeToRemoveCard,
            failedIncludedFiles = failedFiles,
            pendingIncludedFiles = safety.SafeToRemoveCard ? 0 : totalFiles,
            localCompletionReceiptPersisted = safety.SafeToRemoveCard,
            safeToRemoveCard = safety.SafeToRemoveCard,
        };

    private static double Percent(long value, double total) =>
        total <= 0 ? 0 : Math.Round(Math.Clamp(value * 100.0 / total, 0, 100), 2);

    private static double RemainingSeconds(long completed, double total, double bytesPerSecond)
    {
        if (total <= 0 || !double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
            return 0;
        return Math.Min(Math.Max(0, total - completed) / bytesPerSecond, long.MaxValue);
    }

    private static string ReevaluationMessage(object status, string fallback)
    {
        string? view = status.GetType().GetProperty("view")?.GetValue(status) as string;
        if (string.Equals(view, "complete", StringComparison.Ordinal))
            return "已从持久化证据恢复安全完成状态。";
        if (string.Equals(view, "baseline", StringComparison.Ordinal))
        {
            return status.GetType().GetProperty("headline")?.GetValue(status) as string ?? fallback;
        }
        if (string.Equals(view, "failure", StringComparison.Ordinal))
        {
            object? failure = status.GetType().GetProperty("failure")?.GetValue(status);
            string reason = failure?.GetType().GetProperty("what")?.GetValue(failure) as string ??
                "持久化证据无法通过安全校验。";
            return "重新检查被拒绝：" + reason;
        }
        return fallback;
    }

    private static object WithToast(object status, string message) => new { status, message };

    private void PublishSourceProgress(SourceReadStatus status)
    {
        if (_transferStatus.TryUpdateSource(status, out StandaloneTransferStatusSnapshot progress))
            PublishProgressSnapshot(progress);
    }

    private void PublishTransferProgress(bool isLocal, TargetCopyStatus status)
    {
        bool updated = isLocal
            ? _transferStatus.TryUpdateLocal(status, out StandaloneTransferStatusSnapshot progress)
            : _transferStatus.TryUpdateNas(status, out progress);
        if (updated)
            PublishProgressSnapshot(progress);
    }

    private void PublishProgressSnapshot(StandaloneTransferStatusSnapshot progress)
    {
        object payload;
        lock (_gate)
        {
            if (_activeOperationId is not Guid operationId ||
                !_transferStatus.IsActive(progress.TaskId) ||
                progress.Sequence <= _lastPublishedProgressSequence)
            {
                return;
            }
            payload = ToStatusForMode(progress, PendingSafety(), _activeTargetMode, operationId);
            _lastPublishedProgressSequence = progress.Sequence;
            _status = payload;
            UpdateMediaNoLock(_activeVolumeKey ?? string.Empty, media => media with
            {
                WorkState = progress.Local.Phase is CopyPhase.Verifying or
                    CopyPhase.TemporaryVerifying or CopyPhase.FinalVerifying ||
                    progress.Nas.Phase is CopyPhase.Verifying or
                    CopyPhase.TemporaryVerifying or CopyPhase.FinalVerifying
                        ? "verifying"
                        : "copying",
                SafetyConclusion = "keep_inserted",
                ReasonCode = "CARD_TASK_ACTIVE",
                OverallPercent = progress.OverallPercent,
                Detail = string.IsNullOrWhiteSpace(progress.CurrentFile)
                    ? "正在处理素材卡。"
                    : progress.CurrentFile,
            });
        }
        StatusChanged?.Invoke(this, payload);
        RaiseMediaStatusChanged();
    }

    private void SetOperationTerminalStatus(Guid operationId, object status)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _activeOperationId = null;
            _transferStatus.Stop();
            _status = status;
            ApplyMainStatusToActiveMediaNoLock(status);
        }
        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
    }

    private void SetOperationStatus(Guid operationId, object status)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                return;
            _status = status;
            ApplyMainStatusToActiveMediaNoLock(status);
        }
        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
    }

    private void SetTerminalStatus(object status)
    {
        lock (_gate)
        {
            _transferStatus.Stop();
            _status = status;
            ApplyMainStatusToActiveMediaNoLock(status);
        }
        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
    }

    private void SetStatus(object status)
    {
        lock (_gate)
        {
            _status = status;
            ApplyMainStatusToActiveMediaNoLock(status);
        }
        StatusChanged?.Invoke(this, status);
        RaiseMediaStatusChanged();
    }

    private StandaloneMediaStatusDto CreateMediaStatusSnapshotNoLock()
    {
        string activeMountSessionId = !string.IsNullOrWhiteSpace(_activeVolumeKey) &&
            _mediaStates.TryGetValue(_activeVolumeKey, out StandaloneMediaItemDto? active)
                ? active.MountSessionId
                : string.Empty;
        return new StandaloneMediaStatusDto
        {
            Revision = _mediaRevision,
            ActiveMountSessionId = activeMountSessionId,
            Media = _mediaStates.Values
                .OrderBy(media => string.Equals(
                    media.PresenceState, "removed", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(media => media.DetectedAtUtc, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private void EnsureMediaStateNoLock(VolumeEventArgs volume)
    {
        string volumeKey = VolumeKey(volume);
        if (_mediaStates.TryGetValue(volumeKey, out StandaloneMediaItemDto? existing) &&
            !string.Equals(existing.PresenceState, "removed", StringComparison.Ordinal))
        {
            return;
        }

        string volumeLabel = string.Empty;
        try
        {
            volumeLabel = new DriveInfo(volume.DriveLetter).VolumeLabel;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ = exception;
        }
        _mediaStates[volumeKey] = new StandaloneMediaItemDto
        {
            MountSessionId = Guid.NewGuid().ToString("D"),
            VolumeKey = volumeKey,
            DriveLetter = volume.DriveLetter,
            VolumeLabel = volumeLabel,
            FileSystem = volume.FileSystem,
            CapacityBytes = volume.Capacity,
            DetectedAtUtc = volume.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            PresenceState = "mounted",
            EligibilityState = "checking",
            IdentityState = "unknown",
            WorkState = "scanning",
            SafetyConclusion = "keep_inserted",
            ReasonCode = "MEDIA_DETECTED",
            Detail = $"已检测到 {volume.DriveLetter}，正在识别素材卡。",
        };
        _mediaRevision++;
    }

    private void UpdateMediaNoLock(
        string volumeKey,
        Func<StandaloneMediaItemDto, StandaloneMediaItemDto> update)
    {
        if (!_mediaStates.TryGetValue(volumeKey, out StandaloneMediaItemDto? current))
            return;
        StandaloneMediaItemDto next = update(current);
        if (Equals(current, next))
            return;
        _mediaStates[volumeKey] = next;
        _mediaRevision++;
    }

    private void UpdateMedia(
        string volumeKey,
        Func<StandaloneMediaItemDto, StandaloneMediaItemDto> update)
    {
        bool changed;
        lock (_gate)
        {
            long before = _mediaRevision;
            UpdateMediaNoLock(volumeKey, update);
            changed = _mediaRevision != before;
        }
        if (changed)
            RaiseMediaStatusChanged();
    }

    private void ReindexQueuedMediaNoLock()
    {
        for (int index = 0; index < _pendingVolumes.Count; index++)
        {
            string pendingKey = VolumeKey(_pendingVolumes[index].Volume);
            int queuePosition = index + 1;
            UpdateMediaNoLock(pendingKey, media => media with
            {
                WorkState = "queued",
                QueuePosition = queuePosition,
                SafetyConclusion = "no_backup_conclusion",
                ReasonCode = "WAITING_FOR_ACTIVE_CARD",
                PrimaryAction = string.Empty,
                AvailableActions = [],
                Detail = $"已检测到素材卡，当前排队第 {queuePosition} 位；尚未开始复制。",
            });
        }
    }

    private void ApplyMainStatusToActiveMediaNoLock(object status)
    {
        if (string.IsNullOrWhiteSpace(_activeVolumeKey) ||
            !_mediaStates.TryGetValue(_activeVolumeKey, out StandaloneMediaItemDto? media))
        {
            return;
        }

        string view = status.GetType().GetProperty("view")?.GetValue(status) as string ?? string.Empty;
        string phase = status.GetType().GetProperty("phase")?.GetValue(status) as string ?? string.Empty;
        string headline = status.GetType().GetProperty("headline")?.GetValue(status) as string ?? string.Empty;
        if (string.Equals(view, "waiting", StringComparison.Ordinal) &&
            string.Equals(media.WorkState, "awaiting_action", StringComparison.Ordinal))
        {
            return;
        }

        UpdateMediaNoLock(_activeVolumeKey, current => view switch
        {
            "copying" => current with
            {
                WorkState = phase switch
                {
                    "verifying-temporary" or "verifying-final" => "verifying",
                    "copying" or "publishing" => "copying",
                    _ => "scanning",
                },
                QueuePosition = null,
                SafetyConclusion = "keep_inserted",
                ReasonCode = "CARD_TASK_ACTIVE",
                PrimaryAction = string.Empty,
                AvailableActions = [],
                Detail = string.IsNullOrWhiteSpace(headline) ? "正在处理素材卡。" : headline,
            },
            "complete" => current with
            {
                WorkState = "completed",
                QueuePosition = null,
                SafetyConclusion = "approved_material_verified",
                ReasonCode = "APPROVED_MATERIAL_VERIFIED",
                PrimaryAction = "configure_card_scope",
                AvailableActions = ["configure_card_scope"],
                OverallPercent = 100,
                Detail = "本卡已纳入素材范围的文件已完成复制与完整校验，可以拔卡。",
            },
            "baseline" => current with
            {
                WorkState = "completed",
                QueuePosition = null,
                SafetyConclusion = headline.Contains("空素材卡", StringComparison.Ordinal)
                    ? "no_backup_conclusion"
                    : "approved_material_verified",
                ReasonCode = headline.Contains("空素材卡", StringComparison.Ordinal)
                    ? "EMPTY_CARD_INITIALIZED"
                    : "CARD_BASELINE_RECONCILED",
                PrimaryAction = "configure_card_scope",
                AvailableActions = ["configure_card_scope"],
                OverallPercent = 100,
                Detail = string.IsNullOrWhiteSpace(headline) ? "素材卡检查完成。" : headline,
            },
            "failure" => current with
            {
                WorkState = "blocked",
                QueuePosition = null,
                SafetyConclusion = "no_backup_conclusion",
                ReasonCode = "CARD_REQUIRES_ATTENTION",
                PrimaryAction = "retry",
                AvailableActions = ["retry", "defer_current_card", "configure_card_scope"],
                Detail = "这张卡尚未安全完成；可以恢复本卡，或暂缓后继续处理其他卡。",
            },
            _ => current,
        });
    }

    private void RaiseMediaStatusChanged()
    {
        StandaloneMediaStatusDto snapshot;
        lock (_gate)
            snapshot = CreateMediaStatusSnapshotNoLock();
        MediaStatusChanged?.Invoke(this, snapshot);
    }

    private static IReadOnlyList<string> ObservedTopLevelDirectories(string sourceRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            return [];
        }
    }

    private async Task RebindAuthorizedCardBaselineAsync(
        Guid cardInstanceId,
        string expectedPreviousSourceIdentity,
        string currentSourceIdentity,
        CancellationToken cancellationToken)
    {
        var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
        StandaloneInventoryBaseline baseline = await baselineStore.FindByCardInstanceIdAsync(
            cardInstanceId, cancellationToken) ??
            throw new InvalidDataException("历史素材卡基线不存在，不能确认同一张卡。");
        if (string.Equals(baseline.SourceIdentity, currentSourceIdentity, StringComparison.Ordinal))
            return;
        if (!string.Equals(baseline.SourceIdentity, expectedPreviousSourceIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("历史素材卡基线已被其他操作改变，请重新检查后再确认。");
        await baselineStore.RebindSourceIdentityAsync(
            cardInstanceId,
            expectedPreviousSourceIdentity,
            currentSourceIdentity,
            cancellationToken);
    }

    private static string UserFacingFailureMessage(Exception exception) => exception switch
    {
        ConflictException conflict =>
            $"保存位置已存在同名文件，系统没有覆盖它：{conflict.ExistingPath}。请在设置中改用“按导入日期”或“按拍摄日期”命名，或选择不会重名的新保存位置，然后重新检查。",
        _ => exception.Message,
    };

    private Task? CompleteStateRepairAndStartTransfer(
        VolumeEventArgs volume,
        bool forceReevaluation)
    {
        string volumeKey = VolumeKey(volume);
        bool mounted;
        lock (_gate)
            mounted = _lastArrivedVolume is not null && string.Equals(
                VolumeKey(_lastArrivedVolume), volumeKey, StringComparison.OrdinalIgnoreCase);
        if (!mounted || !Directory.Exists(Path.GetFullPath(volume.DriveLetter)))
        {
            EndStateRepair(currentVolumeResolved: true);
            return null;
        }

        lock (_gate)
        {
            _stateRepairInProgress = false;
            if (_stopping || _lastArrivedVolume is null || !string.Equals(
                    VolumeKey(_lastArrivedVolume), volumeKey, StringComparison.OrdinalIgnoreCase))
                return null;
            return StartTransferNoLock(volume, forceReevaluation);
        }
    }

    private void EndStateRepair(bool currentVolumeResolved)
    {
        bool startPending;
        lock (_gate)
        {
            _stateRepairInProgress = false;
            if (currentVolumeResolved)
                _currentVolumeBlocked = false;
            startPending = !_stopping &&
                !_configurationChangeInProgress &&
                _activeTask is not { IsCompleted: false } &&
                !_currentVolumeBlocked &&
                _pendingVolumes.Count > 0;
        }
        if (startPending)
            StartNextPendingVolume();
    }

    private void EndConfigurationChange()
    {
        bool startPending;
        lock (_gate)
        {
            _configurationChangeInProgress = false;
            startPending = !_stopping &&
                !_stateRepairInProgress &&
                _activeTask is not { IsCompleted: false } &&
                _pendingVolumes.Count > 0;
        }
        if (startPending)
            StartNextPendingVolume();
    }

    private sealed class ConfigurationChangeLease(StandaloneRuntimeService owner) : IDisposable
    {
        private StandaloneRuntimeService? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndConfigurationChange();
    }

    private sealed record RecoveredBaselineState(
        StandaloneInventoryBaseline Baseline,
        StandaloneInventoryDelta Delta);
    private sealed record PendingVolumeWork(VolumeEventArgs Volume, bool ForceReevaluation);

}
