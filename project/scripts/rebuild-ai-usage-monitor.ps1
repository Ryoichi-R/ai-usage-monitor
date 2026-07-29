<#
.SYNOPSIS
    AI Usage MonitorをRID別にclean、build、publishし、安全に入れ替える。

.PARAMETER Runtime
    出力対象のWindows RID。win-arm64またはwin-x64。

.PARAMETER OutputRoot
    出力を格納する既存の親ディレクトリ。相対pathはproject root基準。
    project rootの場合はartifactsを使用し、それ以外では直下に
    AiUsageMonitorBuildsを作成する。

.PARAMETER SelectOutputRoot
    OutputRootが省略された場合、Windowsのfolder選択dialogを表示する。

.PARAMETER RevealOutput
    成功後に出力フォルダーをExplorerで開く。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime,
    [string]$OutputRoot,
    [switch]$SelectOutputRoot,
    [switch]$RevealOutput
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
        throw "Refusing to operate outside the allowed root: $pathFull"
    }
    return $pathFull
}

function Assert-ManagedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ExpectedLeaf
    )

    $pathFull = Assert-PathWithinRoot -Path $Path -Root $Root
    if ((Split-Path -Leaf $pathFull) -cne $ExpectedLeaf) {
        throw "Refusing to manage an unexpected directory: $pathFull"
    }
    if (Test-Path -LiteralPath $pathFull) {
        if (-not (Test-Path -LiteralPath $pathFull -PathType Container)) {
            throw "Managed path exists but is not a directory: $pathFull"
        }
        if ((Get-Item -LiteralPath $pathFull -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Managed directory cannot be a symbolic link or junction: $pathFull"
        }
    }
    return $pathFull
}

function Clear-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$AllowedRoot
    )

    $pathFull = Assert-PathWithinRoot -Path $Path -Root $AllowedRoot
    New-Item -ItemType Directory -Path $pathFull -Force | Out-Null
    Get-ChildItem -LiteralPath $pathFull -Force | ForEach-Object {
        $child = Assert-PathWithinRoot -Path $_.FullName -Root $pathFull
        Remove-Item -LiteralPath $child -Recurse -Force
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $sourceFull = Assert-PathWithinRoot -Path $Source -Root $SourceRoot
    $destinationFull = Assert-PathWithinRoot -Path $Destination -Root $DestinationRoot
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        throw "Copy source is not a directory: $sourceFull"
    }
    $reparsePoint = Get-ChildItem -LiteralPath $sourceFull -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "Copy source cannot contain a symbolic link or junction: $($reparsePoint.FullName)"
    }
    New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceFull -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destinationFull -Recurse -Force
    }
}

function Sync-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $sourceFull = Assert-PathWithinRoot -Path $Source -Root $SourceRoot
    $destinationFull = Assert-PathWithinRoot -Path $Destination -Root $DestinationRoot
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        throw "Sync source is not a directory: $sourceFull"
    }
    $sourceItems = @(Get-ChildItem -LiteralPath $sourceFull -Recurse -Force)
    $destinationItems = if (Test-Path -LiteralPath $destinationFull -PathType Container) {
        @(Get-ChildItem -LiteralPath $destinationFull -Recurse -Force)
    }
    else {
        @()
    }
    $reparsePoint = @($sourceItems + $destinationItems) |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "Synchronized directories cannot contain a symbolic link or junction: $($reparsePoint.FullName)"
    }

    $sourceRelativePaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceItem in $sourceItems) {
        [void]$sourceRelativePaths.Add(
            [IO.Path]::GetRelativePath($sourceFull, $sourceItem.FullName))
    }

    foreach ($destinationItem in $destinationItems |
        Sort-Object { $_.FullName.Length } -Descending) {
        $relativePath = [IO.Path]::GetRelativePath($destinationFull, $destinationItem.FullName)
        if (-not $sourceRelativePaths.Contains($relativePath)) {
            $safeItem = Assert-PathWithinRoot -Path $destinationItem.FullName -Root $destinationFull
            Remove-Item -LiteralPath $safeItem -Recurse -Force
        }
    }

    New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
    foreach ($sourceDirectory in $sourceItems |
        Where-Object { $_.PSIsContainer } |
        Sort-Object FullName) {
        $relativePath = [IO.Path]::GetRelativePath($sourceFull, $sourceDirectory.FullName)
        $destinationDirectory = Assert-PathWithinRoot `
            -Path (Join-Path $destinationFull $relativePath) `
            -Root $destinationFull
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    foreach ($sourceFile in $sourceItems | Where-Object { -not $_.PSIsContainer }) {
        $relativePath = [IO.Path]::GetRelativePath($sourceFull, $sourceFile.FullName)
        $destinationFile = Assert-PathWithinRoot `
            -Path (Join-Path $destinationFull $relativePath) `
            -Root $destinationFull
        $unchanged = (Test-Path -LiteralPath $destinationFile -PathType Leaf) -and
            (Get-Item -LiteralPath $destinationFile).Length -eq $sourceFile.Length -and
            (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        if (-not $unchanged) {
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
        }
    }
}

function Get-DirectoryDigest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$AllowedRoot
    )

    $pathFull = Assert-PathWithinRoot -Path $Path -Root $AllowedRoot
    $lines = foreach ($file in Get-ChildItem -LiteralPath $pathFull -File -Recurse -Force |
        Sort-Object FullName) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Digest source cannot contain a symbolic link: $($file.FullName)"
        }
        $relativePath = [IO.Path]::GetRelativePath($pathFull, $file.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$relativePath`t$($file.Length)`t$hash"
    }
    $manifest = $lines -join "`n"
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($manifest)))
}

function Select-OutputRootFolder {
    param([Parameter(Mandatory)][string]$InitialDirectory)

    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
        throw 'フォルダー選択画面を表示するには、PowerShellを-STAオプション付きで起動してください。'
    }

    Add-Type -AssemblyName System.Windows.Forms
    $dialog = [Windows.Forms.FolderBrowserDialog]::new()
    try {
        $dialog.Description = '既存の親フォルダーを選択してください。projectを選ぶとartifactsを使用し、外部フォルダーではAiUsageMonitorBuildsを作成します。AI Usage Monitorの外側またはproject内を選択してください。'
        $dialog.UseDescriptionForTitle = $false
        $dialog.AutoUpgradeEnabled = $true
        $dialog.ShowNewFolderButton = $false
        $dialog.SelectedPath = $InitialDirectory
        $result = $dialog.ShowDialog()
        if ($result -ne [Windows.Forms.DialogResult]::OK -or
            [string]::IsNullOrWhiteSpace($dialog.SelectedPath)) {
            Write-Host '保存先の選択がキャンセルされました。ファイルは生成していません。' -ForegroundColor Yellow
            exit 3
        }
        return $dialog.SelectedPath
    }
    finally {
        $dialog.Dispose()
    }
}

function Invoke-DotNetStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "==> $Name" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset + 6 -gt $stream.Length) {
            throw "Invalid PE header offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature: $Path"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$buildPathsScript = Assert-PathWithinRoot -Path (Join-Path $PSScriptRoot 'resolve-ai-usage-monitor-build-paths.ps1') -Root $projectRoot
. $buildPathsScript

if ($SelectOutputRoot -and [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Select-OutputRootFolder -InitialDirectory $projectRoot
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $projectRoot
}

$resolvedOutputRoot = Assert-AiUsageMonitorOutputParent -Path $(
    if ([IO.Path]::IsPathFullyQualified($OutputRoot)) {
        $OutputRoot
    }
    else {
        Join-Path $projectRoot $OutputRoot
    })
$facadeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $projectRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$insideFacade = $resolvedOutputRoot -ieq $facadeRoot -or
    $resolvedOutputRoot.StartsWith(
        $facadeRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
$insideProject = $resolvedOutputRoot -ieq $projectRoot -or
    $resolvedOutputRoot.StartsWith(
        $projectRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
if ($insideFacade -and -not $insideProject) {
    throw 'The selected parent folder must be project itself, a folder inside project, or an existing folder outside the AI Usage Monitor facade.'
}
$buildPaths = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $resolvedOutputRoot
if ($buildPaths.UsesExternalRoot -and (Split-Path -Leaf $resolvedOutputRoot) -ieq 'AiUsageMonitorBuilds') {
    throw "Specify the parent folder in which AiUsageMonitorBuilds will be created, not AiUsageMonitorBuilds itself: $resolvedOutputRoot"
}

$appProject = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'src/AiUsageMonitor.App/AiUsageMonitor.App.csproj') -Root $projectRoot
$projectArtifactsRoot = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'artifacts') -Root $projectRoot
if ($resolvedOutputRoot -ine $projectRoot -and
    ($resolvedOutputRoot -ieq $projectArtifactsRoot -or
        $resolvedOutputRoot.StartsWith(
            $projectArtifactsRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase))) {
    throw "The selected parent folder cannot be inside the project artifacts directory: $resolvedOutputRoot"
}

$managedRootLeaf = if ($buildPaths.UsesExternalRoot) { 'AiUsageMonitorBuilds' } else { 'artifacts' }
$managedRoot = Assert-ManagedDirectory -Path $buildPaths.ManagedRoot -Root $resolvedOutputRoot -ExpectedLeaf $managedRootLeaf
$outputName = "ai-usage-monitor-$Runtime"
$outputDir = Assert-ManagedDirectory -Path (Join-Path $managedRoot $outputName) -Root $managedRoot -ExpectedLeaf $outputName
$mutexHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($projectRoot.ToUpperInvariant()))).Substring(0, 16)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$stagingParentName = "AiUsageMonitorStaging-$mutexHash"
$stagingParent = Assert-ManagedDirectory -Path (Join-Path $temporaryRoot $stagingParentName) -Root $temporaryRoot -ExpectedLeaf $stagingParentName
$stagingManagedRoot = Assert-ManagedDirectory -Path (Join-Path $stagingParent 'AiUsageMonitorBuilds') -Root $stagingParent -ExpectedLeaf 'AiUsageMonitorBuilds'
$buildArtifacts = Assert-PathWithinRoot -Path (Join-Path $stagingManagedRoot '.build') -Root $stagingManagedRoot
$stagingName = ".staging-ai-usage-monitor-$Runtime-$([guid]::NewGuid().ToString('N'))"
$stagingDir = Assert-ManagedDirectory -Path (Join-Path $stagingManagedRoot $stagingName) -Root $stagingManagedRoot -ExpectedLeaf $stagingName
$backupName = "_backup-ai-usage-monitor-$Runtime"
$localAppDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$backupRootName = 'AiUsageMonitorBackups'
$backupRoot = Assert-ManagedDirectory -Path (Join-Path $localAppDataRoot $backupRootName) -Root $localAppDataRoot -ExpectedLeaf $backupRootName
$backupDir = Assert-ManagedDirectory -Path (Join-Path $backupRoot $backupName) -Root $backupRoot -ExpectedLeaf $backupName
$publishScript = Assert-PathWithinRoot -Path (Join-Path $projectRoot 'scripts/publish-ai-usage-monitor.ps1') -Root $projectRoot
$updateMutex = [Threading.Mutex]::new($false, "Local\AiUsageMonitor-Rebuild-$mutexHash")
$updateLockTaken = $false
$published = $false
$backupPrepared = $false

try {
    try {
        $updateLockTaken = $updateMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $updateLockTaken = $true
    }
    if (-not $updateLockTaken) {
        throw 'Another AI Usage Monitor rebuild is already running. Wait for it to finish, then try again.'
    }

    New-Item -ItemType Directory -Path $managedRoot -Force | Out-Null

    Write-Host "Output parent: $resolvedOutputRoot" -ForegroundColor Cyan
    Write-Host "Managed output: $managedRoot" -ForegroundColor Cyan
    Write-Host "The managed $Runtime directory will be replaced only after a successful publish." -ForegroundColor Yellow

    $runningTarget = @(Get-Process -Name 'AiUsageMonitor.App' -ErrorAction SilentlyContinue | Where-Object {
            $processPath = $_.Path
            $processPath -and [IO.Path]::GetFullPath($processPath).StartsWith(
                $outputDir + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
        })
    if ($runningTarget.Count -gt 0) {
        throw "$Runtime build is running from the update target. Exit it from the task tray, then run this rebuild again."
    }

    Invoke-DotNetStep -Name "Restore ($Runtime)" -Arguments @(
        'restore', $appProject,
        '--runtime', $Runtime,
        '--artifacts-path', $buildArtifacts,
        '--nologo'
    )
    Invoke-DotNetStep -Name "Clean Release ($Runtime)" -Arguments @(
        'clean', $appProject,
        '--configuration', 'Release',
        '--runtime', $Runtime,
        '--artifacts-path', $buildArtifacts,
        '--verbosity', 'normal',
        '--nologo'
    )
    Invoke-DotNetStep -Name "Build Release ($Runtime)" -Arguments @(
        'build', $appProject,
        '--configuration', 'Release',
        '--runtime', $Runtime,
        '--artifacts-path', $buildArtifacts,
        '--no-restore',
        '--nologo'
    )

    Write-Host "==> Publish $Runtime" -ForegroundColor Cyan
    & $publishScript `
        -Runtime $Runtime `
        -OutputDir $stagingDir `
        -ManagedRoot $stagingManagedRoot `
        -BuildArtifactsRoot $buildArtifacts
    if ($LASTEXITCODE -ne 0) {
        throw "Publish $Runtime failed with exit code $LASTEXITCODE"
    }

    $stagedExe = Assert-PathWithinRoot -Path (Join-Path $stagingDir 'AiUsageMonitor.App.exe') -Root $stagingDir
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "Published executable was not found: $stagedExe"
    }
    $stagedBridge = Assert-PathWithinRoot -Path (Join-Path $stagingDir 'claude-statusline-bridge.ps1') -Root $stagingDir
    if (-not (Test-Path -LiteralPath $stagedBridge -PathType Leaf)) {
        throw "Published Claude bridge was not found: $stagedBridge"
    }
    $expectedMachine = if ($Runtime -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
    $actualMachine = Get-PeMachine -Path $stagedExe
    if ($actualMachine -ne $expectedMachine) {
        throw ('Published executable architecture mismatch. Expected 0x{0:X4}, actual 0x{1:X4}.' -f $expectedMachine, $actualMachine)
    }

    $stagingDigest = Get-DirectoryDigest -Path $stagingDir -AllowedRoot $stagingManagedRoot
    if (Test-Path -LiteralPath $outputDir) {
        Clear-DirectoryContents -Path $backupDir -AllowedRoot $backupRoot
        Copy-DirectoryContents `
            -Source $outputDir `
            -Destination $backupDir `
            -SourceRoot $managedRoot `
            -DestinationRoot $backupRoot
        if ((Get-DirectoryDigest -Path $backupDir -AllowedRoot $backupRoot) -ne
            (Get-DirectoryDigest -Path $outputDir -AllowedRoot $managedRoot)) {
            throw 'Backup verification failed before updating the current build.'
        }
        $backupPrepared = $true
    }

    try {
        Sync-DirectoryContents `
            -Source $stagingDir `
            -Destination $outputDir `
            -SourceRoot $stagingManagedRoot `
            -DestinationRoot $managedRoot
        if ((Get-DirectoryDigest -Path $outputDir -AllowedRoot $managedRoot) -ne $stagingDigest) {
            throw 'Published output verification failed after copying the staged build.'
        }
        $published = $true
    }
    catch {
        if ($backupPrepared) {
            Sync-DirectoryContents `
                -Source $backupDir `
                -Destination $outputDir `
                -SourceRoot $backupRoot `
                -DestinationRoot $managedRoot
        }
        throw
    }

    Write-Host "Updated $Runtime build: $outputDir" -ForegroundColor Green
    Write-Host "Executable: $(Join-Path $outputDir 'AiUsageMonitor.App.exe')" -ForegroundColor Green
    Write-Host '通知領域にAI Usage Monitorが残っている場合は終了し、開いたフォルダー内の AiUsageMonitor.App.exe を実行してください。' -ForegroundColor Cyan
    if ($RevealOutput) {
        try {
            Invoke-Item -LiteralPath $outputDir -ErrorAction Stop
        }
        catch {
            Write-Warning "出力フォルダーを自動で開けませんでした。手動で開いてください: $outputDir"
        }
    }
    if (Test-Path -LiteralPath $backupDir) {
        Write-Host "Previous build retained for rollback: $backupDir" -ForegroundColor Yellow
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDir) {
        $safeStaging = Assert-ManagedDirectory -Path $stagingDir -Root $stagingManagedRoot -ExpectedLeaf $stagingName
        Remove-Item -LiteralPath $safeStaging -Recurse -Force
    }
    if (Test-Path -LiteralPath $stagingManagedRoot) {
        $safeStagingRoot = Assert-ManagedDirectory `
            -Path $stagingManagedRoot `
            -Root $stagingParent `
            -ExpectedLeaf 'AiUsageMonitorBuilds'
        Remove-Item -LiteralPath $safeStagingRoot -Recurse -Force
    }
    if ((Test-Path -LiteralPath $stagingParent) -and
        @(Get-ChildItem -LiteralPath $stagingParent -Force).Count -eq 0) {
        Remove-Item -LiteralPath $stagingParent -Force
    }
    if ($updateLockTaken) {
        $updateMutex.ReleaseMutex()
    }
    $updateMutex.Dispose()
}
