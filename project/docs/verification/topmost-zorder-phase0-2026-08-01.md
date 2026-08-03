# TopMost Z-order Phase 0 verification

実施日: 2026-08-01（Asia/Tokyo）  
対象: `C:\coding\ai-usage-monitor\project`  
テスト: `TopmostFlagProbeTests.OwnedModalProbeRecordsTheAdoptedNoOwnerZOrderContract`

## Candidate flags

両候補とも `SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE` を含め、`SWP_NOZORDER` は含めない。

| candidate | flags | owned dialog > owner | foreground changed by `NOACTIVATE` |
|---|---:|---|---|
| with `SWP_NOOWNERZORDER` | `0x213` | PASS | 実行セッションのforeground制約によりdialog HWNDへ変更されず |
| without `SWP_NOOWNERZORDER` | `0x013` | PASS | 実行セッションのforeground制約によりdialog HWNDへ変更されず |

実測ログでは両候補で `SetWindowPos=True`、owned dialogのZ-orderはownerより上位だった。foreground HWNDは候補flagsの差によらず既存のforegroundに留まった。`NOACTIVATE` によるフォーカス非奪取契約には整合するが、test runnerのforeground移譲制約により「dialogをforegroundへ移す」操作自体はこのセッションでは成立しなかった。

ownerless widgetを競合TopMost windowより上位へ移す実測と、owned dialogの相対順序を優先し、本番controllerの採用flagsは次の一意な集合とする。

```text
SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE = 0x013
```

foregroundを変更しないこと、`SWP_NOZORDER`を含めないことは本番controllerとunit testで固定する。Interactive統合テストはforeground移譲がOS側で拒否された場合、その理由を標準出力へ記録して終了する。

`SetWinEventHook` は対象SDKのRelease buildでsource-generated `LibraryImport` bindingが警告・エラーなしで成立したため採用した。`UnhookWinEvent` と `SetWindowPos` は同じthin shim内の単純な`DllImport` bindingとして分離し、managed delegateの寿命はcontroller fieldで保持する。
