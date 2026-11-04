#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][string]$BuildArtifactsRoot
)
$ErrorActionPreference = 'Stop'
if ($Runtime.StartsWith('osx-', [StringComparison]::Ordinal) -and -not $IsMacOS) { throw 'MACOS_REQUIRED' }
if ($Runtime.StartsWith('win-', [StringComparison]::Ordinal) -and -not $IsWindows) { throw 'WINDOWS_REQUIRED' }
$destination = [IO.Path]::GetFullPath($OutputDir)
if ((Split-Path -Leaf $destination) -ne 'work-status-bridge') { throw 'WORK_STATUS_BRIDGE_OUTPUT_REQUIRED' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'src/AiUsageMonitor.WorkStatus.Bridge/AiUsageMonitor.WorkStatus.Bridge.csproj'
& dotnet publish $project -c Release -r $Runtime --self-contained true -o $destination --artifacts-path $BuildArtifactsRoot -p:UseSharedCompilation=false -p:BuildInParallel=false -m:1 -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'WORK_STATUS_BRIDGE_PUBLISH_FAILED' }
$name = if ($IsWindows) { 'ai-usage-work-status-bridge.exe' } else { 'ai-usage-work-status-bridge' }
if (-not (Test-Path -LiteralPath (Join-Path $destination $name) -PathType Leaf)) { throw 'WORK_STATUS_BRIDGE_MISSING' }
