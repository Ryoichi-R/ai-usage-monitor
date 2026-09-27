# Windows 11 検証記録（2026-09-27）

- 対象source: `C:\coding\ai-usage-monitor`、検証時のHEAD `f62a65e150f6ee692ce4709e6a924c579326f0ca`。テストと計測script、Appのテスト用依存注入には未コミット差分がある。commit hashだけでは最終状態を再現できない。変更パスは本記録末尾に列挙する。
- Mac側のP0-3/P0-4記録、`7009a67`の週reset `at`形式対応、`c834802`のmacOS fixtureが履歴に存在することを確認した。作業開始時に両repoの追跡済み変更はなかった。リモートとの同期状態はfetchをしていないため証明していない。
- `dotnet test AiUsageMonitor.slnx -c Release --nologo`: 671/671 PASS。`Claude.Cli.Tests` 104/104にはMonthDayAt形式とmacOS fixtureを含む。
- `pwsh -NoProfile -File scripts/test-ai-usage-monitor.ps1`: build/launcher contract PASS、coverageなし全700/700 PASS（2026-09-27、最終変更後）。Claude.Cli.Tests 111/111にはMonthDayAt形式とmacOS fixtureを含む。
- `pwsh -NoProfile -File scripts/test-ai-usage-monitor.ps1 -Coverage -KeepOutput`: 初回91.62%（1,355/1,479）はCoberturaのファイル名形式によりAppなどの行が抜けた誤集計と判明し、判定に使用しない。実source fileへの照合後の初回は80.84%（4,928/6,096）。Appの起動、画面、設定、CLI異常系のテストを追加し、別test hostを逐次instrumentして実source file上の同一行をOR集計した最終結果は **90.19%（5,515/6,115行）**、閾値90%以上、exit 0。最終manifestは`%LOCALAPPDATA%\Temp\AiUsageMonitorTests-coverage-6705aaa85d6a4c198d0b2223feef28da\results\coverage-manifest.json`。
- 最終母集団はCore、Codex、Claude、Claude.Cli、Claude.Windows、Platform、Platform.Windows、Appの8パッケージ。`App.UI`は`.csproj`だけでC#実装ファイルがなく、対象行0として除外した。行の解決には実在するproduction source pathを必須とし、テストhost間で重複する行は1行として数えた。
- `pwsh -NoProfile -File scripts/format.ps1 -Check`、`pwsh -NoProfile -File scripts/lint.ps1`: PASS。`pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-x64`: PASS。publish後、Appの空行だけを整えたため、最終rebuildのEXE SHA-256はpublish時の値と異なる。
- `pwsh -NoProfile -File scripts/rebuild-ai-usage-monitor.ps1 -Runtime win-x64 -OutputRoot C:\`: PASS。稼働先`C:\AiUsageMonitorBuilds\ai-usage-monitor-win-x64`を更新し、旧版を`%LOCALAPPDATA%\AiUsageMonitorBackups\_backup-ai-usage-monitor-win-x64`へ保持した。
- 利用者向け`ここから開始 - AI Usage Monitorを導入・更新.bat`に`C:\`を渡すと、従来の`"%~f1"`が末尾のbackslashでPowerShell引数を壊し、exit 1、`OutputRoot must be an existing directory: C:\coding\ai-usage-monitor\project\C:" -RevealOutput`となった。`C:\.`も`%~f1`で同じrootへ正規化され、同様に失敗した。x64/ARM64の両BATで`"%~f1\."`に修正した。`pwsh -NoProfile -File scripts/test-rebuild-launcher-contract.ps1`はPASS、同じ導入BATを`C:\`で再実行してexit 0、build更新成功。最終稼働EXE SHA-256: `E59EE496958136D9840F71EAB1FE73A5A8AD73ACBA2FF79A8A15A163E552A228`。
- 最終buildを通常起動し、`C:\AiUsageMonitorBuilds\ai-usage-monitor-win-x64\AiUsageMonitor.App.exe`のprocess起動を確認した。利用者は最終buildが問題なく起動し、Claudeの週枠もErrorにならず取得できていると確認した。P1-WのWindows実機受入は完了。前回更新前の週枠Error原因は画面・診断を確認できず、`USAGE_RESET_FORMAT_UNSUPPORTED`と断定できない。実CLIはテストから直接起動していない。

## Windows managed settings（調査のみ）

`project/src`のClaude.Windows、Claude.Cli、Appを`managed`、`remote.settings`、`enterprise`、`policy`、配置path等で検索した。managed settingsのfile/registryを起動前に検査する実装は見つからない。現行の`--setting-sources ''`と未知画面のfail-closedは、managed policyの事前検出とは別である。

Anthropicの[managed settings公式文書](https://code.claude.com/docs/en/managed-settings)では、Windows system fileは`C:\Program Files\ClaudeCode\managed-settings.json`と任意の`managed-settings.d/*.json`、registryは`HKLM\SOFTWARE\Policies\ClaudeCode`の`Settings`値、補助的に`HKCU\SOFTWARE\Policies\ClaudeCode`の`Settings`値である。旧`C:\ProgramData\ClaudeCode\managed-settings.json`は読まない。[server-managed settings公式文書](https://code.claude.com/docs/en/server-managed-settings)では`~/.claude/remote-settings.json`は最終取得内容のキャッシュであり、起動時fetchや後続の更新がある。キャッシュ不在はserver-managed policy不在の証明にならない。

内容を読まずmetadataだけ確認した結果、上記system file、`managed-settings.d`、`managed-mcp.json`、`%USERPROFILE%\.claude\remote-settings.json`、HKLM key、所有者SIDに相当するHKU keyはすべて不在だった。現在のsandboxユーザーのHKCU keyも不在。実際のpolicy適用状況はCLIの`/status`などでの確認が別途必要で、今回の制約により実CLIを直接起動していない。提案: Windows側もMacのP0-5b/D12/D13と整合する起動前metadata検査を設計し、存在時はUnsupportedとする。実装判断はMac側の方針と合わせる。

## 今回の未コミット変更

- 計測・起動テストと導入の構成: `project/AiUsageMonitor.slnx`、`project/scripts/test-ai-usage-monitor.ps1`、`project/tests/AiUsageMonitor.App.Startup.Tests/`、`project/rebuild-ai-usage-monitor-x64.bat`、`project/rebuild-ai-usage-monitor-arm64.bat`。
- テスト可能性のためのApp実装: `project/src/AiUsageMonitor.App/AiUsageMonitor.App.csproj`、`App.xaml.cs`、`ClaudeSetupWindow.xaml.cs`、`TrayController.cs`。通常起動時の既定動作は同じ依存を生成する。
- 追加テスト: `project/tests/AiUsageMonitor.App.Tests/BackgroundLayerCoordinatorTests.cs`、`MainWindowDisplayTests.cs`、`MainWindowRepositionTests.cs`、`OnboardingTests.cs`、`SettingsWindowTests.cs`、`TrayControllerTests.cs`、`project/tests/AiUsageMonitor.Claude.Cli.Tests/ClaudeCliCapabilityProbeTests.cs`、`ClaudeCliUsageScreenParserTests.cs`。
- 計測scriptと起動テストのSHA-256: `test-ai-usage-monitor.ps1` `BC812C70D0B13728FD538CD8344F643AF7E93ADAC703779320792095519D72DA`、`AppStartupTests.cs` `670250ED7F70AC56F5E52C7B6BC886BA0FAA251AA4F74BDA05A02C56453479DC`。ほかの変更ファイルの完全な列挙はGit working treeを参照する。
