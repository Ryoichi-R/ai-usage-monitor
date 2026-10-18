#requires -Version 7.4
[CmdletBinding()]
param([string]$FixtureRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = if ($FixtureRoot) { [IO.Path]::GetFullPath($FixtureRoot) } else {
    Join-Path ([IO.Path]::GetTempPath()) ('AiUsageVersionInfo-' + [Guid]::NewGuid().ToString('N'))
}
if ($fixture -eq $root -or $fixture.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'VERSION_FIXTURE_MUST_BE_OUTSIDE_PROJECT'
}
$project = Join-Path $fixture 'project'
$tools = Join-Path $fixture 'tools'
New-Item -ItemType Directory -Path $project, $tools -Force | Out-Null
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $project
}
$csproj = Join-Path $project 'AiUsageMonitor.Core.csproj'
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' |
    Set-Content -LiteralPath $csproj -Encoding utf8NoBOM
[xml]$props = Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw
$expectedVersion = [string]$props.Project.PropertyGroup.Version
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$previousPath = $env:PATH
$count = 0

function Assert-VersionContract {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "VERSION_CONTRACT_FAILED: $Message" }
}

function Invoke-Snapshot {
    param([string]$Runtime, [switch]$NoBuild)
    $start = [DateTimeOffset]::UtcNow
    $arguments = @('msbuild', $csproj, '-t:GenerateProductBuildInfo', '-nologo', '-nodeReuse:false', '-p:Configuration=Release', "-p:RuntimeIdentifier=$Runtime")
    if ($NoBuild) { $arguments += '-p:NoBuild=true' }
    & $dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'SNAPSHOT_BUILD_FAILED' }
    $jsonPath = Join-Path $project "obj/Release/net10.0/$Runtime/build-info.json"
    $json = Get-Content -LiteralPath $jsonPath -Raw
    $info = $json | ConvertFrom-Json
    Assert-VersionContract ($info.Version -ceq $expectedVersion) 'Product version must be independent of the runtime and Git state.'
    Assert-VersionContract ($info.AppName -ceq 'AI Usage Monitor') 'Product name mismatch.'
    # PowerShell 7.4 keeps JSON dates as strings; newer versions can return DateTime.
    $utc = [DateTimeOffset]$info.BuildTimeUtc
    Assert-VersionContract ($utc.Offset -eq [TimeSpan]::Zero) 'Timestamp must be UTC.'
    if (-not $NoBuild) { Assert-VersionContract ($utc -ge $start -and $utc -le [DateTimeOffset]::UtcNow) 'Timestamp must be generated during the build.' }
    Assert-VersionContract (-not $json.Contains($fixture, [StringComparison]::Ordinal)) 'Snapshot must not include machine paths.'
    Assert-VersionContract (@($info.PSObject.Properties).Count -eq 5) 'Snapshot must contain only the five allowed build fields.'
    return $info
}

try {
    # A source export without a repository must build successfully and report unknown.
    $export = Invoke-Snapshot 'osx-arm64'
    Assert-VersionContract ($export.SourceIdentifier -ceq '不明' -and $null -eq $export.HasUncommittedChanges) 'Source export must report unknown.'
    $count++
    $unchanged = Invoke-Snapshot 'osx-arm64' -NoBuild
    Assert-VersionContract ($unchanged.BuildTimeUtc -eq $export.BuildTimeUtc) 'publish --no-build must preserve the embedded timestamp.'
    $count++
    $rebuilt = Invoke-Snapshot 'osx-arm64'
    Assert-VersionContract ($rebuilt.Version -ceq $export.Version -and $rebuilt.BuildTimeUtc -gt $export.BuildTimeUtc) 'Rebuild must update only the build timestamp.'
    $count++
    # Fake Git exercises status outcomes without creating commits or touching the real repository.
    foreach ($state in @('Clean', 'Dirty', 'Unavailable', 'UnknownSource', 'UnknownStatus')) {
        $sourceExit = if ($state -in @('Unavailable', 'UnknownSource')) { 127 } else { 0 }
        $statusExit = if ($state -in @('Unavailable', 'UnknownStatus')) { 127 } else { 0 }
        $dirtyOutput = if ($state -eq 'Dirty') { " M file-with-apostrophe's.cs" } else { '' }
        $shim = Join-Path $tools $(if ($IsWindows) { 'git.cmd' } else { 'git' })
        $body = if ($IsWindows) {
@'
@echo off
if "%~3"=="rev-parse" (
  echo 123456789abc
  exit /b __SOURCE_EXIT__
)
if "%~3"=="status" (
  __DIRTY_OUTPUT__
  exit /b __STATUS_EXIT__
)
exit /b 1
'@
        } else {
@'
#!/bin/sh
if [ "$3" = 'rev-parse' ]; then
  echo 123456789abc
  exit __SOURCE_EXIT__
fi
if [ "$3" = 'status' ]; then
  __DIRTY_OUTPUT__
  exit __STATUS_EXIT__
fi
exit 1
'@
        }
        $outputCommand = if ($dirtyOutput) {
            if ($IsWindows) { 'echo ' + $dirtyOutput } else { 'printf "%s\n" "' + $dirtyOutput + '"' }
        } else { if ($IsWindows) { 'rem empty status' } else { ':' } }
        $body.Replace('__SOURCE_EXIT__', [string]$sourceExit).Replace('__STATUS_EXIT__', [string]$statusExit).Replace('__DIRTY_OUTPUT__', $outputCommand) |
            Set-Content -LiteralPath $shim -Encoding utf8NoBOM
        if (-not $IsWindows) {
            & chmod +x $shim
            if ($LASTEXITCODE -ne 0) { throw 'SHIM_MODE_FAILED' }
        }
        $env:PATH = $tools + [IO.Path]::PathSeparator + $previousPath
        foreach ($runtime in @('win-x64', 'win-arm64', 'osx-arm64')) {
            $info = Invoke-Snapshot $runtime
            $expectedSource = if ($sourceExit -eq 0) { '123456789abc' } else { '不明' }
            Assert-VersionContract ($info.SourceIdentifier -ceq $expectedSource) "Source mismatch: $state / $runtime"
            $expectedDirty = if ($sourceExit -ne 0 -or $statusExit -ne 0) { $null } else { $state -eq 'Dirty' }
            Assert-VersionContract ($info.HasUncommittedChanges -eq $expectedDirty) "Dirty state mismatch: $state / $runtime"
            $count++
        }
    }
    Write-Host "Version information build contract passed: $count snapshots."
} finally {
    $env:PATH = $previousPath
    Write-Host "Version contract fixtures retained: $fixture"
}
