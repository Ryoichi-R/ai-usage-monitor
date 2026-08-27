namespace AiUsageMonitor.Platform.Windows.Window;

public readonly record struct TopmostHealth(
    bool IsDegraded,
    string? Operation,
    int ErrorCode,
    int ConsecutiveFailures);

public sealed class TopmostWindowController : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeEnd = 0x000A;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);

    private readonly nint _windowHandle;
    private readonly ITopmostInterop _interop;
    private readonly WinEventCallback _callback;
    private readonly List<nint> _hookHandles = [];
    private readonly int _registrationThreadId;
    private bool _enabled;
    private bool _disposed;
    private TopmostHealth _health;

    public TopmostWindowController(nint windowHandle)
        : this(windowHandle, new NativeTopmostInterop())
    {
    }

    internal TopmostWindowController(nint windowHandle, ITopmostInterop interop)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle, nameof(windowHandle));
        _windowHandle = windowHandle;
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _callback = OnWinEvent;
        _registrationThreadId = Environment.CurrentManagedThreadId;
    }

    public event Action? RecoveryRequested;
    public event Action<TopmostHealth>? HealthChanged;
    public TopmostHealth Health => _health;

    // Phase 0 probe showed that both candidate sets preserve owned-dialog order.
    // Omitting NOOWNERZORDER is required to move an ownerless widget itself to the
    // front of a competing topmost window.
    internal static uint RecoveryFlags => SwpNoMove | SwpNoSize | SwpNoActivate;
    internal static uint HookFlags => WinEventOutOfContext | WinEventSkipOwnProcess;
    internal int RegistrationThreadId => _registrationThreadId;
    internal IReadOnlyList<nint> HookHandles => _hookHandles;

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (!enabled)
        {
            _enabled = false;
            UnregisterHooks();
            SetHealthy();
            return;
        }
        if (_enabled) return;

        _enabled = true;
        foreach ((uint min, uint max) in new[]
        {
            (EventSystemForeground, EventSystemForeground),
            (EventSystemMoveSizeEnd, EventSystemMoveSizeEnd),
            (EventSystemDesktopSwitch, EventSystemDesktopSwitch),
        })
        {
            nint hook = 0;
            try
            {
                hook = _interop.SetWinEventHook(min, max, HookFlags, _callback);
            }
            catch
            {
                hook = 0;
            }
            if (hook == 0)
            {
                int registrationErrorCode = _interop.LastErrorCode;
                _enabled = false;
                UnregisterHooks();
                SetFailure("SetWinEventHook", registrationErrorCode);
                return;
            }
            _hookHandles.Add(hook);
        }
        SetHealthy();
    }

    public bool TryRecover()
    {
        if (_disposed || !_enabled) return false;
        try
        {
            bool recovered = _interop.SetWindowPos(_windowHandle, HwndTopmost, RecoveryFlags);
            if (recovered) SetHealthy();
            else SetFailure("SetWindowPos");
            return recovered;
        }
        catch
        {
            SetFailure("SetWindowPos");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _registrationThreadId)
            throw new InvalidOperationException("Topmost hooks must be released on their registration thread.");
        _disposed = true;
        _enabled = false;
        UnregisterHooks();
        RecoveryRequested = null;
        HealthChanged = null;
    }

    private void OnWinEvent(
        nint hookHandle,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        if (_disposed || !_enabled) return;
        try
        {
            RecoveryRequested?.Invoke();
        }
        catch
        {
            // WinEvent callbacks must never leak subscriber failures to user32.
        }
    }

    private void UnregisterHooks()
    {
        foreach (nint hook in _hookHandles.ToArray())
        {
            try { _interop.UnhookWinEvent(hook); }
            catch { }
        }
        _hookHandles.Clear();
    }

    private void SetHealthy()
    {
        if (!_health.IsDegraded && _health.ConsecutiveFailures == 0) return;
        _health = default;
        try { HealthChanged?.Invoke(_health); }
        catch { }
    }

    private void SetFailure(string operation, int? errorCode = null)
    {
        _health = new TopmostHealth(
            IsDegraded: true,
            Operation: operation,
            ErrorCode: errorCode ?? _interop.LastErrorCode,
            ConsecutiveFailures: _health.ConsecutiveFailures + 1);
        try { HealthChanged?.Invoke(_health); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
