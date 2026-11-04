#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SourceBundle,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('HardenedAdHoc', 'DeveloperId')][string]$SigningMode = 'HardenedAdHoc',
    [string]$SigningIdentity
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'MACOS_REQUIRED' }
# An identity is supplied explicitly; never enumerate Keychain identities or credentials.
if ($SigningMode -eq 'DeveloperId' -and $SigningIdentity -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'DEVELOPER_ID_CERTIFICATE_SHA1_REQUIRED'
}
if ($SigningMode -ne 'DeveloperId' -and $SigningIdentity) { throw 'IDENTITY_NOT_USED_FOR_ADHOC' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$source = [IO.Path]::GetFullPath($SourceBundle).TrimEnd('/')
$destination = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('/')
foreach ($path in @($source, $destination)) {
    if (-not $path.StartsWith($artifactsRoot + '/', [StringComparison]::Ordinal)) { throw 'PATH_MUST_BE_WITHIN_PROJECT_ARTIFACTS' }
    $ancestor = $path
    while ($ancestor -ne $projectRoot) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).LinkType) { throw 'SYMLINK_NOT_ALLOWED' }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}
if (-not $source.EndsWith('.app', [StringComparison]::Ordinal) -or -not (Test-Path -LiteralPath $source -PathType Container)) { throw 'SOURCE_APP_REQUIRED' }
if ($destination.StartsWith($source + '/', [StringComparison]::Ordinal)) { throw 'OUTPUT_INSIDE_SOURCE_APP' }
if (Test-Path -LiteralPath $destination) { throw 'OUTPUT_ALREADY_EXISTS' }
$sourceFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object LinkType).Count) { throw 'SYMLINK_NOT_ALLOWED' }
$sourceManifest = Join-Path (Split-Path -Parent $source) 'bundle-manifest.json'
if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf) -or (Get-Item -LiteralPath $sourceManifest -Force).LinkType) { throw 'SOURCE_MANIFEST_REQUIRED' }
$manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'osx-arm64' -or @($manifest.files).Count -ne $sourceFiles.Count) { throw 'SOURCE_MANIFEST_INVALID' }
$actualPaths = [Collections.Generic.HashSet[string]]::new([string[]]$sourceFiles.FullName, [StringComparer]::Ordinal)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    $full = [IO.Path]::GetFullPath((Join-Path $source $relative))
    if ([IO.Path]::IsPathRooted($relative) -or -not $full.StartsWith($source + '/', [StringComparison]::Ordinal) -or -not $actualPaths.Contains($full) -or -not $seen.Add($full)) { throw 'SOURCE_MANIFEST_PATH_INVALID' }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ine $file.sha256) { throw 'SOURCE_HASH_MISMATCH' }
}
& /usr/bin/codesign --verify --deep --strict $source
if ($LASTEXITCODE -ne 0) { throw 'SOURCE_SIGNATURE_INVALID' }
$managedNames = @('AiUsageMonitor.App.Mac', 'ai-usage-claude-statusline', 'ai-usage-work-status-bridge')
$entryPaths = @('AiUsageMonitor.App.Mac', 'ai-usage-process-supervisor', 'ai-usage-claude-statusline', 'work-status-bridge/ai-usage-work-status-bridge')
foreach ($relative in $entryPaths) {
    & /usr/bin/lipo -verify_arch arm64 (Join-Path $source "Contents/MacOS/$relative")
    if ($LASTEXITCODE -ne 0) { throw 'ARM64_ENTRYPOINT_REQUIRED' }
}
$outputName = Split-Path -Leaf $destination
if (-not $outputName.Contains('internal-test', [StringComparison]::Ordinal) -or -not $outputName.Contains('readiness-blocked', [StringComparison]::Ordinal)) { throw 'UNACCEPTED_OUTPUT_MARKERS_REQUIRED' }
if (-not $PSCmdlet.ShouldProcess($destination, "Copy and prepare $SigningMode signing candidate; no notarization upload")) { return }
New-Item -ItemType Directory -Path $destination | Out-Null
$app = Join-Path $destination 'AI Usage Monitor.app'
& /usr/bin/ditto $source $app
if ($LASTEXITCODE -ne 0) { throw 'BUNDLE_COPY_FAILED' }
$entitlements = Join-Path $destination 'managed-runtime.entitlements.plist'
@'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>com.apple.security.cs.allow-jit</key><true/></dict></plist>
'@ | Set-Content -LiteralPath $entitlements -Encoding utf8NoBOM
$identity = if ($SigningMode -eq 'DeveloperId') { $SigningIdentity } else { '-' }
$timestamp = if ($SigningMode -eq 'DeveloperId') { '--timestamp' } else { '--timestamp=none' }
$macos = Join-Path $app 'Contents/MacOS'
$main = Join-Path $macos 'AiUsageMonitor.App.Mac'
$native = [Collections.Generic.List[string]]::new()
# Sign nested code before the bundle; only Mach-O receives native runtime flags.
foreach ($file in @(Get-ChildItem -LiteralPath $macos -File -Recurse | Sort-Object FullName -Descending)) {
    $description = & /usr/bin/file -b $file.FullName
    if ($LASTEXITCODE -ne 0) { throw 'FILE_CLASSIFICATION_FAILED' }
    if ($description -notlike '*Mach-O*') {
        # Contents/MacOS is a code directory: generic DLL/data objects also need
        # signatures. Re-sign them with the selected identity, without runtime
        # flags or entitlements, instead of retaining the publisher's ad-hoc one.
        & /usr/bin/codesign --force --sign $identity $timestamp $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'GENERIC_CODE_SIGN_FAILED' }
        continue
    }
    $native.Add([IO.Path]::GetRelativePath($app, $file.FullName))
    if ($file.FullName -eq $main) { continue }
    $arguments = @('--force', '--sign', $identity, '--options', 'runtime', $timestamp)
    if ($file.Name -in $managedNames) { $arguments += @('--entitlements', $entitlements) }
    & /usr/bin/codesign @arguments $file.FullName
    if ($LASTEXITCODE -ne 0) { throw 'NESTED_RUNTIME_SIGN_FAILED' }
}
& /usr/bin/codesign --force --sign $identity --options runtime $timestamp --entitlements $entitlements $app
if ($LASTEXITCODE -ne 0) { throw 'RUNTIME_BUNDLE_SIGN_FAILED' }
& /usr/bin/codesign --verify --deep --strict $app
if ($LASTEXITCODE -ne 0) { throw 'RUNTIME_SIGNATURE_INVALID' }
$details = (& /usr/bin/codesign -d --verbose=4 $app 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $details -notmatch 'flags=.*runtime') { throw 'HARDENED_RUNTIME_NOT_PRESENT' }
if ($SigningMode -eq 'DeveloperId' -and ($details -notmatch 'Authority=Developer ID Application:' -or $details -notmatch 'Timestamp=')) { throw 'DEVELOPER_ID_TIMESTAMP_REQUIRED' }
if ($SigningMode -eq 'DeveloperId') {
    & /usr/bin/codesign --verify --strict -R 'anchor apple generic' $app
    if ($LASTEXITCODE -ne 0) { throw 'APPLE_ISSUED_IDENTITY_REQUIRED' }
}
# Never infer Gatekeeper acceptance from a valid signature. No account operations/uploads occur here.
$manifest.signature = if ($SigningMode -eq 'DeveloperId') { 'developer-id' } else { 'ad-hoc-hardened-runtime' }
$manifest.acceptance = 'NOT_ACCEPTED'
$manifest.files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($app, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'bundle-manifest.json') -Encoding utf8NoBOM
[ordered]@{
    schema_version = '1.0'; signing_mode = $SigningMode; hardened_runtime = $true
    runtime_acceptance = 'NOT_TESTED'
    runtime_eligibility = $(if ($SigningMode -eq 'DeveloperId') { 'REQUIRES_EXECUTION_ACCEPTANCE' } else { 'INSPECTION_ONLY_NO_TEAM_ID' })
    managed_entitlements = @('com.apple.security.cs.allow-jit'); native_files = @($native)
    signature_verified = $true; notarization = 'NOT_SUBMITTED'; gatekeeper = 'NOT_ACCEPTED'
    source_manifest_sha256 = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'signing-receipt.json') -Encoding utf8NoBOM
Write-Host 'Signature preparation only. Runtime, notarization and Gatekeeper acceptance are incomplete.'
