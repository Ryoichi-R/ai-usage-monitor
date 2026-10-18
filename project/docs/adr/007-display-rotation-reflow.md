# ADR 007: Display rotation reflow

- Status: Accepted for implementation; Surface実機受入は別途保留
- Date: 2026-08-18 (Asia/Tokyo)

## Decision

MainWindowのHWND message hookで `WM_DISPLAYCHANGE` と `SPI_SETWORKAREA` を受ける。加えて、HWND hookへ通知されない端末・回転経路を補うため `SystemEvents.DisplaySettingsChanged` を購読し、UI Dispatcherへ戻して同じ `DisplayReflowScheduler` のwaveへ集約する。重複通知はschedulerのdebounceで統合する。通常waveは750msのtrailing edgeと開始から2秒のdeadlineを持つ。回転確定直後は最初のworking area取得が変更前の矩形を返してもinterop上は成功になるため、通常waveの成否にかかわらず750ms後に1回だけsettle retryを新しいwave IDで開始する。retry wave自身は別の2秒deadlineを持ち、連鎖retryは行わない。

Win32層は `EnumDisplayMonitors`／`GetMonitorInfo`／`GetScaleFactorForMonitor` を用いてdevice名、物理pixelのworking area、対象monitorのscale factorを返し、scale factorからeffective DPIを算出する。通常の再配置は保存deviceを優先し、ドラッグ完了時は保存deviceを再利用せず、実HWNDの物理pixel矩形中心を `MonitorFromPoint` へ渡してmonitorを選択する。App層は対象monitor原点を `(0,0)` とするlocal DIPでCore配置計算を行い、相対量だけを対象DPIで物理pixelへ変換した後、monitorの物理pixel原点を加えて `SetWindowPos` する。さらにpreset/customの初期配置後は実HWND矩形を再取得し、情報領域を物理pixelでworking area内へ最終clampする。仮想desktopの絶対pixel座標をDPIで単純除算しない。

EventSourceは既定では無効でファイルを作らず、明示的なtrace sessionでのみ、monotonic timestamp、wave ID、retry属性、timer種別、device名、矩形、HRESULT／失敗stageを記録する。設定値、fraction、ユーザーパス、取得内容はpayloadに含めない。

## Alternatives considered

`System.Windows.Forms.Screen` のcacheは、display/work-area通知と更新順が一致する保証がないため、reflowの正本にしない。`GetDpiForMonitor` はPerMonitorV2 threadでの利用をMicrosoftが非推奨としているため採用しない。`VisualTreeHelper.GetDpi(this)`だけで別monitorの物理矩形をDIPへ変換する方法、および対象monitorの絶対pixel原点を対象DPIで除算してWPF `Left`／`Top`へ代入する方法もmixed-DPIで誤るため採用しない。既存のBackgroundWindow hookはbottom-most repair専用として維持し、MainWindowのgeometry確定後にbackground coordinatorを同期する。

## Verification boundary

fake timer factoryで750ms、2秒deadline、後着wave、成功後settle retry、失敗後retry、drag defer、hidden/cancel、shutdown後callbackなしをsleepなしで検証する。monitor resolverはsaved-device、drag中心点、device消失fallback、DPI failureをfake native seamで検証する。Surface実機の横→縦→横、縦向き再起動、175% DPI、各background modeは自動テストで代替せず、候補EXEを用いた手動受入として別記録する。
