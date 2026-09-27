#requires -Version 7.4
[CmdletBinding()]
param([switch]$Coverage, [ValidateRange(0, 100)][double]$Threshold = 90, [string]$OutputRoot, [switch]$KeepOutput)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'MACOS_REQUIRED' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { [IO.Path]::GetTempPath() }
if ($parent -eq $root -or $parent.StartsWith($root + '/', [StringComparison]::Ordinal)) { throw 'TEST_OUTPUT_MUST_BE_OUTSIDE_PROJECT' }
$output = Join-Path $parent ('AiUsageMonitorMacTests-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $output 'artifacts'
$results = Join-Path $output 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$solution = Join-Path $root 'AiUsageMonitor.Mac.slnx'
$isolation = '-p:ArtifactsPath=' + $artifacts
$previous = $env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT
try {
    $env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT = $artifacts
    & (Join-Path $PSScriptRoot 'test-build-contract.ps1') -FixtureRoot (Join-Path $output 'fixtures')
    if ($LASTEXITCODE -ne 0) { throw 'BUILD_CONTRACT_FAILED' }
    & dotnet restore $solution --nologo --artifacts-path $artifacts $isolation
    if ($LASTEXITCODE -ne 0) { throw 'RESTORE_FAILED' }
    & dotnet build $solution -c Release --no-restore --nologo --artifacts-path $artifacts $isolation
    if ($LASTEXITCODE -ne 0) { throw 'BUILD_FAILED' }
    if (-not $Coverage) {
        & dotnet test $solution -c Release --no-build --no-restore --artifacts-path $artifacts $isolation --results-directory $results --nologo
        if ($LASTEXITCODE -ne 0) { throw 'TEST_FAILED' }
    } else {
        # Sequential collectors avoid simultaneous instrumentation of shared assemblies.
        [xml]$sln = Get-Content -LiteralPath $solution -Raw
        foreach ($node in $sln.SelectNodes('//Project')) {
            $relative = [string]$node.Path
            if ($relative -notlike 'tests/*.Tests/*.csproj') { continue }
            $name = [IO.Path]::GetFileNameWithoutExtension($relative)
            & dotnet test (Join-Path $root $relative) -c Release --no-build --no-restore --artifacts-path $artifacts $isolation `
                --collect:'XPlat Code Coverage' --results-directory (Join-Path $results $name) --nologo
            if ($LASTEXITCODE -ne 0) { throw "TEST_FAILED: $name" }
        }
    }
    $native = Join-Path $output 'ai-usage-process-supervisor'
    $nativeSource = Join-Path $root 'src/AiUsageMonitor.Platform.Mac/Native/process-supervisor.c'
    $flags = @('-framework', 'Security', '-framework', 'CoreFoundation', '-std=gnu17', '-Wall', '-Wextra', '-Werror')
    if ($Coverage) { $flags += @('-fprofile-instr-generate', '-fcoverage-mapping', '-fprofile-update=atomic') }
    & clang @flags $nativeSource -o $native
    if ($LASTEXITCODE -ne 0) { throw 'NATIVE_BUILD_FAILED' }
    $testNative = Join-Path $output 'test-claude-supervisor'
    & clang @flags -DAIUSAGE_TEST_SIGNING $nativeSource -o $testNative
    if ($LASTEXITCODE -ne 0) { throw 'SIGNED_FIXTURE_BUILD_FAILED' }
    $socketSource = Join-Path $root 'src/AiUsageMonitor.Platform.Mac/Native/local-socket.c'
    $socketLibrary = Join-Path $output 'libaiusage-local-socket.dylib'
    & clang @flags -dynamiclib $socketSource -o $socketLibrary
    if ($LASTEXITCODE -ne 0) { throw 'SOCKET_NATIVE_BUILD_FAILED' }
    $previousProfile = $env:LLVM_PROFILE_FILE
    try {
        if ($Coverage) { $env:LLVM_PROFILE_FILE = Join-Path $output 'native-%p.profraw' }
        & python3 (Join-Path $PSScriptRoot 'test-macos-supervisor.py') --helper $native
        if ($LASTEXITCODE -ne 0) { throw 'NATIVE_ACCEPTANCE_FAILED' }
        & python3 (Join-Path $PSScriptRoot 'test-macos-supervisor.py') --helper $native --pty
        if ($LASTEXITCODE -ne 0) { throw 'PTY_ACCEPTANCE_FAILED' }
        & python3 (Join-Path $PSScriptRoot 'test-macos-claude-native.py') --production $native --test-helper $testNative --library $socketLibrary --fake-source (Join-Path $root 'tests/support/fake-claude-macos.c')
        if ($LASTEXITCODE -ne 0) { throw 'CLAUDE_NATIVE_ACCEPTANCE_FAILED' }
    } finally { $env:LLVM_PROFILE_FILE = $previousProfile }
    if ($Coverage) {
        $profile = Join-Path $output 'native.profdata'
        $raw = @(Get-ChildItem -LiteralPath $output -Filter '*.profraw' | ForEach-Object FullName)
        & xcrun llvm-profdata merge -sparse @raw -o $profile
        if ($LASTEXITCODE -ne 0) { throw 'NATIVE_PROFILE_FAILED' }
        & xcrun llvm-cov export $native -object $testNative -object $socketLibrary ('-instr-profile=' + $profile) $nativeSource $socketSource |
            Set-Content -LiteralPath (Join-Path $results 'native-coverage.json') -Encoding utf8NoBOM
        if ($LASTEXITCODE -ne 0) { throw 'NATIVE_COVERAGE_FAILED' }
        & python3 (Join-Path $PSScriptRoot 'summarize-macos-coverage.py') --project $root --results $results --threshold $Threshold
        if ($LASTEXITCODE -ne 0) { throw "COVERAGE_GATE_FAILED: $results" }
    }
    Write-Host "Mac validation passed. Results: $results"
} finally {
    $env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT = $previous
    # Retain receipts and failed-run artifacts for diagnosis; no implicit deletion of evidence.
    Write-Host "Validation artifacts: $output"
}
