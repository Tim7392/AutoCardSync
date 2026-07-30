using System.Runtime.Versioning;
using System.Threading.Channels;

namespace AutoCardSync.Agent.Service.Devices;

[SupportedOSPlatform("windows")]
public sealed class VolumeNotificationListener : IAsyncDisposable
{
    private readonly IVolumeSnapshotProvider _snapshots;
    private readonly IVolumeNotificationBackend _primaryBackend;
    private readonly Func<IVolumeNotificationBackend> _fallbackFactory;
    private readonly int _queueCapacity;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _settleDelay;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTimeOffset> _debounce =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, VolumeEventArgs> _previous =
        new(StringComparer.OrdinalIgnoreCase);
    private IVolumeNotificationBackend? _activeBackend;
    private IVolumeNotificationBackend? _fallbackBackend;
    private Channel<VolumeSignal>? _signals;
    private CancellationTokenSource? _cts;
    private Task? _processor;
    private long _droppedSignals;
    private int _reconciliationRequired;
    private bool _started;
    private bool _disposed;

    public VolumeNotificationListener()
        : this(new NativeVolumeSnapshotProvider(),
            new Win32VolumeNotificationBackend(),
            static () => new WmiVolumeNotificationBackend())
    {
    }

    public VolumeNotificationListener(
        IVolumeSnapshotProvider snapshots,
        IVolumeNotificationBackend primaryBackend,
        Func<IVolumeNotificationBackend> fallbackFactory,
        int queueCapacity = 64,
        TimeSpan? debounceWindow = null,
        TimeSpan? settleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(primaryBackend);
        ArgumentNullException.ThrowIfNull(fallbackFactory);
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _snapshots = snapshots;
        _primaryBackend = primaryBackend;
        _fallbackFactory = fallbackFactory;
        _queueCapacity = queueCapacity;
        _debounceWindow = debounceWindow ?? TimeSpan.FromSeconds(2);
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public event EventHandler<VolumeEventArgs>? VolumeArrived;
    public event EventHandler<VolumeEventArgs>? VolumeRemoved;

    public bool UsingFallback { get; private set; }
    public long DroppedSignalCount => Interlocked.Read(ref _droppedSignals);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _started = true;
            try
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _signals = Channel.CreateBounded<VolumeSignal>(new BoundedChannelOptions(_queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
                _previous = SnapshotByIdentity(_snapshots.EnumerateMountedVolumes());
                _processor = ProcessSignalsAsync(_cts.Token);
                UsingFallback = false;
            }
            catch
            {
                _cts?.Dispose();
                _cts = null;
                _signals = null;
                _processor = null;
                _started = false;
                throw;
            }
        }

        try
        {
            await StartBackendAsync(_primaryBackend, cancellationToken);
            _activeBackend = _primaryBackend;
        }
        catch (OperationCanceledException)
        {
            await CleanupFailedStartAsync(null);
            throw;
        }
        catch
        {
            await _primaryBackend.StopAsync();
            IVolumeNotificationBackend fallback = _fallbackFactory();
            _fallbackBackend = fallback;
            try
            {
                await StartBackendAsync(fallback, cancellationToken);
                _activeBackend = fallback;
                UsingFallback = true;
            }
            catch
            {
                await CleanupFailedStartAsync(fallback);
                throw;
            }
        }
    }

    private async Task CleanupFailedStartAsync(IVolumeNotificationBackend? failedFallback)
    {
        if (failedFallback is not null)
        {
            failedFallback.Signal -= OnSignal;
            try
            { await failedFallback.StopAsync(); }
            catch
            { }
            await failedFallback.DisposeAsync();
        }

        Task? processor;
        lock (_stateLock)
        {
            _signals?.Writer.TryComplete();
            _cts?.Cancel();
            processor = _processor;
            _activeBackend = null;
            _fallbackBackend = null;
            _started = false;
            UsingFallback = false;
        }
        if (processor is not null)
        {
            try
            { await processor; }
            catch (OperationCanceledException)
            { }
        }
        lock (_stateLock)
        {
            _cts?.Dispose();
            _cts = null;
            _signals = null;
            _processor = null;
            Interlocked.Exchange(ref _reconciliationRequired, 0);
        }
    }

    public async Task StopAsync()
    {
        Task? processor;
        IVolumeNotificationBackend? backend;
        lock (_stateLock)
        {
            if (!_started)
                return;
            _started = false;
            backend = _activeBackend;
            _activeBackend = null;
            if (backend is not null)
                backend.Signal -= OnSignal;
            _signals?.Writer.TryComplete();
            _cts?.Cancel();
            processor = _processor;
        }

        if (backend is not null)
            await backend.StopAsync();
        if (processor is not null)
        {
            try
            { await processor; }
            catch (OperationCanceledException)
            { }
        }
        lock (_stateLock)
        {
            _cts?.Dispose();
            _cts = null;
            _signals = null;
            _processor = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync();
        _disposed = true;
        await _primaryBackend.DisposeAsync();
        if (_fallbackBackend is not null)
            await _fallbackBackend.DisposeAsync();
    }

    private async Task StartBackendAsync(
        IVolumeNotificationBackend backend, CancellationToken cancellationToken)
    {
        backend.Signal += OnSignal;
        try
        { await backend.StartAsync(cancellationToken); }
        catch
        {
            backend.Signal -= OnSignal;
            throw;
        }
    }

    private void OnSignal(object? sender, VolumeSignal signal)
    {
        Channel<VolumeSignal>? channel = _signals;
        if (channel is not null && channel.Writer.TryWrite(signal))
            return;
        Interlocked.Increment(ref _droppedSignals);
        Interlocked.Exchange(ref _reconciliationRequired, 1);
    }

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        Channel<VolumeSignal> channel = _signals ??
            throw new InvalidOperationException(nameof(_signals));
        await foreach (VolumeSignal signal in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (_settleDelay > TimeSpan.Zero)
                await Task.Delay(_settleDelay, cancellationToken);
            await ReconcileSnapshotAsync(signal.Timestamp, signal.Kind, cancellationToken);
            if (Interlocked.Exchange(ref _reconciliationRequired, 0) == 1)
                await ReconcileSnapshotAsync(DateTimeOffset.UtcNow, signalKind: null, cancellationToken);
        }
    }

    private async Task ReconcileSnapshotAsync(
        DateTimeOffset timestamp,
        VolumeSignalKind? signalKind,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<VolumeEventArgs> values;
        try
        {
            values = await Task.Run(_snapshots.EnumerateMountedVolumes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Interlocked.Exchange(ref _reconciliationRequired, 1);
            return;
        }

        Dictionary<string, VolumeEventArgs> current = SnapshotByIdentity(values);
        HashSet<string> replaced = current
            .Where(pair => _previous.TryGetValue(pair.Key, out VolumeEventArgs? previous) &&
                (!SameMountedSnapshot(previous, pair.Value) ||
                 (signalKind == VolumeSignalKind.Arrival && !pair.Value.MountContinuityProven)))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        VolumeEventArgs[] arrived = current
            .Where(pair => !_previous.ContainsKey(pair.Key) || replaced.Contains(pair.Key))
            .Select(pair => pair.Value with { Timestamp = timestamp })
            .ToArray();
        VolumeEventArgs[] removed = _previous
            .Where(pair => !current.ContainsKey(pair.Key) || replaced.Contains(pair.Key))
            .Select(pair => pair.Value with { Timestamp = timestamp })
            .ToArray();
        _previous = current;

        foreach (VolumeEventArgs value in removed)
            if (ShouldRaise(VolumeSignalKind.Removal, value, timestamp))
                VolumeRemoved?.Invoke(this, value);
        foreach (VolumeEventArgs value in arrived)
            if (ShouldRaise(VolumeSignalKind.Arrival, value, timestamp))
                VolumeArrived?.Invoke(this, value);
    }

    private bool ShouldRaise(
        VolumeSignalKind kind, VolumeEventArgs volume, DateTimeOffset timestamp)
    {
        string key = string.Concat(kind, "|", SnapshotIdentity(volume));
        lock (_debounce)
        {
            if (_debounce.TryGetValue(key, out DateTimeOffset previous) &&
                timestamp - previous < _debounceWindow)
                return false;
            _debounce[key] = timestamp;
            foreach (string expired in _debounce
                .Where(pair => timestamp - pair.Value >= _debounceWindow)
                .Select(pair => pair.Key).ToArray())
                if (!string.Equals(expired, key, StringComparison.OrdinalIgnoreCase))
                    _debounce.Remove(expired);
            return true;
        }
    }

    private static bool SameMountedSnapshot(VolumeEventArgs left, VolumeEventArgs right) =>
        string.Equals(left.DriveLetter, right.DriveLetter, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.VolumeGuid, right.VolumeGuid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FileSystem, right.FileSystem, StringComparison.OrdinalIgnoreCase) &&
        left.Capacity == right.Capacity &&
        string.Equals(left.MountIdentity, right.MountIdentity, StringComparison.OrdinalIgnoreCase) &&
        left.MountContinuityProven == right.MountContinuityProven;

    private static string SnapshotIdentity(VolumeEventArgs value) => string.Join(
        "|",
        BaseIdentity(value),
        value.DriveLetter.Trim(),
        value.FileSystem.Trim(),
        value.Capacity,
        value.MountIdentity?.Trim() ?? string.Empty,
        value.MountContinuityProven ? "strong" : "weak");

    private static Dictionary<string, VolumeEventArgs> SnapshotByIdentity(
        IReadOnlyList<VolumeEventArgs> values)
    {
        var result = new Dictionary<string, VolumeEventArgs>(StringComparer.OrdinalIgnoreCase);
        foreach (VolumeEventArgs value in values)
        {
            string identity = SnapshotIdentity(value);
            if (string.IsNullOrWhiteSpace(identity) || !result.TryAdd(identity, value))
                throw new InvalidDataException("Mounted volume snapshots must be non-empty and unique.");
        }
        return result;
    }

    private static string BaseIdentity(VolumeEventArgs value) =>
        string.IsNullOrWhiteSpace(value.VolumeGuid) ? value.DriveLetter : value.VolumeGuid;
}
