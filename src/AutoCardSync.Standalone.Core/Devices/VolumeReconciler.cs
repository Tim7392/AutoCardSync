using System.Management;
using System.Runtime.Versioning;

namespace AutoCardSync.Agent.Service.Devices;

[SupportedOSPlatform("windows")]
public class VolumeReconciler : IAsyncDisposable
{
    private readonly IVolumeSnapshotProvider _snapshotProvider;
    private readonly TimeSpan _scanInterval;
    private readonly Lock _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private bool _started;
    private bool _disposed;

    public event EventHandler<VolumeReconciliationResult>? ReconciliationComplete;

    public VolumeReconciler()
        : this(new NativeVolumeSnapshotProvider(), TimeSpan.FromSeconds(10))
    {
    }

    public VolumeReconciler(
        IVolumeSnapshotProvider snapshotProvider, TimeSpan scanInterval)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        if (scanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(scanInterval));
        _snapshotProvider = snapshotProvider;
        _scanInterval = scanInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _backgroundTask = RunLoopAsync(_cts.Token);
            _started = true;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (_stateLock)
        {
            if (!_started)
                return;

            _cts?.Cancel();
            task = _backgroundTask;
            _started = false;
        }

        if (task is not null)
        {
            try
            { await task; }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts?.Dispose();
        _cts = null;
        _backgroundTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync();
    }

    public async Task<IReadOnlyList<VolumeEventArgs>> GetCurrentVolumesAsync()
    {
        return await Task.Run(() => EnumerateMountedVolumes());
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var volumes = EnumerateMountedVolumes();
                var result = new VolumeReconciliationResult(
                    CurrentVolumes: volumes,
                    Timestamp: DateTimeOffset.UtcNow);

                ReconciliationComplete?.Invoke(this, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A notification or the next fixed interval will retry a transient scan.
            }

            await Task.Delay(_scanInterval, cancellationToken);
        }
    }

    protected virtual IReadOnlyList<VolumeEventArgs> EnumerateMountedVolumes()
        => _snapshotProvider.EnumerateMountedVolumes();
}

public record VolumeReconciliationResult(
    IReadOnlyList<VolumeEventArgs> CurrentVolumes,
    DateTimeOffset Timestamp);
