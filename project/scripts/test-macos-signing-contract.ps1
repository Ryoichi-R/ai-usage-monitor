#requires -Version 7.4
[CmdletBinding()]
param([string]$FixtureRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'MACOS_REQUIRED' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = if ($FixtureRoot) { [IO.Path]::GetFullPath($FixtureRoot) } else { Join-Path $root ('artifacts/signing-contract-' + [Guid]::NewGuid().ToString('N')) }
if (-not $fixture.StartsWith((Join-Path $root 'artifacts') + '/', [StringComparison]::Ordinal) -or (Test-Path -LiteralPath $fixture)) { throw 'FRESH_ARTIFACT_FIXTURE_REQUIRED' }
New-Item -ItemType Directory -Path $fixture | Out-Null
$sourceParent = Join-Path $fixture 'source'
$app = Join-Path $sourceParent 'AI Usage Monitor.app'
$macos = Join-Path $app 'Contents/MacOS'
New-Item -ItemType Directory -Path (Join-Path $macos 'work-status-bridge') -Force | Out-Null
$code = Join-Path $fixture 'fixture.c'
'int main(void) { return 0; }' | Set-Content -LiteralPath $code -Encoding utf8NoBOM
& clang -arch arm64 -Wall -Wextra -Werror $code -o (Join-Path $macos 'AiUsageMonitor.App.Mac')
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_CLANG_FAILED' }
foreach ($name in @('ai-usage-process-supervisor', 'ai-usage-claude-statusline', 'work-status-bridge/ai-usage-work-status-bridge')) {
    Copy-Item -LiteralPath (Join-Path $macos 'AiUsageMonitor.App.Mac') -Destination (Join-Path $macos $name)
    & /usr/bin/codesign --force --sign - (Join-Path $macos $name)
    if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_SIGN_FAILED' }
}
'int fixture(void) { return 1; }' | Set-Content -LiteralPath $code -Encoding utf8NoBOM
& clang -arch arm64 -dynamiclib -Wall -Wextra -Werror $code -o (Join-Path $macos 'libfixture.dylib')
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_LIBRARY_FAILED' }
& /usr/bin/codesign --force --sign - (Join-Path $macos 'libfixture.dylib')
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_SIGN_FAILED' }
@'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>io.github.ryoichi-r.ai-usage-monitor.signing-test</string><key>CFBundleExecutable</key><string>AiUsageMonitor.App.Mac</string></dict></plist>
'@ | Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8NoBOM
$resource = Join-Path $macos 'managed-resource.dll'
'synthetic non-Mach-O resource' | Set-Content -LiteralPath $resource -Encoding utf8NoBOM
& /usr/bin/codesign --force --sign - $resource
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_GENERIC_SIGN_FAILED' }
& /usr/bin/codesign --force --sign - $app
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_BUNDLE_SIGN_FAILED' }
$files = @(Get-ChildItem -LiteralPath $app -File -Recurse | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($app, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifestPath = Join-Path $sourceParent 'bundle-manifest.json'
[ordered]@{ runtime = 'osx-arm64'; signature = 'ad-hoc'; acceptance = 'NOT_ACCEPTED'; files = $files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$originalManifest = Get-Content -LiteralPath $manifestPath -Raw
$prepare = Join-Path $PSScriptRoot 'prepare-ai-usage-monitor-macos-signing.ps1'
$count = 0
function Assert-Contract([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "SIGNING_CONTRACT_FAILED: $Name" }
    $script:count++
}
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-Contract ($failure.Contains($Reason, [StringComparison]::Ordinal)) $Reason
}
$invalidOutput = Join-Path $fixture 'invalid internal-test readiness-blocked'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $invalidOutput -SigningMode DeveloperId } 'DEVELOPER_ID_CERTIFICATE_SHA1_REQUIRED'
Assert-Contract (-not (Test-Path -LiteralPath $invalidOutput)) 'No output or credential lookup before identity validation'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $invalidOutput -SigningIdentity 'not-used' } 'IDENTITY_NOT_USED_FOR_ADHOC'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot (Join-Path ([IO.Path]::GetTempPath()) 'aiusage-signing-forbidden') } 'PATH_MUST_BE_WITHIN_PROJECT_ARTIFACTS'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot (Join-Path $app 'nested') } 'OUTPUT_INSIDE_SOURCE_APP'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $sourceParent } 'OUTPUT_ALREADY_EXISTS'
$bad = $originalManifest | ConvertFrom-Json
$bad.files[0].sha256 = '0' * 64
$bad | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $invalidOutput } 'SOURCE_HASH_MISMATCH'
$bad.files[0].path = '../outside'
$bad | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $invalidOutput } 'SOURCE_MANIFEST_PATH_INVALID'
$bad = $originalManifest | ConvertFrom-Json
$bad.files[1].path = 'Contents/../' + $bad.files[0].path
$bad.files[1].sha256 = $bad.files[0].sha256
$bad | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot $invalidOutput } 'SOURCE_MANIFEST_PATH_INVALID'
$originalManifest | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM -NoNewline
$alias = Join-Path $fixture 'alias.app'
& /bin/ln -s $app $alias
if ($LASTEXITCODE -ne 0) { throw 'FIXTURE_LINK_FAILED' }
Assert-Rejected { & $prepare -SourceBundle $alias -OutputRoot $invalidOutput } 'SYMLINK_NOT_ALLOWED'
$linkedParent = Join-Path $fixture 'manifest-link'
New-Item -ItemType Directory -Path $linkedParent | Out-Null
$linkedApp = Join-Path $linkedParent 'AI Usage Monitor.app'
& /usr/bin/ditto $app $linkedApp
if ($LASTEXITCODE -ne 0) { throw 'LINK_FIXTURE_COPY_FAILED' }
& /bin/ln -s $manifestPath (Join-Path $linkedParent 'bundle-manifest.json')
if ($LASTEXITCODE -ne 0) { throw 'LINK_MANIFEST_FIXTURE_FAILED' }
Assert-Rejected { & $prepare -SourceBundle $linkedApp -OutputRoot $invalidOutput } 'SOURCE_MANIFEST_REQUIRED'
Assert-Rejected { & $prepare -SourceBundle $app -OutputRoot (Join-Path $fixture 'unmarked') } 'UNACCEPTED_OUTPUT_MARKERS_REQUIRED'
$whatIf = Join-Path $fixture 'whatif internal-test readiness-blocked'
& $prepare -SourceBundle $app -OutputRoot $whatIf -WhatIf
Assert-Contract (-not (Test-Path -LiteralPath $whatIf)) 'WhatIf leaves the filesystem unchanged'
$output = Join-Path $fixture 'prepared internal-test readiness-blocked'
& $prepare -SourceBundle $app -OutputRoot $output
$receipt = Get-Content -LiteralPath (Join-Path $output 'signing-receipt.json') -Raw | ConvertFrom-Json
Assert-Contract ($receipt.hardened_runtime -and $receipt.signature_verified -and $receipt.notarization -eq 'NOT_SUBMITTED' -and $receipt.gatekeeper -eq 'NOT_ACCEPTED') 'Signature is not a notarization or Gatekeeper pass'
Assert-Contract ($receipt.runtime_acceptance -eq 'NOT_TESTED' -and $receipt.runtime_eligibility -eq 'INSPECTION_ONLY_NO_TEAM_ID') 'Ad-hoc inspection is not execution acceptance'
Assert-Contract ($receipt.managed_entitlements.Count -eq 1 -and $receipt.managed_entitlements[0] -eq 'com.apple.security.cs.allow-jit') 'Only the required JIT exception'
foreach ($name in @('AiUsageMonitor.App.Mac', 'ai-usage-process-supervisor', 'ai-usage-claude-statusline', 'work-status-bridge/ai-usage-work-status-bridge', 'libfixture.dylib')) {
    $binary = Join-Path $output "AI Usage Monitor.app/Contents/MacOS/$name"
    $details = (& /usr/bin/codesign -d --verbose=4 $binary 2>&1 | Out-String)
    Assert-Contract ($LASTEXITCODE -eq 0 -and $details -match 'flags=.*runtime') "Hardened runtime: $name"
    $rights = (& /usr/bin/codesign -d --entitlements :- $binary 2>$null | Out-String)
    $managed = $name -in @('AiUsageMonitor.App.Mac', 'ai-usage-claude-statusline', 'work-status-bridge/ai-usage-work-status-bridge')
    Assert-Contract (($rights -match 'com.apple.security.cs.allow-jit') -eq $managed) "Executable-only entitlement policy: $name"
    Assert-Contract ($rights -notmatch 'get-task-allow|disable-library-validation|allow-unsigned-executable-memory|cs.debugger') "No unrelated runtime exceptions: $name"
}
$genericDetails = (& /usr/bin/codesign -d (Join-Path $output 'AI Usage Monitor.app/Contents/MacOS/managed-resource.dll') 2>&1 | Out-String)
Assert-Contract ($LASTEXITCODE -eq 0) 'Generic object in the MacOS code directory remains signed'
$sourceGenericDetails = (& /usr/bin/codesign -d $resource 2>&1 | Out-String)
Assert-Contract ($LASTEXITCODE -eq 0) 'Original generic signature is preserved'
foreach ($file in $files) {
    Assert-Contract ((Get-FileHash -LiteralPath (Join-Path $app $file.path) -Algorithm SHA256).Hash -ieq $file.sha256) 'Source bundle retained byte-for-byte'
}
& /usr/bin/codesign --verify --deep --strict $app
if ($LASTEXITCODE -ne 0) { throw 'SOURCE_WAS_CHANGED' }
[ordered]@{ passed = $count; failed = 0; fixture = $fixture } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $fixture 'contract-receipt.json') -Encoding utf8NoBOM
Write-Host "Mac signing contract passed: $count assertions. Fixtures retained: $fixture"
