# macOS P0-3 Claude CLI画面取得プローブ

`screen-capture.c`は、実際のClaude CLIをPTYで起動し、`/usage`の画面をメモリ上で矩形の画面へ復元します。起動・署名検証・子孫追跡・終了は、P0-9の[descendant-tracker](../macos-process-lifecycle/README.md)をそのまま使います。

- 生のバイト列と、匿名化前の画面は、ファイル・ログ・標準出力のいずれにも出しません。
- 標準出力に出すのは、制御シーケンスの種類と件数、画面の分類状態、禁止パターンの件数、プロセスの観測結果だけです。
- ファイルに書くのは、匿名化したうえで禁止パターンが0件だった候補だけです。書き込み先は、既存のowner-only（mode 700）ディレクトリに限ります。

```sh
clang -std=gnu17 -Wall -Wextra -Werror \
  project/probes/macos-claude-screen/screen-capture.c \
  -framework Security -framework CoreFoundation \
  -o /private/tmp/ai-usage-screen-capture
/private/tmp/ai-usage-screen-capture --selfcheck
AIUSAGE_NO_DA_REPLY=1 /private/tmp/ai-usage-screen-capture Q6L2SF6YDW \
  "$HOME/.local/share/claude/versions/2.1.274" "<trust済みの専用空directory>" "<owner-only出力directory または ->" \
  --setting-sources "" --tools "" --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader
```

出力directoryに`-`を指定すると、ファイルを書かずに観測だけを行います。

## 画面モデル（PoC用の参照実装）

400桁×120行の画面に、UTF-8の文字（`wcwidth`で全角2桁）、C0制御、CSI、ESC、OSC、DCS / APC / PMを適用します。これはPoC用の参照実装であり、製品のVTモデルそのものではありません（製品での扱いはP0-4 / D5で決めます）。

- **描画に反映するもの**: カーソル移動、消去、スクロール領域、alt screen、カーソルの保存・復元。
- **無視するもの**: SGRと、描画に影響しない問い合わせ・モード。
- **それ以外**: `unsupported`として数え、画面を無効として扱う根拠にします。
- **応答**: DSRには応答します。DA1は`AIUSAGE_NO_DA_REPLY=1`で無応答にできます。

## 結果（2026-09-27、Claude CLI 2.1.274、Mac Studio / macOS 27.0）

作業directoryには、利用者がtrustを承認した`~/Library/Application Support/AiUsageMonitor-probe/ClaudeCliWorkspace`を使いました。取得は5回行い、すべて成功しました（DA1に応答あり2回、応答なし3回。後半2回で候補を書き出し）。

- 5回とも、Ready・`/usage`・Esc後のReady復帰を判定できました。
- group離脱0、残留0。子process `security` / `git` / `sh` / `bash` / `grep`はすべてgroup内で終了しました。
- 終了は、masterを開いたままのSIGTERMでは猶予内に終わらず、SIGKILLで回収しました（P0-9の結果と同じです）。

### 観測した制御シーケンス（screen-readerモード）

| 区分       | 種類                                                                                                                          | 扱い                     |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------- | ------------------------ |
| 描画       | CR、LF、`CSI A`（CUU）、`CSI B`（CUD）、`CSI G`（CHA）、`CSI K`（EL）、`CSI r`（DECSTBM）、`ESC 7` / `ESC 8`（DECSC / DECRC） | 必須                     |
| モード     | `?25h`、`?2004h/l`、`?1004h/l`、`?2031h/l`                                                                                    | 無視（文字に影響しない） |
| 問い合わせ | `CSI c`（DA1、2回）、`CSI > q`（XTVERSION）、`CSI ? u`（kitty keyboard）                                                      | 無応答で動作             |
| 設定       | `CSI > m`（modifyOtherKeys）                                                                                                  | 無視                     |
| その他     | `OSC 0`（タイトル）、`OSC 133`（shell統合の区切り）、`SI`（0x0f）、`ESC (` charset、BEL                                       | 無視                     |

- 5回とも出現しなかったもの: alt screen（`?1049`）、`CSI H`（CUP）、`CSI J`（ED）、SGR（`CSI m`）、全角文字、結合文字。
- 分類を細かくした後の4回では、`unsupported`は0件でした。最初の1回は、`?2031`、`OSC 1x`（実際は`OSC 133`）、`SI`が分類前のため`unsupported`と数えられていました。
- DA1に応答しなかった3回でも、画面の表示と`/usage`の操作は変わりませんでした。そのため、製品は問い合わせに応答しない方針にできます。
- 以上から、screen-readerモードで必要なVTの範囲は、行単位の上書き描画（CR / LF / CUU / CUD / CHA / EL）とスクロール領域に限られます。

### 表示言語（2026-09-27）

`AIUSAGE_CAPTURE_LANG=ja_JP.UTF-8`で、CLIへ渡す`LANG`を日本語にして1回観測しました（ファイルは書いていません）。

- Ready・`/usage`・Escの流れは英語のときと同じく成功し、画面は英語のままでした（全角文字0件）。
- 公式の`language`設定は、応答言語の設定と説明されています。また製品は`--setting-sources ""`で利用者の設定を読みません。
- このため、取得対象の画面は英語だけとみなします（計画D5）。

### 匿名化と禁止パターン

匿名化は、書き込み前にメモリ上で次の順に行います。

1. usageの内訳行（`<名前> NN%`。skillやagentなど）の名前を、`/skill-a`・`item-a`などの固定ダミー名にする
2. プラン名を`Claude Pro`に固定する
3. `/Users/<名前>`を`~`にする
4. メールアドレスを`user@example.com`にする
5. account名（`pw_name`、`pw_gecos`）を`User`にする
6. 曜日を`Sun`、月を`Aug`にする
7. `Asia/Tokyo`以外のtime zoneを`Asia/Tokyo`にする
8. 数字を置き換える: 分類用の版は`#`、parser用の版（`.parse.txt`）は`1`

検出する禁止パターンは、次の8区分です。

- 置換後の文字以外の数字
- `user@example.com`以外のメールアドレス
- account名
- `/Users/`
- 32文字以上のtoken状の文字列
- `Sun`以外の曜日
- `Aug`以外の月
- ダミー名以外の内訳行

`--selfcheck`は、合成した負例で次の3点を確認します。3つともPASSでした。

- 各区分を匿名化の前に検出できること
- 匿名化の後に除去されること
- token状の文字列は除去されず、書き込みを止めること

**目視で見つけた漏れ**: 初回の候補では、自動検出では見つからなかったskill名（実際に使っているskillの名前）が、usage画面の内訳に残っていました。この候補は削除し、上記の手順1と、内訳行の検出を追加してから取得し直しました。自動検出だけでは、この種の漏れは防げません。保存前の人による見直しは必須です。

### 製品の分類とparserでの確認

scratchのconsoleから、製品の`AiUsageMonitor.Claude.Cli`を参照して確認しました。

- `ClaudeCliScreenStateMachine.Classify`は6候補すべてで期待どおりでした（`ready-macos` / `after-escape-macos`はReady、`usage-screen-macos`はUsageScreen）。
- `ClaudeCliUsageScreenParser.Parse`は、parser用の版で`USAGE_RESET_FORMAT_UNSUPPORTED`となりました。
  - 原因は、実CLIの週リセット表示`Resets <Mon> <d> at <h:mm><am|pm> (Asia/Tokyo)`です。parserの日付付き形式はカンマ区切り（`MMM d, h:mmtt`）しか受け付けません。
  - 同じ行をカンマ形式に置き換えると、5時間枠・7日枠とも`Available`で解析できました。
  - 5時間枠の`Resets 11:11pm (Asia/Tokyo)`は、そのままで解析できます。
  - これはOSではなくCLIの表示形式による差です。Windows版でも同じCLI versionでは週の枠がErrorになる可能性が高いと考えられます（Windows実機では未確認）。

### 候補の保存先と状態

- 候補は`~/Library/Application Support/AiUsageMonitor-probe/fixture-candidates/2026-09-27/`（mode 700、各file 0600）に作成しました。
- 2026-09-27、ownerが別sessionで6件を見直し、秘密情報が含まれていないことを確認しました。
- 見直し時のSHA-256と一致することを確かめたうえで、`tests/fixtures/claude-cli-screens/macos/`へ保存しました。
- 保存したfixtureは`ClaudeCliScreenStateMachineTests`（6件）と`ClaudeCliUsageScreenParserTests`で使っています。

### parserの修正（TODO CUM-17）

`ClaudeCliUsageScreenParser`に、実表示の`MMM d at h[:mm]am/pm`形式を追加しました。

- 厳密な正規表現に一致した場合だけカンマ形式へ正規化し、既存の範囲検査・年跨ぎ・曖昧時刻の規則を共有します。
- 失敗時の診断カテゴリは`english_month_day_at`です。
- 24時間表記、時刻の欠落、月以外の語、余分な語が付いた形式は、従来どおり`USAGE_RESET_FORMAT_UNSUPPORTED`でfail-closedになります。
- 追加したテストは、修正前のparserでは6件が失敗し、修正後は全件が合格しました。

テスト結果（macOS用solution）: 386件すべて合格（Claude.Cli 104件）。

カバレッジは、P1-Mと同じ条件（`bin` / `obj` / testsを除外）で集計しました。

- 全体: 2,532 / 2,746行（92.21%）
- `ClaudeCliUsageScreenParser.cs`: 336 / 367行
- Claude.Cliアセンブリ: 89.75%（P1-M時点は89.01%）

ただし、Coreの集計値がP1-M記録と一致しないため、アセンブリの重複の数え方が完全には同一でない可能性があります。
