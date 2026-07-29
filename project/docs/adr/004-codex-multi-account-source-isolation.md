# ADR 004: Codex複数アカウントの取得元分離

## 決定

各ChatGPTアカウントは別の `CODEX_HOME` と別の公式 `codex app-server` 子プロセスで監視する。既定アカウントだけは親プロセスの環境を継承する。追加アカウントのhomeが無効または重複している場合、別homeへfallbackしない。

金額、残高、使用上限windowは `account/rateLimits/read` で選択した同一bucketから取得する。認証ファイル、cookie、非公開endpoint、Webスクレイピングは使用しない。

`CODEX_HOME` は認証だけでなくconfig、log、sessionを含むCodex stateの分離境界である。子プロセス固有の環境だけへ設定し、親プロセス環境は変更しない。client configurationは生成後に変更せず、home変更時はそのアカウントのclientとprocessを再生成する。

実効home重複はprocess起動前に設定順で検出し、後続行を停止する。さらに `account/read` が非null emailを返したChatGPTアカウントは、process内メモリだけで大文字小文字を無視して比較する。同じidentityの後続行は `DUPLICATE_ACCOUNT` として停止し、手動更新時だけ直列probeしてサインイン変更からの復旧を確認する。emailがnullのaccountはidentity重複と判定しない。

## 理由

異なる認証状態と値が混ざることを防ぎ、設定変更時もアカウント単位でprocess、通知、直近状態を分離するため。可用性より誤表示防止を優先し、設定順を表示順・重複時の優先順として一貫させる。

## 影響

有効アカウント数だけ常駐processが増える。子processは共有kill-on-close Job Objectへ所属し、アプリ終了時の孤児化を防ぐ。設定にはhomeパスを保存するが、email、利用値、金額、残高、認証内容は保存しない。identity比較用emailは公開snapshotやViewModelへ渡さず、ログにも出力しない。
