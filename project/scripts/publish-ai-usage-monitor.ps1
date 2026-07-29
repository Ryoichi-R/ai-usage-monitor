[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$ManagedRoot,
    [string]$BuildArtifactsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $pathFull = [IO.Path]::GetFullPath($Path)
    if ($pathFull -ne $rootFull -and
        -not $pathFull.StartsWith(
            $rootFull + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to publish outside the allowed managed root: $pathFull"
    }
    return $pathFull
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$project = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'src/AiUsageMonitor.App/AiUsageMonitor.App.csproj') -Root $projectRoot
$distributionDocuments = @(
    [pscustomobject]@{
        Name = 'LICENSE'
        Source = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'LICENSE') -Root $projectRoot
    }
    [pscustomobject]@{
        Name = 'THIRD-PARTY-NOTICES.md'
        Source = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Root $projectRoot
    }
)
foreach ($document in $distributionDocuments) {
    if (-not (Test-Path -LiteralPath $document.Source -PathType Leaf)) {
        throw "Distribution document missing: $($document.Source)"
    }
}
$defaultManagedRoot = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'artifacts') -Root $projectRoot
$resolvedManagedRoot = if ([string]::IsNullOrWhiteSpace($ManagedRoot)) {
    $defaultManagedRoot
}
else {
    [IO.Path]::GetFullPath(
        $(if ([IO.Path]::IsPathFullyQualified($ManagedRoot)) {
                $ManagedRoot
            }
            else {
                Join-Path $projectRoot $ManagedRoot
            }))
}

$managedLeaf = Split-Path -Leaf $resolvedManagedRoot
if ($managedLeaf -ine 'artifacts' -and $managedLeaf -ine 'AiUsageMonitorBuilds') {
    throw "ManagedRoot must be named artifacts or AiUsageMonitorBuilds: $resolvedManagedRoot"
}
if (Test-Path -LiteralPath $resolvedManagedRoot) {
    if (-not (Test-Path -LiteralPath $resolvedManagedRoot -PathType Container)) {
        throw "ManagedRoot exists but is not a directory: $resolvedManagedRoot"
    }
    if ((Get-Item -LiteralPath $resolvedManagedRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "ManagedRoot cannot be a symbolic link or junction: $resolvedManagedRoot"
    }
}

$requestedOutput = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $defaultManagedRoot $Runtime
}
elseif ([IO.Path]::IsPathFullyQualified($OutputDir)) {
    $OutputDir
}
else {
    Join-Path $projectRoot $OutputDir
}
$output = Assert-PathWithinRoot -Path $requestedOutput -Root $resolvedManagedRoot
if ($output -eq $resolvedManagedRoot) {
    throw 'OutputDir must be a child directory of ManagedRoot.'
}
$outputLeaf = Split-Path -Leaf $output
$allowedLeaf = $outputLeaf -ceq $Runtime -or
    $outputLeaf -ceq "ai-usage-monitor-$Runtime" -or
    $outputLeaf -cmatch "^\.staging-ai-usage-monitor-$([regex]::Escape($Runtime))-[0-9a-f]{32}$"
if (-not $allowedLeaf) {
    throw "OutputDir has an unexpected leaf name: $output"
}
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) {
        throw "OutputDir exists but is not a directory: $output"
    }
    if ((Get-Item -LiteralPath $output -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "OutputDir cannot be a symbolic link or junction: $output"
    }
}

$buildArtifacts = if ([string]::IsNullOrWhiteSpace($BuildArtifactsRoot)) {
    Assert-PathWithinRoot -Path (Join-Path $defaultManagedRoot ".build\$Runtime") -Root $defaultManagedRoot
}
elseif ([IO.Path]::IsPathFullyQualified($BuildArtifactsRoot)) {
    Assert-PathWithinRoot -Path $BuildArtifactsRoot -Root $resolvedManagedRoot
}
else {
    Assert-PathWithinRoot -Path (Join-Path $resolvedManagedRoot $BuildArtifactsRoot) -Root $resolvedManagedRoot
}
New-Item -ItemType Directory -Path $resolvedManagedRoot -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    --artifacts-path $buildArtifacts `
    --nologo `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $output 'AiUsageMonitor.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Executable missing from publish output: $executable"
}
$bridge = Join-Path $output 'claude-statusline-bridge.ps1'
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
    throw "Bridge missing from publish output: $bridge"
}

foreach ($document in $distributionDocuments) {
    $destination = Assert-PathWithinRoot -Path (Join-Path $output $document.Name) -Root $output
    Copy-Item -LiteralPath $document.Source -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $document.Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "Distribution document copy verification failed: $($document.Name)"
    }
}

Write-Host "Published AI Usage Monitor to $output" -ForegroundColor Green
