# 画面回転時のウィジェット再配置

AI Usage Monitor は `WM_DISPLAYCHANGE`、`SPI_SETWORKAREA`、DPI変更を同一の表示変更waveへ集約し、最後の通知から750ms、またはwave開始から2秒で最新のmonitor working areaを再取得します。自由配置は保存済みfractionを新しい作業領域へ再適用し、プリセット配置は既存のanchorと余白を再適用します。

Win32のmonitor矩形と情報領域のfraction取得は物理pixel、Coreの配置計算は対象monitorのeffective DPIで変換したDIPです。表示モード、UI倍率、設定schema、保存fraction自体は自動reflowで変更しません。monitor/DPI取得に失敗したwaveは直前位置を維持し、1回だけ750ms後の独立retryを行います。

split background使用時は、MainWindowの最終配置完了通知を受けてから背景を同期・表示します。通知が欠落した場合は、再表示から2秒の独立fallbackで一度だけ同期します。MainWindowとBackgroundWindowのHWND hookは終了時に解除します。

## 検証記録

- App build: `dotnet build tests/AiUsageMonitor.App.Tests/AiUsageMonitor.App.Tests.csproj -c Release --no-restore` — 0警告、0エラー。schedulerのtimer factoryを差し替え可能にし、sleepなしのwave／retry／drag／cancel試験を追加。
- App tests: current-source rebuilt `AiUsageMonitor.App.Tests.dll` — 173 passed, 0 failed。target-monitor local DIPから物理pixel位置への変換、負座標monitor上の情報領域clampを追加検証。
- Windows tests: current-source rebuilt `AiUsageMonitor.Windows.Tests.dll` — 27 passed, 0 failed。ドラッグ中心点の`MonitorFromPoint`選択と実 `GetScaleFactorForMonitor` P/Invoke smokeを検証。
- Full solution: `dotnet test AiUsageMonitor.slnx -c Release --no-restore` — 6 test assemblies、合計581 passed、0 failed。
- Format/build: `dotnet format AiUsageMonitor.slnx --no-restore --verify-no-changes` — PASS。`dotnet build AiUsageMonitor.slnx -c Release --no-restore --warnaserror` — 0警告、0エラー。wrapperのPowerShell 7.4実行自体は環境に`pwsh`がないため未実行。
- ADR: [007-display-rotation-reflow.md](../adr/007-display-rotation-reflow.md) にhook、DPI境界、retry wave、EventSource、実機受入境界を記録。
- 修正前candidateの実機受入: 2026-08-19にユーザーから解消済みとの申告あり。candidate EXEのpath、ProductVersion、SHA-256および個別条件は未記録。詳細と適用境界は [画面回転のユーザー受入申告](../verification/display-rotation-user-acceptance-2026-08-19.md) を参照。申告後にDPI／物理pixel配置を変更したため、amend後candidateは再受入が必要。
