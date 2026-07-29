# ADR 003: 公式Claude CLI境界内の複数usage sourceを使用する

## Status

Accepted (2026-07-24、旧2026-07-23決定を置換) / Amended by ADR 005 (2026-07-27)

ADR 005は、Automaticの優先順位、単一merger所有、旧generationの遅延commitという3点だけを置換する。active CLIの起動・trust・Job Object・fail-closed・privacy・reset validationに関する本ADRの決定は引き続き有効である。

## Decision

Claude使用上限は、次の公式Claude Code client由来sourceから取得する。

1. 利用者の常駐sessionから届くpassive statusLine
2. 監視アプリが起動した公式CLIの`/usage` accessibility画面を厳格に読むactive source

既定の`Automatic`はfreshなpassive観測を優先し、ない場合だけactive sourceを実行する。`StatusLineOnly`と`OfficialCliOnly`も設定で選べる。

active sourceは署名済みnative `claude.exe`を専用の空フォルダーで`--safe-mode --ax-screen-reader`付きの隠しconsoleとして起動する。ConPTYは使用せず、別helper processが`AttachConsole`、`ReadConsoleOutputCharacterW`、`WriteConsoleInputW`を使用する。画面はtrust/setup/signed-out/ready/usageの状態判定と、usage画面の既知2sectionの厳格parseにだけ使う。

CLIへ送る入力は`Ready`画面での`/usage`と終了時のEscapeだけである。workspace trustは利用者自身が公式CLIで1度承認し、監視アプリは自動承認しない。unknown画面はfail-closedとする。

CLIへはuser/project/local customizationを除外する引数、`--safe-mode`、`--ax-screen-reader`、`--strict-mcp-config`、空のtools、browser無効化を渡す。active取得はstatusLineや一時settingsに依存しない。process treeはJob Objectへ格納し、monitor終了時にも残さない。

認証ファイル、API key、OAuth token、browser cookie、非公開usage endpoint、dummy prompt、Claude Desktop UI Automationは使用しない。managed settingsは上書き不能な既知の制約としてfail-closedにする。

usage観測状態はmonitor process内で長寿命の単一mergerが所有し、active sourceの実行fileまたはtimeout変更でcoordinatorを再生成する場合も同じmergerをconstructor injectionする。同一source構成ではcoordinator自体を再利用し、single-flight、failure backoff、取得元も維持する。値を別snapshotとして再投入してfreshnessを更新しない。

passive入力は`ObservationReceived`から現在coordinatorの`ObservePassive`だけを呼び、`SnapshotChanged`を経由してUIへ通知する。pipe eventからUIへ直接通知しない。coordinator再生成時は旧eventを解除し、generation guardで遅延完了によるUIの巻き戻しを防ぐ。旧active取得が共有mergerへ遅れて到着した場合は、mergerの`TakenAt`とreset window規則で新旧を決める。

接続テスト完了はsettingsへ保存するが、使用率の実値は保存しない。process開始後にsource観測をまだ受領していない場合、接続テスト完了済みなら`Waiting`、未完了なら`Setup`として表示する。実際のError、SignedOut、Unsupported、trust要求は観測済み状態として扱い、`Waiting`で隠さない。

## Consequences

- CLIを利用者が常駐させなくても現在の5時間・週間枠を取得できる。
- 初回だけ専用フォルダーの明示的なtrust操作が必要になる。
- active refreshはnative CLIのcold startを伴うため、既定300秒より短い間隔にはしない。
- CLI更新で画面署名やflagが変わった場合は誤入力せず`Unsupported`または`Error`になる。
- 生画面、usage値、account情報はディスクへ保存しない。
- 設定画面の保存やactive source再構成では、process内のfreshな観測値を失わない。
- 再起動後の`StatusLineOnly`は最初のpassive観測までWaiting表示になる。
- 2026-07-24の実測でB1 active statusLineは`NO-GO_NO_PAYLOAD`だったため、B2 strict screen parserを採用した。
# Reset時刻のfail-closed契約（2026-07-26追記）

CLI画面由来のreset候補は、既知windowの期間と2分の表記精度許容を上限として検証する。
日付なしの過去時刻は原則として翌日へ繰り上げない。ただし5時間枠のTokyo
time-only表記だけは、翌日候補が5時間＋2分以内に収まる場合に限りcross-midnight
resetとして受理する。これにより日付をまたぐ正当な短時間候補を維持しつつ、
`Resets 2pm`を約24時間後へ送るような汚染はhorizonで拒否する。週間枠は翌日へ
繰り上げない。月日付きの年跨ぎもwindow horizon内の場合だけ受理する。
未対応形式および東京以外の明示zoneは専用reasonでfail-closedとする。
mergerは保持中resetがhorizon外または期限切れの場合に限り、妥当なincomingを優先して
再起動なしで回復する。拒否したincomingで正常取得日時を更新してはならない。
reset専用reasonは`USAGE_RESET_AMBIGUOUS`、`USAGE_RESET_OUT_OF_RANGE`、
`USAGE_RESET_TIME_ZONE_UNSUPPORTED`、`USAGE_RESET_FORMAT_UNSUPPORTED`とする。
reasonとは別に保持できる診断contextは表記category、zone category、候補との差（分）、
window期間に限定し、生行、使用率、account、絶対日時を含めない。
