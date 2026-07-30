namespace AutoCardSync.Standalone.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\AutoCardSync.Standalone.Instance";
    private const string ActivationEventName = @"Local\AutoCardSync.Standalone.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _disposed;

    private SingleInstanceCoordinator(
        Mutex mutex,
        bool isPrimary,
        EventWaitHandle? activationEvent)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _activationEvent = activationEvent;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            var activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivationEventName,
                out _);
            return new SingleInstanceCoordinator(mutex, true, activationEvent);
        }

        return new SingleInstanceCoordinator(mutex, false, null);
    }

    public void SignalPrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
            throw new InvalidOperationException("The primary instance cannot signal itself.");

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) when (attempt < 19)
            {
                Thread.Sleep(100);
            }
        }
    }

    public void ListenForActivation(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || _activationEvent is null)
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        if (_activationRegistration is not null)
            throw new InvalidOperationException("Activation listening has already started.");

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                    ((Action)state!).Invoke();
            },
            callback,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        if (IsPrimary)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
