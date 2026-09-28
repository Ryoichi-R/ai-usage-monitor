# Windows クリック透過修正（2026-09-28）

## 変更

Avalonia版でも共用していたClickThroughHelperはWS_EX_TRANSPARENTだけを設定していた。
有効化時にWS_EX_LAYEREDがなければ追加し、SetLayeredWindowAttributesのalpha=255で初期化する。
既にレイヤードの場合は初期化せず、WPFのper-pixel alphaや既存の透明度・カラーキーを維持する。
解除時はWS_EX_TRANSPARENTだけを外す。Win32失敗は例外として既存の診断経路へ渡し、
初期化失敗時は元の拡張スタイルへ復元を試みる。
既存の最前面表示修正を含む候補であり、稼働プロセスや配布物は置換していない。

根拠:
- https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes
- https://github.com/AvaloniaUI/Avalonia/blob/12.1.2/src/Windows/Avalonia.Win32/WindowImpl.cs

## 検証

- Platform.Windows.Tests: 67 passed / 0 failed / 0 skipped。
- 追加のnative HWNDテスト: 非レイヤード、NOREDIRECTIONBITMAP付きの各ウィンドウでON/OFF/再ON、他属性保持、初期alphaを確認。既存レイヤードのalpha・カラーキー保持を確認。
- 無効ハンドルのエラー検出を確認。
- lint.ps1 -Mode Fast: PASS。
- git diff --check: PASS。
- Windows Avalonia win-arm64 publish: 成功。
- 当該実行のOS/process architecture: Arm64 / Arm64。候補PE Machine: 0xAA64。
- 初回testはsandboxのNuGet通信制限で復元失敗。許可された再実行で復元・テスト成功。

候補: `project/artifacts/clickthrough-fix-20260928/win-arm64/aiusagemonitor.app.exe`

SHA256: `10F1CE731426BA2B43240F7733DAB92820F723DA04099A9F3E5E9A233D983871`

実行位置はproject/:

```powershell
dotnet test tests/AiUsageMonitor.Platform.Windows.Tests/AiUsageMonitor.Platform.Windows.Tests.csproj -c Release --artifacts-path artifacts/clickthrough-fix-20260928/tests --nologo
pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-arm64 -OutputDir artifacts/clickthrough-fix-20260928/win-arm64 -BuildArtifactsRoot clickthrough-fix-20260928/build-arm64
pwsh -NoProfile -File scripts/lint.ps1 -Mode Fast -ReceiptPath artifacts/clickthrough-fix-20260928/lint-receipt.json
```

## 残る実機確認

native HWNDテストは非表示の試験ウィンドウで属性を検証するもので、クリック配送やAvalonia描画の実機受入を代替しない。
候補への試験切替後、ONで背後の別アプリへのクリック到達、OFFでドラッグ・スクロール復帰、
再ON、透明度変更、分離背景、非表示/再表示で描画と透過が維持されることを確認する。
これらは未実施。commit/pushは実施していない。
