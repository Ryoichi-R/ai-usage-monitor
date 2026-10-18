# macOS P0-6 署名・起動対象同一性プローブ

このディレクトリのprobeは、実CLIを起動せずにClaude Codeの署名を確認し、別のad-hoc署名fakeでTOCTOU対策候補を検証します。製品実装や実CLIの生成・終了管理を代替するものではありません。

## Claude CLIの静的署名確認（実行しない）

`verify.swift`は実行ファイルをFDで開き、`/dev/fd/N`経由でSecurity.frameworkのstrict explicit requirementを検証します。expected Team IDはレビュー済み値を呼出側から渡します。対象バイナリから期待値を決めてはいけません。

```sh
swiftc -module-cache-path /private/tmp/ai-usage-swift-module-cache \
  project/probes/macos-code-signing/verify.swift \
  -o /private/tmp/ai-usage-macos-signature-probe
/private/tmp/ai-usage-macos-signature-probe \
  "$HOME/.local/share/claude/versions/2.1.274" Q6L2SF6YDW

clang -std=c17 -Wall -Wextra -Werror \
  project/probes/macos-code-signing/fd-exec-boundary.c \
  -o /private/tmp/ai-usage-fd-exec-boundary
/private/tmp/ai-usage-fd-exec-boundary
```

FD実行境界probeは`posix_spawn("/dev/fd/N", ...)`と`fork`後の`execv("/dev/fd/N", ...)`が`EACCES`になることをfake executableで示します。Claude CLIは起動しません。

## 停止起動 verifier の fake-only 再現

`POSIX_SPAWN_START_SUSPENDED`でfakeをユーザーコード開始前に停止させ、Security.frameworkでPIDの動的コード署名を確認し、開いたFDから事前検証したコードの`kSecCodeInfoUnique`と一致した場合だけ`SIGCONT`します。fakeには空のenvironmentを渡すため、呼出側のcredentialや`DYLD_*`変数は継承されません。パス置換モードは専用owner-only scratch directory内のファイルだけを書き換える安全確認です。

```sh
scratch=$(mktemp -d /private/tmp/ai-usage-suspended-launch-probe.XXXXXXXX)
clang -std=c17 -Wall -Wextra -Werror \
  -DPROBE_MARKER_VALUE='"approved"' \
  project/probes/macos-code-signing/fake-launch-target.c -o "$scratch/approved"
clang -std=c17 -Wall -Wextra -Werror \
  -DPROBE_MARKER_VALUE='"replacement"' \
  project/probes/macos-code-signing/fake-launch-target.c -o "$scratch/replacement"
codesign --force --sign - --identifier org.example.ai-usage-suspended-probe "$scratch/approved"
codesign --force --sign - --identifier org.example.ai-usage-suspended-probe "$scratch/replacement"
cp "$scratch/approved" "$scratch/launch-approved"
cp "$scratch/approved" "$scratch/launch-swap"
cp "$scratch/replacement" "$scratch/launch-mismatch"
swiftc -module-cache-path /private/tmp/ai-usage-swift-module-cache \
  project/probes/macos-code-signing/suspended-launch-verifier.swift \
  -o /private/tmp/ai-usage-suspended-launch-verifier
/private/tmp/ai-usage-suspended-launch-verifier "$scratch/approved" "$scratch/launch-approved" "$scratch/marker-approved"
/private/tmp/ai-usage-suspended-launch-verifier "$scratch/approved" "$scratch/launch-mismatch" "$scratch/marker-mismatch"
cp "$scratch/replacement" "$scratch/replacement-after-spawn"
/private/tmp/ai-usage-suspended-launch-verifier "$scratch/approved" "$scratch/launch-swap" "$scratch/marker-swap" --replace-after-spawn "$scratch/replacement-after-spawn"
```

成功ケースではmarkerに`approved`が入り、不一致ケースではmarkerを作る前に拒否されます。置換ケースは`SecCodeCheckValidity`が`-67034`（guest code invalidation）で失敗し、子をkill/reapしてmarkerがないことを確認しました。macOS/SDKの動作差で動的検証が通った場合でも、実行イメージのunique identityが事前検証値と一致しなければ再開しません。

## Mac Studioでの検証結果（2026-09-27）

Claude Code CLI 2.1.274 arm64は未起動です。`codesign --verify --strict`とSecurity.frameworkのstrict explicit requirement（Apple anchor / identifier / Developer ID chain OID / Team ID）は現在のCLIファイルで成功し、署名情報とFD/pathのfile identityも確認しました。初回検査では同じ記録済みdevice/inode/size/hashで両検証が失敗しており、その原因は未解明です。

fake-only probeは、承認コードの停止起動・動的署名検証・unique identity一致・再開、同じidentifierで署名内容が異なる実行イメージの再開前拒否、spawn後のlaunch path置換拒否を確認しました。通常のFD path execが使えなくても、停止中の実行イメージ自体を検証する候補を実証しました。

Appleの[`POSIX_SPAWN_START_SUSPENDED`](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/posix_spawnattr_getflags.3.html)文書は、子taskをuserspace実行前に停止すると説明します。Security.frameworkの[`SecCodeCopyGuestWithAttributes`](https://developer.apple.com/documentation/security/seccodecopyguestwithattributes%28_%3A_%3A_%3A_%3A%29)はPIDをguest attributeとして受け取れ、[`SecCodeCheckValidity`](https://developer.apple.com/documentation/security/seccodecheckvalidity%28_%3A_%3A_%3A%29)で実行中コードを検証できます。

## 判定

P0-6のPhase 0 feasibility条件は完了です。インストール済みClaude CLIの静的full requirement検証と、fakeによる停止起動・実行イメージ同一性・差替え拒否の方式実証が揃いました。production verifierへの統合はPhase 3、実CLIのcapability probeと隔離確認はP0-5bで行います。P0-9の監督helper/PTY契約とmanaged source確認が終わるまでは実CLIを起動しません。signed release manifestは未検証の補足事項として残します。監視アプリ自身のad-hoc署名はCLI trustの根拠に使いません。
