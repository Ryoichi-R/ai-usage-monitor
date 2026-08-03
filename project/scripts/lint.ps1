#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Fast', 'Full')][string]$Mode = 'Fast',
    [string]$ProjectPath,
    [string]$ReceiptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$formatArgs = @('-NoProfile', '-File', (Join-Path $projectRoot 'scripts\format.ps1'), '-Check')
if ($ProjectPath) { $formatArgs += @('-ProjectPath', $ProjectPath) }
& pwsh @formatArgs
$formatExit = $LASTEXITCODE
$buildExit = 0
if ($formatExit -eq 0 -and $Mode -eq 'Full') {
    & dotnet build (Join-Path $projectRoot 'AiUsageMonitor.slnx') -c Release --no-restore --warnaserror
    $buildExit = $LASTEXITCODE
}
$exitCode = if ($formatExit -ne 0) { $formatExit } else { $buildExit }
$receipt = [ordered]@{
    schema_version = '1.0'
    project = 'ai-usage-monitor'
    mode = $Mode
    executed_at = [DateTimeOffset]::Now.ToString('o')
    formatter = [ordered]@{ status = if ($formatExit -eq 0) { 'PASS' } else { 'FAILED' }; exit_code = [int]$formatExit }
    build = [ordered]@{ status = if ($Mode -eq 'Fast') { 'NOT_RUN' } elseif ($buildExit -eq 0) { 'PASS' } else { 'FAILED' }; exit_code = [int]$buildExit }
    status = if ($exitCode -eq 0) { 'PASS' } else { 'FAILED' }
}
$target = if ($ReceiptPath) { [IO.Path]::GetFullPath($ReceiptPath) } else { Join-Path $projectRoot 'artifacts\quality\lint-receipt.json' }
$targetParent = Split-Path -Parent $target
New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $target -Encoding utf8NoBOM
exit $exitCode
