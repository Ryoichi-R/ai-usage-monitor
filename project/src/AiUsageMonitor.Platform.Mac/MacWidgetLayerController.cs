using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// macOSのウィジェット層制御。NSWindow.level（floating / desktop / normal）、ignoresMouseEvents、
/// collectionBehavior（全Spaceに表示・Mission Controlで固定・フルスクリーン補助）を設定し、読み戻して一致を確認する。
/// macOSはOS側が層を保持するため、<see cref="TryRecoverLayer"/>は読み戻しの不一致を再適用するだけでよい。
/// </summary>
public sealed class MacWidgetLayerController : IWidgetLayerController
{
    internal const ulong WidgetCollectionBehavior =
        NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.Stationary |
        NSWindowCollectionBehavior.IgnoresCycle | NSWindowCollectionBehavior.FullScreenAuxiliary;

    private readonly INativeWindowLayerApi _api;
    private nint _window;
    private WidgetLayerMode _mode = WidgetLayerMode.Normal;
    private bool _clickThrough;

    [SupportedOSPlatform("macos")]
    public MacWidgetLayerController()
        : this(ObjCWindowLayerApi.Instance)
    {
    }

    internal MacWidgetLayerController(INativeWindowLayerApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public WidgetLayerHealth Health { get; private set; } = new(false, null, 0);

    public event Action<WidgetLayerHealth>? HealthChanged;

    /// <summary>NSWindowまたはNSViewのポインタを受け付ける（Avaloniaのplatform handleはどちらの場合もある）。</summary>
    public void Attach(nint nativeWindowHandle)
    {
        nint window = _api.ResolveWindow(nativeWindowHandle);
        if (window == 0)
        {
            ReportHealth(new(true, nameof(Attach), 0));
            return;
        }
        _window = window;
        _api.SetCollectionBehavior(_window, _api.GetCollectionBehavior(_window) | WidgetCollectionBehavior);
        Apply(nameof(Attach));
    }

    public void SetLayerMode(WidgetLayerMode mode)
    {
        _mode = mode;
        Apply(nameof(SetLayerMode));
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        Apply(nameof(SetClickThrough));
    }

    public bool TryRecoverLayer()
    {
        if (_window == 0) return false;
        if (!Matches()) Apply(nameof(TryRecoverLayer));
        return !Health.IsDegraded;
    }

    public void Dispose()
    {
        _window = 0;
    }

    private void Apply(string operation)
    {
        if (_window == 0) return;
        _api.SetLevel(_window, _api.LevelFor(_mode));
        _api.SetIgnoresMouseEvents(_window, _clickThrough);
        ReportHealth(Matches() ? new(false, null, 0) : new(true, operation, 0));
    }

    private bool Matches() =>
        _api.GetLevel(_window) == _api.LevelFor(_mode) &&
        _api.GetIgnoresMouseEvents(_window) == _clickThrough;

    private void ReportHealth(WidgetLayerHealth health)
    {
        bool changed = health.IsDegraded != Health.IsDegraded;
        Health = health;
        if (changed) HealthChanged?.Invoke(health);
    }
}
