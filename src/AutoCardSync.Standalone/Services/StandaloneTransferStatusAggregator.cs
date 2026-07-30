using AutoCardSync.Application.Copying;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Services;

public sealed record StandaloneTransferStatusSnapshot(
    long Sequence,
    Guid TaskId,
    TargetCopyStatus Local,
    TargetCopyStatus Nas,
    double OverallPercent,
    string CurrentFile,
    double CurrentBytesPerSecond,
    double AverageBytesPerSecond,
    double EtaSeconds,
    int CompletedFiles,
    int TotalFiles,
    long SourceBytesRead = 0,
    double SourceBytesPerSecond = 0,
    double SourceAverageBytesPerSecond = 0,
    double LocalBytesPerSecond = 0,
    double NasBytesPerSecond = 0,
    double LocalVerificationBytesPerSecond = 0,
    double NasVerificationBytesPerSecond = 0);

public sealed class StandaloneTransferStatusAggregator
{
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(4);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly RollingRateTracker _sourceRate;
    private readonly RollingRateTracker _localRate;
    private readonly RollingRateTracker _nasRate;
    private readonly RollingRateTracker _localVerificationRate;
    private readonly RollingRateTracker _nasVerificationRate;
    private bool _active;
    private Guid _taskId;
    private long _sequence;
    private long _startedTimestamp;
    private long _sourceBytesRead;
    private double _sourceAverageBytesPerSecond;
    private int _sourceCompletedFiles;
    private string? _sourceCurrentFile;
    private CopyPhase _sourcePhase = CopyPhase.Copying;
    private long _totalBytes;
    private int _totalFiles;
    private StandaloneTargetMode _targetMode = StandaloneTargetMode.LocalAndNas;
    private TargetCopyStatus _local = new();
    private TargetCopyStatus _nas = new();

    public StandaloneTransferStatusAggregator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sourceRate = new RollingRateTracker(_timeProvider, SpeedWindow);
        _localRate = new RollingRateTracker(_timeProvider, SpeedWindow);
        _nasRate = new RollingRateTracker(_timeProvider, SpeedWindow);
        _localVerificationRate = new RollingRateTracker(_timeProvider, SpeedWindow);
        _nasVerificationRate = new RollingRateTracker(_timeProvider, SpeedWindow);
    }

    public void StartTask(
        Guid taskId,
        Guid localTargetId,
        Guid nasTargetId,
        long totalBytes,
        int totalFiles,
        StandaloneTargetMode targetMode = StandaloneTargetMode.LocalAndNas)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        if (localTargetId == Guid.Empty)
            throw new ArgumentException("Local target identity is required.", nameof(localTargetId));
        if (nasTargetId == Guid.Empty)
            throw new ArgumentException("NAS target identity is required.", nameof(nasTargetId));
        if (totalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (totalFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(totalFiles));

        lock (_gate)
        {
            long now = _timeProvider.GetTimestamp();
            _active = true;
            _taskId = taskId;
            _sequence = 0;
            _startedTimestamp = now;
            _sourceBytesRead = 0;
            _sourceAverageBytesPerSecond = 0;
            _sourceCompletedFiles = 0;
            _sourceCurrentFile = null;
            _sourcePhase = CopyPhase.Copying;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _targetMode = targetMode;
            _local = InitialStatus(taskId, localTargetId, totalBytes, totalFiles);
            _nas = InitialStatus(taskId, nasTargetId, totalBytes, totalFiles);
            _sourceRate.Reset(now);
            _localRate.Reset(now);
            _nasRate.Reset(now);
            _localVerificationRate.Reset(now);
            _nasVerificationRate.Reset(now);
        }
    }

    public bool TryUpdateSource(
        SourceReadStatus status,
        out StandaloneTransferStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            if (!_active || status.TaskId != _taskId)
            {
                snapshot = default!;
                return false;
            }

            long now = _timeProvider.GetTimestamp();
            _sourceBytesRead = Math.Clamp(Math.Max(_sourceBytesRead, status.BytesRead), 0, _totalBytes);
            _sourceCompletedFiles = Math.Clamp(
                Math.Max(_sourceCompletedFiles, status.CompletedFiles), 0, _totalFiles);
            _sourceCurrentFile = status.CurrentFile;
            _sourcePhase = status.IsComplete ? CopyPhase.Completed : status.Phase;
            _sourceRate.Update(now, _sourceBytesRead, _sourcePhase == CopyPhase.Copying);
            double sourceElapsedSeconds = ElapsedSeconds(_startedTimestamp, now);
            if (_sourcePhase == CopyPhase.Copying && sourceElapsedSeconds > 0 && _sourceBytesRead > 0)
                _sourceAverageBytesPerSecond = _sourceBytesRead / sourceElapsedSeconds;
            snapshot = CreateSnapshot(now, status.CurrentFile);
            return true;
        }
    }

    public bool TryUpdateLocal(
        TargetCopyStatus status,
        out StandaloneTransferStatusSnapshot snapshot) =>
        TryUpdateTarget(isLocal: true, status, out snapshot);

    public bool TryUpdateNas(
        TargetCopyStatus status,
        out StandaloneTransferStatusSnapshot snapshot) =>
        TryUpdateTarget(isLocal: false, status, out snapshot);

    public bool IsActive(Guid taskId)
    {
        lock (_gate)
            return _active && taskId == _taskId;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            long now = _timeProvider.GetTimestamp();
            _sourceRate.Update(now, _sourceBytesRead, active: false);
            _localRate.Update(now, _local.BytesTransferred, active: false);
            _nasRate.Update(now, _nas.BytesTransferred, active: false);
            _localVerificationRate.Update(now, VerificationBytes(_local), active: false);
            _nasVerificationRate.Update(now, VerificationBytes(_nas), active: false);
        }
    }

    private bool TryUpdateTarget(
        bool isLocal,
        TargetCopyStatus status,
        out StandaloneTransferStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            if (!_active || status.TaskId != _taskId)
            {
                snapshot = default!;
                return false;
            }
            if ((isLocal && !_targetMode.RequiresLocal()) ||
                (!isLocal && !_targetMode.RequiresNas()))
            {
                snapshot = default!;
                return false;
            }

            long now = _timeProvider.GetTimestamp();
            if (isLocal)
            {
                _local = Merge(_local, status);
                _localRate.Update(now, _local.BytesTransferred, _local.Phase == CopyPhase.Copying);
                _localVerificationRate.Update(
                    now, VerificationBytes(_local), IsVerificationPhase(_local.Phase));
            }
            else
            {
                _nas = Merge(_nas, status);
                _nasRate.Update(now, _nas.BytesTransferred, _nas.Phase == CopyPhase.Copying);
                _nasVerificationRate.Update(
                    now, VerificationBytes(_nas), IsVerificationPhase(_nas.Phase));
            }

            snapshot = CreateSnapshot(now, status.CurrentFile);
            return true;
        }
    }

    private StandaloneTransferStatusSnapshot CreateSnapshot(long now, string? latestCurrentFile)
    {
        _sourceRate.Update(now, _sourceBytesRead, _sourcePhase == CopyPhase.Copying);
        _localRate.Update(now, _local.BytesTransferred, _local.Phase == CopyPhase.Copying);
        _nasRate.Update(now, _nas.BytesTransferred, _nas.Phase == CopyPhase.Copying);
        _localVerificationRate.Update(
            now, VerificationBytes(_local), IsVerificationPhase(_local.Phase));
        _nasVerificationRate.Update(
            now, VerificationBytes(_nas), IsVerificationPhase(_nas.Phase));
        int selectedTargetCount = (_targetMode.RequiresLocal() ? 1 : 0) +
            (_targetMode.RequiresNas() ? 1 : 0);
        double totalWorkBytes = _totalBytes * (1 + 3 * selectedTargetCount);
        double completedWorkBytes = SelectedCompletedWorkBytes();
        double overallPercent = Percent(completedWorkBytes, totalWorkBytes);
        double remainingSourceBytes = Math.Max(0, _totalBytes - _sourceBytesRead);
        double verificationRate = SelectedVerificationRate();
        double remainingVerificationBytes = SelectedRemainingVerificationBytes();
        double etaSeconds = verificationRate > 0
            ? Math.Min(remainingVerificationBytes / verificationRate, long.MaxValue)
            : _sourcePhase == CopyPhase.Copying && _sourceRate.CurrentBytesPerSecond > 0
                ? Math.Min(remainingSourceBytes / _sourceRate.CurrentBytesPerSecond, long.MaxValue)
                : 0;
        string currentFile = FirstCurrentFile(
            _sourceCurrentFile,
            latestCurrentFile,
            _targetMode.RequiresLocal() ? _local.CurrentFile : null,
            _targetMode.RequiresNas() ? _nas.CurrentFile : null);
        double sourceCurrent = FiniteNonNegative(_sourceRate.CurrentBytesPerSecond);
        double sourceAverage = FiniteNonNegative(_sourceAverageBytesPerSecond);

        return new StandaloneTransferStatusSnapshot(
            ++_sequence,
            _taskId,
            _local,
            _nas,
            overallPercent,
            currentFile,
            sourceCurrent,
            sourceAverage,
            FiniteNonNegative(etaSeconds),
            SelectedCompletedFiles(),
            _totalFiles,
            _sourceBytesRead,
            sourceCurrent,
            sourceAverage,
            FiniteNonNegative(_localRate.CurrentBytesPerSecond),
            FiniteNonNegative(_nasRate.CurrentBytesPerSecond),
            FiniteNonNegative(_localVerificationRate.CurrentBytesPerSecond),
            FiniteNonNegative(_nasVerificationRate.CurrentBytesPerSecond));
    }

    private TargetCopyStatus Merge(TargetCopyStatus previous, TargetCopyStatus current)
    {
        if (previous.IsComplete)
            return previous;

        return current with
        {
            TaskId = _taskId,
            TargetId = previous.TargetId,
            BytesCopied = Math.Clamp(Math.Max(previous.BytesCopied, current.BytesCopied), 0, _totalBytes),
            BytesTransferred = Math.Clamp(
                Math.Max(previous.BytesTransferred, current.BytesTransferred), 0, _totalBytes),
            TemporaryBytesVerified = Math.Clamp(
                Math.Max(previous.TemporaryBytesVerified, current.TemporaryBytesVerified), 0, _totalBytes),
            FinalBytesVerified = Math.Clamp(
                Math.Max(previous.FinalBytesVerified, current.FinalBytesVerified), 0, _totalBytes),
            TotalBytes = _totalBytes,
            TotalFiles = _totalFiles,
            FilesVerified = Math.Clamp(
                Math.Max(previous.FilesVerified, current.FilesVerified), 0, _totalFiles),
            FilesFailed = Math.Max(previous.FilesFailed, current.FilesFailed),
            IsComplete = current.IsComplete || current.Phase == CopyPhase.Completed,
        };
    }

    private double SelectedCompletedWorkBytes()
    {
        double completedWorkBytes = _sourceBytesRead;
        if (_targetMode.RequiresLocal())
            completedWorkBytes += TargetCompletedWorkBytes(_local);
        if (_targetMode.RequiresNas())
            completedWorkBytes += TargetCompletedWorkBytes(_nas);
        return completedWorkBytes;
    }

    private static double TargetCompletedWorkBytes(TargetCopyStatus status) =>
        status.BytesCopied + VerificationBytes(status);

    private double SelectedVerificationRate()
    {
        double rate = 0;
        if (_targetMode.RequiresLocal() && IsVerificationPhase(_local.Phase))
            rate += FiniteNonNegative(_localVerificationRate.CurrentBytesPerSecond);
        if (_targetMode.RequiresNas() && IsVerificationPhase(_nas.Phase))
            rate += FiniteNonNegative(_nasVerificationRate.CurrentBytesPerSecond);
        return rate;
    }

    private double SelectedRemainingVerificationBytes()
    {
        double remaining = 0;
        if (_targetMode.RequiresLocal())
            remaining += Math.Max(0, 2d * _totalBytes - VerificationBytes(_local));
        if (_targetMode.RequiresNas())
            remaining += Math.Max(0, 2d * _totalBytes - VerificationBytes(_nas));
        return remaining;
    }

    private static long VerificationBytes(TargetCopyStatus status) =>
        checked(status.TemporaryBytesVerified + status.FinalBytesVerified);

    private static bool IsVerificationPhase(CopyPhase phase) =>
        phase is CopyPhase.Verifying or CopyPhase.TemporaryVerifying or CopyPhase.FinalVerifying;

    private int SelectedCompletedFiles()
    {
        var values = new List<int>(2);
        if (_targetMode.RequiresLocal())
            values.Add(_local.FilesVerified);
        if (_targetMode.RequiresNas())
            values.Add(_nas.FilesVerified);
        return values.Count == 0 ? _sourceCompletedFiles : values.Min();
    }

    private double ElapsedSeconds(long start, long end)
    {
        if (end <= start)
            return 0;
        return Math.Max(0, _timeProvider.GetElapsedTime(start, end).TotalSeconds);
    }

    private static TargetCopyStatus InitialStatus(
        Guid taskId,
        Guid targetId,
        long totalBytes,
        int totalFiles) => new()
        {
            TaskId = taskId,
            TargetId = targetId,
            TotalBytes = totalBytes,
            TotalFiles = totalFiles,
            Phase = CopyPhase.Copying,
        };

    private static string FirstCurrentFile(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static double Percent(double value, double total) =>
        total <= 0 ? 0 : Math.Round(Math.Clamp(value * 100 / total, 0, 100), 2);

    private static double FiniteNonNegative(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;

    private sealed class RollingRateTracker(TimeProvider timeProvider, TimeSpan window)
    {
        private readonly List<RateSample> _samples = [];
        private long _lastBytes;

        public double CurrentBytesPerSecond { get; private set; }

        public void Reset(long timestamp)
        {
            _samples.Clear();
            _samples.Add(new RateSample(timestamp, 0));
            _lastBytes = 0;
            CurrentBytesPerSecond = 0;
        }

        public void Update(long timestamp, long bytes, bool active)
        {
            long monotonicBytes = Math.Max(_lastBytes, bytes);
            _lastBytes = monotonicBytes;
            if (!active)
            {
                _samples.Clear();
                _samples.Add(new RateSample(timestamp, monotonicBytes));
                CurrentBytesPerSecond = 0;
                return;
            }

            if (_samples.Count > 0 && _samples[^1].Timestamp == timestamp)
                _samples[^1] = new RateSample(timestamp, monotonicBytes);
            else
                _samples.Add(new RateSample(timestamp, monotonicBytes));

            while (_samples.Count > 2 &&
                   timeProvider.GetElapsedTime(_samples[1].Timestamp, timestamp) > window)
            {
                _samples.RemoveAt(0);
            }

            RateSample first = _samples[0];
            double seconds = timeProvider.GetElapsedTime(first.Timestamp, timestamp).TotalSeconds;
            CurrentBytesPerSecond = seconds > 0
                ? Math.Max(0, monotonicBytes - first.Bytes) / seconds
                : 0;
        }
    }

    private readonly record struct RateSample(long Timestamp, long Bytes);
}
