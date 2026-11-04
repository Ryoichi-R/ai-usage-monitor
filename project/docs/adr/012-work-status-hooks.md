# ADR 012: 作業状態は公式hooksから受け取る

- Status: Accepted for implementation（2026-11-03の利用者判断）
- 利用枠のデータ源: ADR 001どおりCodex App Serverを維持する。
- 作業状態のデータ源: Codex/Claudeの公式hooks。session JSONL、応答本文、transcript、認証情報は読まない。

2026-11-04の質問カード対応では、同じ公式hooks入力からtool名、call ID、質問のindex、および構造化回答envelopeの対応IDだけを抽出する。promptの通常本文や質問文・回答内容は永続化せず、IDもhash化する。非同期質問のtool結果は表示受付であり回答ではないため、通常のActivityやStopで待ち状態を解除しない。このDesktop専用envelopeの互換性と実受信は対象版ごとの検証が必要であり、公式hooksが専用toolを省略する版へは対応済みと宣言しない。

WindowsのCodex Desktopで2026-11-03にUserPromptSubmit/PreToolUse/PostToolUse/PermissionRequest/Stopを実受信した。Codexパッケージ内の実体名はapp/ChatGPT.exeだった。CLIそのもののcodex.exeの存在ではなく、Desktop祖先を証拠にする。監視アプリ自身が起動した利用枠取得は祖先で除外する。これは同一ユーザーに対する認証機構ではなく、対象セッションの分類である。

Stopは他のフックにより継続し得る。統合版Bridgeは現段階では停止候補を生成し、確定完了へ自動昇格させない。idle_prompt、agent_completed、モデル要求単位の失敗をメインturnの確定完了/失敗とは扱わない。Claude StopFailureはエラー、PermissionRequestは入力待ち、Interruptは中断。確定終端の実証を追加するまで、停止候補までの段階提供とする。

共通Coreは作業状態と未確認通知を別管理し、取得・保存障害や新しい作業で通知を消さない。確認操作は表示時点の通知ID集合に限定し、保存成功後に反映する。旧版の本文を含まないstateは明示選択による通知移行だけを行い、旧進行状態を現在の活動として引き継がない。

App.UIはOS別projectを参照しない。OS別の祖先判定はPlatform実装に置き、BridgeではそのファイルをOS条件付きでリンクする。Macはlibprocで親PID・実行ファイルpath・起動時刻だけを調べ、標準/ユーザーApplications内の対象.appに限定する。Macの実機受入は未実施で、移植コードの存在を実証済みとはしない。

新Bridgeは独立したself-contained成果物として同梱する。Windowsはprotocol/build内容hash単位の不変directory、Macは署名境界を保つ.app内のBridgeを参照する。使用中バイナリを停止・上書きせず、変更された接続先は利用者が再接続する。自動起動・既存のstatusLine・アプリの通常設定は接続例生成では変更しない。

公式資料（2026-11-03確認）:

- https://learn.chatgpt.com/docs/hooks
- https://code.claude.com/docs/en/hooks
- Mac ABI: Apple公開proc_info.hのproc_bsdinfo/PROC_PIDTBSDINFO（https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h）
