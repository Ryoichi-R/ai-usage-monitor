namespace AiUsageMonitor.Platform.Windows.Window;

internal enum LayerRepairTrigger
{
    SourceInitialized, Timer, TaskbarCreated, DisplayChanged, SessionUnlock, VisibilityChanged, ExplicitReapply,
}

internal enum LayerRepairOutcome { Skipped, Healthy, Failed, FallbackPending }
internal enum LayerFailureKind { None, SetWindowPosFailed, StyleReadFailed, StyleMismatch, ManagedException }

internal readonly record struct LayerApplyResult(
    LayerStrategy DesiredStrategy,
    bool SetWindowPosSucceeded,
    bool? ObservedTopMost,
    int ErrorCode,
    LayerFailureKind FailureKind)
{
    public bool Succeeded => FailureKind == LayerFailureKind.None;
}

internal readonly record struct LayerRepairResult(
    LayerRepairOutcome Outcome,
    LayerApplyResult ApplyResult,
    int ConsecutiveFailures);

internal sealed class WindowLayerRepairEngine
{
    private const int FallbackThreshold = 3;
    private readonly IWindowLayerApi _api;
    private int _failures;

    internal WindowLayerRepairEngine(IWindowLayerApi api) => _api = api ?? throw new ArgumentNullException(nameof(api));
    internal int ConsecutiveFailures => _failures;

    internal LayerRepairResult Repair(nint hWnd, LayerStrategy strategy, LayerRepairTrigger trigger, bool countFailure, bool forceApply = false)
    {
        if (hWnd == 0) { _failures = 0; return Skipped(strategy); }
        try
        {
            if (!forceApply && strategy != LayerStrategy.BottomMost)
            {
                WindowStyleObservation style = _api.GetExtendedStyle(hWnd);
                if (!style.Succeeded)
                    return RegisterFailure(new(strategy, false, null, style.ErrorCode, LayerFailureKind.StyleReadFailed), countFailure);
                bool expected = strategy == LayerStrategy.TopMost;
                if (style.IsTopMost == expected)
                    return new(LayerRepairOutcome.Healthy, new(strategy, false, style.IsTopMost, 0, LayerFailureKind.None), 0);
            }

            nint insertAfter = strategy switch
            {
                LayerStrategy.BottomMost => WindowInterop.HWND_BOTTOM,
                LayerStrategy.TopMost => WindowInterop.HWND_TOPMOST,
                _ => WindowInterop.HWND_NOTOPMOST,
            };
            WindowPositionCallResult position = _api.SetWindowPosition(
                hWnd,
                insertAfter,
                WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
            if (!position.Succeeded)
                return RegisterFailure(new(strategy, false, null, position.ErrorCode, LayerFailureKind.SetWindowPosFailed), countFailure);

            WindowStyleObservation after = _api.GetExtendedStyle(hWnd);
            if (!after.Succeeded)
                return RegisterFailure(new(strategy, true, null, after.ErrorCode, LayerFailureKind.StyleReadFailed), countFailure);
            bool expectedTopMost = strategy == LayerStrategy.TopMost;
            if (after.IsTopMost != expectedTopMost && strategy != LayerStrategy.BottomMost)
                return RegisterFailure(new(strategy, true, after.IsTopMost, 0, LayerFailureKind.StyleMismatch), countFailure);
            _failures = 0;
            return new(LayerRepairOutcome.Healthy, new(strategy, true, after.IsTopMost, 0, LayerFailureKind.None), 0);
        }
        catch
        {
            return RegisterFailure(new(strategy, false, null, 0, LayerFailureKind.ManagedException), countFailure);
        }
    }

    internal void ResetEpisode() => _failures = 0;

    private LayerRepairResult RegisterFailure(LayerApplyResult result, bool countFailure)
    {
        if (countFailure && result.DesiredStrategy == LayerStrategy.BottomMost)
            _failures = Math.Min(FallbackThreshold, _failures + 1);
        LayerRepairOutcome outcome = result.DesiredStrategy == LayerStrategy.BottomMost && _failures >= FallbackThreshold
            ? LayerRepairOutcome.FallbackPending
            : LayerRepairOutcome.Failed;
        return new(outcome, result, _failures);
    }

    private static LayerRepairResult Skipped(LayerStrategy strategy) =>
        new(LayerRepairOutcome.Skipped, new(strategy, false, null, 0, LayerFailureKind.None), 0);
}
