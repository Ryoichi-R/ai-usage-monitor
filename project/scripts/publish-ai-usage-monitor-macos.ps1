#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'MACOS_REQUIRED' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else {
    Join-Path $root ('artifacts/macos-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
}
if (-not $destination.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
    throw 'OUTPUT_MUST_BE_WITHIN_PROJECT'
}
if (Test-Path -LiteralPath $destination) { throw 'OUTPUT_ALREADY_EXISTS: choose a fresh output directory.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$app = Join-Path $destination 'AI Usage Monitor.app'
$contents = Join-Path $app 'Contents'
$macos = Join-Path $contents 'MacOS'
$resources = Join-Path $contents 'Resources'
New-Item -ItemType Directory -Path $macos, $resources | Out-Null
$project = Join-Path $root 'src/AiUsageMonitor.App.Mac/AiUsageMonitor.App.Mac.csproj'
& dotnet publish $project -c Release -r osx-arm64 --self-contained true -o $macos --nologo
if ($LASTEXITCODE -ne 0) { throw 'PUBLISH_FAILED' }
$executable = Join-Path $macos 'AiUsageMonitor.App.Mac'
$helper = Join-Path $macos 'ai-usage-process-supervisor'
foreach ($binary in @($executable, $helper)) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'BINARY_MISSING' }
    & /usr/bin/lipo -verify_arch arm64 $binary
    if ($LASTEXITCODE -ne 0) { throw 'ARM64_BINARY_REQUIRED' }
    & /bin/chmod +x $binary
    if ($LASTEXITCODE -ne 0) { throw 'EXECUTABLE_MODE_FAILED' }
}
$iconset = Join-Path $destination 'AppIcon.iconset'
New-Item -ItemType Directory -Path $iconset | Out-Null
$sourceIcon = Join-Path $root 'src/AiUsageMonitor.App.WpfLegacy/Assets/ai-usage-monitor.ico'
foreach ($size in @(16, 32, 128, 256, 512)) {
    foreach ($factor in @(1, 2)) {
        $suffix = if ($factor -eq 2) { '@2x' } else { '' }
        $image = Join-Path $iconset ("icon_${size}x${size}${suffix}.png")
        & /usr/bin/sips -s format png -z ($size * $factor) ($size * $factor) $sourceIcon --out $image | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'ICON_CONVERSION_FAILED' }
    }
}
& /usr/bin/iconutil -c icns $iconset -o (Join-Path $resources 'AppIcon.icns')
if ($LASTEXITCODE -ne 0) { throw 'ICNS_FAILED' }
@'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>io.github.ryoichi-r.ai-usage-monitor</string>
<key>CFBundleName</key><string>AI Usage Monitor</string>
<key>CFBundleExecutable</key><string>AiUsageMonitor.App.Mac</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>0.1.0</string>
<key>CFBundleIconFile</key><string>AppIcon.icns</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
'@ | Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -Encoding utf8NoBOM
foreach ($document in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $root $document) -Destination $resources
}
# Sign nested native binaries first, then the enclosing local-only bundle.
foreach ($file in @(Get-ChildItem -LiteralPath $macos -File -Recurse | Sort-Object { if ($_.Extension -eq ".dll") { 0 } else { 1 } })) {
    $description = & /usr/bin/file -b $file.FullName
    if ($file.FullName -ne $executable) {
        & /usr/bin/codesign --force --sign - $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'NESTED_SIGN_FAILED' }
    }
}
& /usr/bin/codesign --force --sign - $app
if ($LASTEXITCODE -ne 0) { throw 'BUNDLE_SIGN_FAILED' }
& /usr/bin/codesign --verify --deep --strict $app
if ($LASTEXITCODE -ne 0) { throw 'SIGNATURE_VERIFY_FAILED' }
$files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($app, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ schema_version = '1.0'; runtime = 'osx-arm64'; signature = 'ad-hoc'; acceptance = 'NOT_ACCEPTED'; files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'bundle-manifest.json') -Encoding utf8NoBOM
Write-Host "Local bundle verified: $app"
