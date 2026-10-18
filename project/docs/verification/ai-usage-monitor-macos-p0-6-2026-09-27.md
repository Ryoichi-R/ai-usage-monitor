# macOS P0-6 Claude CLI署名・起動対象同一性の検証

日付: 2026-09-27（Asia/Tokyo）

## 対象と静的署名結果

- `~/.local/bin/claude`は`~/.local/share/claude/versions/2.1.274`へのsymlink。
- 実行ファイルはthin Mach-O arm64。
- 検証時のfile identity: device `16777234`、inode `998400`、size `214149552`、SHA-256 `3509913f9d1576316c8845b88837f8fd3bbbcf26625833ac82cfb6b8985da94a`。
- `codesign --verify --strict --verbose=2`は`valid on disk` / `satisfies its Designated Requirement`で成功。
- `codesign -dv --verbose=4`のidentifierは`com.anthropic.claude-code`、Team IDは`Q6L2SF6YDW`。authorityは`Developer ID Application: Anthropic PBC (Q6L2SF6YDW)` → `Developer ID Certification Authority` → `Apple Root CA`。
- Swift probeは実行ファイルをopenしたFDから`/dev/fd/N`を作り、そのpathに対しSecurity.frameworkのstrict explicit requirementを評価。requirementはApple generic anchor、Claude identifier、中間証明書OID `1.2.840.113635.100.6.2.6`、leaf OID `1.2.840.113635.100.6.1.13`、固定expected Team IDを要求する。結果`0`。
- `SecCodeCopySigningInformation`も`0`で、identifierとTeam IDは期待値に一致。誤ったidentifierのnegative controlは`-67050` (`errSecCSReqFailed`)で拒否。FDと元pathのdevice / inode / size / mtime / ctimeは検証前後で安定。
- 以前は同じ記録済みdevice / inode / size / hashで`codesign`とSecurity.frameworkが失敗した記録がある。原因は分かっていない。現在の両検証は反復して成功しているが、この矛盾は未解明のまま残す。

## 開いたFDのまま起動できるか

Claude CLIではなく、[再現用fake-only probe](../../probes/macos-code-signing/fd-exec-boundary.c)で自分自身だけを対象に検証した。

- `posix_spawn("/dev/fd/N", ...)`は`O_EXEC`で開いたFDでも`EACCES`。
- `fork`後の`execv("/dev/fd/N", ...)`も`EACCES`。
- ローカルmacOS SDKには`fexecve`宣言がなく、link時にも`_fexecve`が見つからなかった。`execveat`も公開SDK headerにはない。
- したがって、Security.frameworkのFD基準検証を通常のFD-path execへそのまま結び付ける方法は確認できなかった。その後の停止起動＋PID guest code検証方式は、次節のfake-only probeで別経路として実証した。

Appleの[`SecStaticCodeCreateWithPath`](https://developer.apple.com/documentation/security/1396899-secstaticcodecreatewithpath)文書はstatic code objectが指定path上のコードを表し、実行中コードには自動で結び付かないと説明する。[`SecStaticCodeCheckValidity`](https://developer.apple.com/documentation/security/secstaticcodecheckvalidity%28_%3A_%3A_%3A%29)文書は同時変更がない場合に限って安全で、コードが変更されるまで検証結果が有効とする。pathを再確認してからpath-based spawnを呼ぶだけでは、計画に定めたsame-file TOCTOU要件を満たした証拠にならない。

`spctl --assess --type execute --verbose=4`はinternal Code Signing subsystem errorとなり、成功・失敗の判定には使えなかった。

## 停止起動＋実行イメージ検証（fake-only）

`project/probes/macos-code-signing/suspended-launch-verifier.swift`と`fake-launch-target.c`を追加した。実CLIは起動せず、同一identifierのad-hoc署名fakeだけを使用した。

- 事前検証: 候補をopenしたFDの`/dev/fd/N`から`SecStaticCode`を作り、署名requirementをstrict検証。`kSecCodeInfoUnique`を保存する。
- 停止起動: `POSIX_SPAWN_START_SUSPENDED`と専用process groupでfakeをspawnし、markerがまだ存在しないことを確認する。呼出側environmentは継承せず、fakeへcredentialや`DYLD_*`変数を渡さない。
- 動的検証: PIDから`SecCodeCopyGuestWithAttributes`でguest codeを取得し、strict requirementを検証。`SecCodeCopyStaticCode`から得た`kSecCodeInfoUnique`が事前検証値と一致した場合だけ`SIGCONT`する。
- 正常fakeは停止中の署名・unique照合後に再開し、marker=`approved`を生成。
- 同じidentifierだが異なる署名内容のfakeは、動的署名自体は有効でもunique identity不一致で再開前に拒否され、markerなし。
- spawn後のlaunch-path置換は`SecCodeCheckValidity` status `-67034`で拒否され、子をkill/reapしmarkerなし。置換モードは`/private/tmp/ai-usage-suspended-launch-probe.*`配下のowner-only scratch directoryに限定した。

この方式は、FDをそのまま`exec`できることに依存せず、停止状態の実行イメージを検証してから再開するfeasibility候補である。実CLI停止起動、.NET / native supervisorへの製品統合、helper異常終了・PTY controlling-terminal契約との接続は未検証で、Phase 3 / P0-9に残る。これらをP0-6 Phase 0 feasibilityの未完了とは扱わない。

## その他の安全条件

Anthropicの[公式integrity文書](https://code.claude.com/docs/en/getting-started)は、署名付きrelease manifestをGPG keyで検証し、`darwin-arm64`のSHA-256と照合する手順を示す。manifest / detached signature / keyは取得済みだが、MacにGPG verifierがなく署名検証は未実施。manifest checksumは信頼済み証拠としていない。

`profiles status -type enrollment`は`Enrolled via DEP: No` / `MDM enrollment: No`。これはローカルのconfiguration profileやClaude.ai / gatewayのserver-managed sourceの不在を示さない。設定内容は読んでいない。

metadataだけを確認し、次のmanaged-settings関連pathは不在だった: `/Library/Application Support/ClaudeCode/managed-settings.json`、`managed-settings.d`、`managed-mcp.json`、`~/.claude/remote-settings.json`。configuration profile、他のremote policy sourceを含む不在は確認できていない。設定内容やcredentialは読んでいない。

## 判定

**P0-6のPhase 0 feasibility条件は完了。** 実CLI本体は起動せずにfull explicit requirementで静的検証し、fake-only probeでは停止中PIDの動的署名と`kSecCodeInfoUnique`を照合してから再開する方式を検証した。署名内容不一致とspawn後path置換も拒否した。production verifierへの統合はPhase 3、実CLIのcapability / isolation検証はP0-5bに属する。managed profile / remote policy全sourceの確認とP0-9監督/PTY契約が未了のため、これらの安全ゲートが成立するまで実CLIは起動しない。Anthropic signed release manifest検証は補足事項として未完了。

添付画像はClaude Code 2.1.274の対話sessionが既に開いていることを示すが、この検証作業はCLI command / executableを新たに起動していない。
