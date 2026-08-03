#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Write,
    [string]$ProjectPath,
    [string]$ReceiptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$solution = Join-Path $projectRoot 'AiUsageMonitor.slnx'
if ($ProjectPath) {
    $candidate = [IO.Path]::GetFullPath($ProjectPath)
    if (-not $candidate.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not [StringComparer]::OrdinalIgnoreCase.Equals($candidate, $projectRoot)) {
        throw 'FORMAT_PATH_OUTSIDE_PROJECT'
    }
}
if ($Check -and $Write) { throw 'FORMAT_CHECK_WRITE_CONFLICT' }
$verify = $Check -or -not $Write
$arguments = @('format', $solution, '--no-restore', '--verbosity', 'minimal')
if ($verify) { $arguments += '--verify-no-changes' }
$started = [DateTimeOffset]::Now
& dotnet @arguments
$exitCode = $LASTEXITCODE
$receipt = [ordered]@{
    schema_version = '1.0'
    project = 'ai-usage-monitor'
    solution = $solution
    mode = if ($verify) { 'check' } else { 'write' }
    executed_at = $started.ToString('o')
    exit_code = [int]$exitCode
    status = if ($exitCode -eq 0) { 'PASS' } else { 'FAILED' }
}
$target = if ($ReceiptPath) { [IO.Path]::GetFullPath($ReceiptPath) } else { Join-Path $projectRoot 'artifacts\quality\format-receipt.json' }
$targetParent = Split-Path -Parent $target
New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $target -Encoding utf8NoBOM
exit $exitCode
