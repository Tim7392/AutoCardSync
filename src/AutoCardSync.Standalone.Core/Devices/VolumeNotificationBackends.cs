using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoCardSync.Agent.Service.Devices;

public enum VolumeSignalKind { Arrival, Removal }

public sealed record VolumeSignal(VolumeSignalKind Kind, DateTimeOffset Timestamp);

public interface IVolumeNotificationBackend : IAsyncDisposable
{
    event EventHandler<VolumeSignal>? Signal;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

[SupportedOSPlatform("windows")]
public sealed class Win32VolumeNotificationBackend : IVolumeNotificationBackend
{
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypeDeviceInterface = 5;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly Guid VolumeInterfaceClass =
        new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    private readonly object _sync = new();
    private readonly WndProc _windowProcedure;
    private Thread? _thread;
    private TaskCompletionSource? _started;
    private TaskCompletionSource? _stopped;
    private IntPtr _window;
    private IntPtr _notification;
    private uint _threadId;

    public Win32VolumeNotificationBackend() => _windowProcedure = WindowProcedure;

    public event EventHandler<VolumeSignal>? Signal;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Task started;
        lock (_sync)
        {
            if (_thread is not null)
            {
                started = _started!.Task;
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException();
                _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _thread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "AutoCardSync.VolumeNotifications",
                };
                _thread.Start();
                started = _started.Task;
            }
        }
        await started.WaitAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        Task? stopped;
        lock (_sync)
        {
            if (_thread is null)
                return;
            stopped = _stopped?.Task;
            if (_window != IntPtr.Zero)
                _ = PostMessage(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
            else if (_threadId != 0)
                _ = PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        }
        if (stopped is not null)
            await stopped;
        lock (_sync)
        {
            _thread = null;
            _started = null;
            _stopped = null;
            _threadId = 0;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void RunMessageLoop()
    {
        string className = string.Concat("AutoCardSyncVolumeWindow_", Guid.NewGuid().ToString("N"));
        IntPtr instance = GetModuleHandle(null);
        ushort atom = 0;
        try
        {
            _threadId = GetCurrentThreadId();
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Instance = instance,
                WindowProcedure = _windowProcedure,
                ClassName = className,
            };
            atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
                HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (_window == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            RegisterVolumeNotifications();
            _started!.TrySetResult();

            while (true)
            {
                int result = GetMessage(out Message message, IntPtr.Zero, 0, 0);
                if (result == 0)
                    break;
                if (result < 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _started?.TrySetException(exception);
        }
        finally
        {
            if (_notification != IntPtr.Zero)
            {
                _ = UnregisterDeviceNotification(_notification);
                _notification = IntPtr.Zero;
            }
            if (_window != IntPtr.Zero)
            {
                _ = DestroyWindow(_window);
                _window = IntPtr.Zero;
            }
            if (atom != 0)
                _ = UnregisterClass(className, instance);
            _stopped?.TrySetResult();
        }
    }

    private void RegisterVolumeNotifications()
    {
        var filter = new DeviceBroadcastInterface
        {
            Size = Marshal.SizeOf<DeviceBroadcastInterface>(),
            DeviceType = DbtDevTypeDeviceInterface,
            ClassGuid = VolumeInterfaceClass,
        };
        IntPtr buffer = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            _notification = RegisterDeviceNotification(_window, buffer, DeviceNotifyWindowHandle);
            if (_notification == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDeviceChange)
        {
            VolumeSignalKind? kind = wParam.ToInt64() switch
            {
                DbtDeviceArrival => VolumeSignalKind.Arrival,
                DbtDeviceRemoveComplete => VolumeSignalKind.Removal,
                _ => null,
            };
            if (kind is not null)
            {
                try
                { Signal?.Invoke(this, new VolumeSignal(kind.Value, DateTimeOffset.UtcNow)); }
                catch
                { /* native callback must never propagate into user32 */ }
            }
            return IntPtr.Zero;
        }
        if (message == WmClose)
        {
            _ = DestroyWindow(window);
            return IntPtr.Zero;
        }
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceBroadcastInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public char Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className,
        string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

[SupportedOSPlatform("windows")]
public sealed class WmiVolumeNotificationBackend : IVolumeNotificationBackend
{
    private ManagementEventWatcher? _arrival;
    private ManagementEventWatcher? _removal;

    public event EventHandler<VolumeSignal>? Signal;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _arrival = CreateWatcher("__InstanceCreationEvent", VolumeSignalKind.Arrival);
        _removal = CreateWatcher("__InstanceDeletionEvent", VolumeSignalKind.Removal);
        _arrival.Start();
        _removal.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stop(ref _arrival);
        Stop(ref _removal);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private ManagementEventWatcher CreateWatcher(string eventType, VolumeSignalKind kind)
    {
        var watcher = new ManagementEventWatcher(new WqlEventQuery(
            eventType, TimeSpan.FromSeconds(2),
            "TargetInstance ISA 'Win32_Volume' AND TargetInstance.DriveLetter != NULL"));
        watcher.EventArrived += (_, _) =>
            Signal?.Invoke(this, new VolumeSignal(kind, DateTimeOffset.UtcNow));
        return watcher;
    }

    private static void Stop(ref ManagementEventWatcher? watcher)
    {
        if (watcher is null)
            return;
        watcher.Stop();
        watcher.Dispose();
        watcher = null;
    }
}
