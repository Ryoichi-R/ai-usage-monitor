[CmdletBinding()]
param(
    [string]$FixtureRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$resolver = Join-Path $PSScriptRoot 'resolve-ai-usage-monitor-build-paths.ps1'
. $resolver

$failures = [Collections.Generic.List[string]]::new()

function Assert-Contract {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        $failures.Add($Message)
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Message
    )

    try {
        & $Action
        $failures.Add($Message)
    }
    catch {
    }
}

# Fixtures for this contract test are pure path-resolution probes; they must never be
# written inside the project tree, or a normal test run would itself destabilize the
# public release candidate identity. Callers that isolate output (test-ai-usage-monitor.ps1)
# pass an explicit -FixtureRoot under their own owned temporary root; direct/standalone
# invocation falls back to a fresh OS-temp child that this script owns and removes itself.
$resolvedFixtureParent = if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    Join-Path ([IO.Path]::GetTempPath()) ('ai-usage-monitor-build-contract-' + [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($FixtureRoot)
}
$normalizedProjectRoot = $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($resolvedFixtureParent -ieq $normalizedProjectRoot -or
    $resolvedFixtureParent.StartsWith($normalizedProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "FixtureRoot must be outside the project tree, not inside it: $resolvedFixtureParent"
}
$null = New-Item -ItemType Directory -Path $resolvedFixtureParent -Force

$testRoot = Join-Path $resolvedFixtureParent 'build-contract'
$externalParent = Join-Path $testRoot 'external-parent'
$filePath = Join-Path $testRoot 'not-a-directory.txt'
$facadeRoot = [IO.Path]::GetFullPath((Split-Path -Parent $projectRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
# The facade-child rejection probe reuses the release/ directory, which already exists as a
# permanent sibling of project/ under the facade root. This avoids creating (and needing to
# clean up) a synthetic facade-tree directory that would linger as candidate residue if this
# script were interrupted before its finally block ran.
$facadeChildCandidate = Join-Path $facadeRoot 'release'

try {
    New-Item -ItemType Directory -Path $externalParent -Force | Out-Null
    New-Item -ItemType File -Path $filePath -Force | Out-Null

    $default = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $null
    $expectedArtifacts = ConvertTo-CanonicalDirectoryPath -Path (Join-Path $projectRoot 'artifacts')
    Assert-Contract (-not $default.UsesExternalRoot) 'Default build paths must use the project root.'
    Assert-Contract ($default.ManagedRoot -ieq $expectedArtifacts) "Default managed root must be artifacts: $($default.ManagedRoot)"

    $explicitProject = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $projectRoot
    Assert-Contract (-not $explicitProject.UsesExternalRoot) 'Explicit project root must not be external.'
    Assert-Contract ($explicitProject.ManagedRoot -ieq $expectedArtifacts) 'Explicit project root must use artifacts.'

    $external = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $externalParent
    $expectedExternal = ConvertTo-CanonicalDirectoryPath -Path (Join-Path $externalParent 'AiUsageMonitorBuilds')
    Assert-Contract $external.UsesExternalRoot 'External parent must be marked external.'
    Assert-Contract ($external.ManagedRoot -ieq $expectedExternal) "External managed root mismatch: $($external.ManagedRoot)"

    $validatedParent = Assert-AiUsageMonitorOutputParent -Path $externalParent
    Assert-Contract ($validatedParent -ieq (ConvertTo-CanonicalDirectoryPath -Path $externalParent)) 'Existing directory validation changed the path.'
    Assert-Throws { Assert-AiUsageMonitorOutputParent -Path $filePath } 'A file must be rejected as OutputRoot.'
    Assert-Throws { Assert-AiUsageMonitorOutputParent -Path (Join-Path $testRoot 'missing') } 'A missing OutputRoot must be rejected.'
    $managedLeafParent = Join-Path $testRoot 'AiUsageMonitorBuilds'
    New-Item -ItemType Directory -Path $managedLeafParent -Force | Out-Null
    Assert-Throws {
        Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $managedLeafParent
    } 'AiUsageMonitorBuilds itself must be rejected as OutputRoot.'

    $relativeParent = [IO.Path]::GetRelativePath($projectRoot, $externalParent)
    $relative = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $relativeParent
    Assert-Contract ($relative.ManagedRoot -ieq $expectedExternal) 'Relative OutputRoot must resolve from the project root.'

    $filesystemRoot = [IO.Path]::GetPathRoot($projectRoot)
    $rootPaths = Resolve-AiUsageMonitorBuildPaths -ProjectRoot $projectRoot -OutputRoot $filesystemRoot
    $expectedRootManaged = ConvertTo-CanonicalDirectoryPath -Path (Join-Path $filesystemRoot 'AiUsageMonitorBuilds')
    Assert-Contract ($rootPaths.ManagedRoot -ieq $expectedRootManaged) 'Filesystem root must use AiUsageMonitorBuilds.'

    $x64Batch = Get-Content -LiteralPath (Join-Path $projectRoot 'rebuild-ai-usage-monitor-x64.bat') -Raw
    $arm64Batch = Get-Content -LiteralPath (Join-Path $projectRoot 'rebuild-ai-usage-monitor-arm64.bat') -Raw
    Assert-Contract ($x64Batch.Contains('if not "%~2"==""', [StringComparison]::Ordinal)) 'x64 batch must reject multiple arguments.'
    Assert-Contract ($arm64Batch.Contains('if not "%~2"==""', [StringComparison]::Ordinal)) 'ARM64 batch must reject multiple arguments.'
    Assert-Contract ($x64Batch.Contains('-RevealOutput', [StringComparison]::Ordinal)) 'x64 batch must reveal successful output.'
    Assert-Contract ($arm64Batch.Contains('-RevealOutput', [StringComparison]::Ordinal)) 'ARM64 batch must reveal successful output.'

    $publish = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publish-ai-usage-monitor.ps1') -Raw
    Assert-Contract ($publish.Contains('Join-Path $defaultManagedRoot $Runtime', [StringComparison]::Ordinal)) 'Raw publish must retain artifacts/<runtime> as its default.'

    $directoryBuildProps = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
    $treatWarningsAsErrors = @(
        $directoryBuildProps.SelectNodes('//TreatWarningsAsErrors') |
            ForEach-Object { $_.InnerText.Trim() }
    )
    $warningsNotAsErrors = @(
        $directoryBuildProps.SelectNodes('//WarningsNotAsErrors') |
            ForEach-Object { $_.InnerText -split ';' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ }
    )
    $nugetAuditValues = @(
        $directoryBuildProps.SelectNodes('//NuGetAudit') |
            ForEach-Object { $_.InnerText.Trim() }
    )
    Assert-Contract ($treatWarningsAsErrors -contains 'true') 'Builds must continue treating warnings as errors.'
    Assert-Contract ($warningsNotAsErrors -contains 'NU1900') 'NuGet audit-source transport failures must remain warnings.'
    foreach ($vulnerabilityCode in 'NU1901', 'NU1902', 'NU1903', 'NU1904') {
        Assert-Contract (
            $warningsNotAsErrors -notcontains $vulnerabilityCode
        ) "Known-vulnerability warning $vulnerabilityCode must remain an error."
    }
    $disabledNuGetAudit = @(
        $nugetAuditValues | Where-Object { $_ -ieq 'false' }
    )
    Assert-Contract (
        $disabledNuGetAudit.Count -eq 0
    ) 'NuGet Audit must not be disabled to handle audit-source outages.'

    $rebuild = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rebuild-ai-usage-monitor.ps1') -Raw
    $testRunner = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-ai-usage-monitor.ps1') -Raw
    Assert-Contract ($rebuild.Contains('$dialog.ShowNewFolderButton = $false', [StringComparison]::Ordinal)) 'Folder selection must not create facade children.'
    Assert-Contract ($rebuild.Contains('[switch]$RevealOutput', [StringComparison]::Ordinal)) 'Rebuild must expose the RevealOutput switch.'
    Assert-Contract ($rebuild.Contains('Invoke-Item -LiteralPath $outputDir -ErrorAction Stop', [StringComparison]::Ordinal)) 'RevealOutput must use literal output path dispatch.'
    Assert-Contract ($rebuild.Contains('$insideFacade -and -not $insideProject', [StringComparison]::Ordinal)) 'Rebuild must reject facade paths outside project.'
    Assert-Contract (-not $rebuild.Contains('Remove-Item -LiteralPath $managedRoot', [StringComparison]::Ordinal)) 'Rebuild must never remove the managed root.'
    Assert-Contract (-not $rebuild.Contains('Remove-Item -LiteralPath $resolvedOutputRoot', [StringComparison]::Ordinal)) 'Rebuild must never remove the output parent.'
    Assert-Contract ($rebuild.Contains('[IO.Path]::GetTempPath()', [StringComparison]::Ordinal)) 'Rebuild staging must use the OS temporary directory.'
    Assert-Contract ($rebuild.Contains("'--artifacts-path', `$buildArtifacts", [StringComparison]::Ordinal)) 'Restore, clean, and build must use temporary artifacts paths.'
    Assert-Contract ($testRunner.Contains('ArtifactsPath', [StringComparison]::Ordinal)) 'Isolated tests must pin the SDK artifacts path outside the source tree.'
    Assert-Contract ($testRunner.Contains('--no-restore @isolationProperties', [StringComparison]::Ordinal)) 'Normal isolated tests must not perform an implicit source-tree restore.'
    Assert-Contract ($rebuild.Contains('-BuildArtifactsRoot $buildArtifacts', [StringComparison]::Ordinal)) 'Publish must use the temporary artifacts root.'
    Assert-Contract ($rebuild.Contains('[Environment+SpecialFolder]::LocalApplicationData', [StringComparison]::Ordinal)) 'Rollback backup must be outside the synchronized managed root.'
    Assert-Contract ($rebuild.Contains('Sync-DirectoryContents', [StringComparison]::Ordinal)) 'Promotion must skip unchanged synchronized files.'
    Assert-Contract (-not $rebuild.Contains('Move-Item -LiteralPath $outputDir', [StringComparison]::Ordinal)) 'Rebuild must not rename the current output directory.'
    Assert-Contract (-not $rebuild.Contains('Move-Item -LiteralPath $stagingDir', [StringComparison]::Ordinal)) 'Rebuild must not rename the staging directory into the synchronized output root.'
    Assert-Contract ($rebuild.Contains('if ($backupPrepared)', [StringComparison]::Ordinal)) 'Rollback must require a backup prepared by the current rebuild.'

    # Runtime validation must fail before restore/build/publish for both the
    # facade itself and an existing facade child outside project.
    $rebuildScript = Join-Path $PSScriptRoot 'rebuild-ai-usage-monitor.ps1'
    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    foreach ($facadeCandidate in $facadeRoot, $facadeChildCandidate) {
        & $pwshPath -NoLogo -NoProfile -File $rebuildScript `
            -Runtime win-x64 `
            -OutputRoot $facadeCandidate *> $null
        Assert-Contract ($LASTEXITCODE -ne 0) (
            "Facade OutputRoot must fail at runtime: $facadeCandidate"
        )
    }
    Assert-Contract (-not (Test-Path -LiteralPath (Join-Path $facadeRoot 'AiUsageMonitorBuilds'))) (
        'Facade runtime rejection must not create AiUsageMonitorBuilds.'
    )

    $appSourceRoot = Join-Path $projectRoot 'src\AiUsageMonitor.App'
    $coordinatorConstructions = @(
        Get-ChildItem -LiteralPath $appSourceRoot -File -Filter '*.cs' |
            Select-String -Pattern 'new\s+ClaudeUsageSourceCoordinator\s*\('
    )
    Assert-Contract ($coordinatorConstructions.Count -eq 1) 'App must construct ClaudeUsageSourceCoordinator in exactly one source location.'
    if ($coordinatorConstructions.Count -eq 1) {
        Assert-Contract (
            (Split-Path -Leaf $coordinatorConstructions[0].Path) -eq 'ClaudeUsageRuntime.cs'
        ) 'App must construct ClaudeUsageSourceCoordinator only in ClaudeUsageRuntime.cs.'
    }
    $runtimeSource = Get-Content -LiteralPath (Join-Path $appSourceRoot 'ClaudeUsageRuntime.cs') -Raw
    Assert-Contract (
        $runtimeSource -match 'ClaudeUsageStateStore\s+_state\s*=\s*new\s*\(\s*\)'
    ) 'ClaudeUsageRuntime must own the Claude active/passive state store.'
    Assert-Contract (
        $runtimeSource -match '_state\.CommitActive\s*\(' -and
        $runtimeSource -match '_state\.CommitPassive\s*\('
    ) 'ClaudeUsageRuntime must route active and passive observations to separate channels.'
    Assert-Contract (
        $runtimeSource -notmatch 'ClaudeUsageSourceCoordinator\s*\(\s*source\s*,\s*_'
    ) 'ClaudeUsageSourceCoordinator must not mutate the runtime-owned channel state.'
    $coordinatorSource = Get-Content -LiteralPath (
        Join-Path $projectRoot 'src\AiUsageMonitor.Claude\Acquisition\ClaudeUsageSourceCoordinator.cs'
    ) -Raw
    Assert-Contract (
        $coordinatorSource -notmatch 'ClaudeUsageObservationMerger|ObservePassive|SnapshotChanged'
    ) 'ClaudeUsageSourceCoordinator must expose only the production active outcome path.'
    Assert-Contract (
        $coordinatorSource -match 'Task<ClaudeActiveRefreshOutcome>\s+RefreshAsync'
    ) 'ClaudeUsageSourceCoordinator must return a side-effect-free active outcome.'
    $activeMeasureScript = Join-Path $projectRoot 'scripts\measure-claude-active-source.ps1'
    Assert-Contract (Test-Path -LiteralPath $activeMeasureScript -PathType Leaf) (
        'The AC-26 active source measurement script is required.'
    )
    $activeMeasureSource = Get-Content -LiteralPath $activeMeasureScript -Raw
    Assert-Contract (
        $activeMeasureSource -match 'OwnedProcessesAfter10Seconds' -and
        $activeMeasureSource -match 'MaxConcurrentClaude' -and
        $activeMeasureSource -match 'WallSeconds\s+-ge\s+45'
    ) 'AC-26 must gate process ownership, concurrency, and the 45-second maximum.'

    $sourceRoot = Join-Path $projectRoot 'src'
    $productionSources = @(
        Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    )
    $codexClientConstructions = @(
        $productionSources | Select-String -Pattern 'new\s+CodexUsageClient\s*\('
    )
    Assert-Contract ($codexClientConstructions.Count -eq 1) 'Production code must construct CodexUsageClient in exactly one source location.'
    if ($codexClientConstructions.Count -eq 1) {
        Assert-Contract (
            (Split-Path -Leaf $codexClientConstructions[0].Path) -eq 'CodexAccountsCoordinator.cs'
        ) 'Only CodexAccountsCoordinator may construct CodexUsageClient.'
    }

    $appCodexClientReferences = @(
        Get-ChildItem -LiteralPath $appSourceRoot -Recurse -File -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Select-String -Pattern '\bCodexUsageClient\b'
    )
    Assert-Contract ($appCodexClientReferences.Count -eq 0) 'App UI code must not directly reference CodexUsageClient.'

    $usageSnapshotSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Core\Usage\UsageSnapshot.cs') -Raw
    $usageViewModelSource = Get-Content -LiteralPath (Join-Path $appSourceRoot 'UsageViewModel.cs') -Raw
    $publicIdentityPattern = '(?im)^\s*public\s+.*\b(e-?mail|accountmail|usermail|mailaddress)\b'
    Assert-Contract (
        $usageSnapshotSource -notmatch $publicIdentityPattern
    ) 'Public UsageSnapshot members must not expose account email.'
    Assert-Contract (
        $usageViewModelSource -notmatch $publicIdentityPattern
    ) 'Public widget view-model members must not expose account email.'

    $codexClientSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Codex\Client\CodexUsageClient.cs') -Raw
    Assert-Contract (
        $codexClientSource -notmatch 'public\s+CodexUsageClient\s*\(\s*\)'
    ) 'CodexUsageClient must not expose a parameterless constructor.'
    Assert-Contract (
        $codexClientSource -notmatch 'ReadAsync\s*\(\s*string\??\s+'
    ) 'CodexUsageClient must not accept a mutable CODEX_HOME argument at read time.'

    $codexProcessSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Codex\Process\CodexAppServerProcess.cs') -Raw
    $appRuntimeSource = Get-Content -LiteralPath (Join-Path $appSourceRoot 'App.xaml.cs') -Raw
    Assert-Contract (
        $codexProcessSource.Contains('lifetimeGuardFactory?.Invoke(_process)', [StringComparison]::Ordinal)
    ) 'Codex app-server process must attach the configured lifetime guard.'
    Assert-Contract (
        $appRuntimeSource.Contains('ProcessJobObject.Attach', [StringComparison]::Ordinal)
    ) 'App must attach Codex app-server children to the shared kill-on-close Job Object.'
    Assert-Contract (
        $appRuntimeSource.Contains('_codexCoordinator.RunPeriodicPollingAsync', [StringComparison]::Ordinal)
    ) 'App must delegate periodic Codex polling to the account coordinator.'
    Assert-Contract (
        $appRuntimeSource.Contains('refreshCodex: false', [StringComparison]::Ordinal)
    ) 'The Claude polling loop must not trigger a shared Codex polling cycle.'
    $codexCoordinatorSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Codex\Runtime\CodexAccountsCoordinator.cs') -Raw
    Assert-Contract (
        $codexCoordinatorSource.Contains('_pollingJitterFactory()', [StringComparison]::Ordinal)
    ) 'Codex periodic polling must sample jitter independently for each account schedule.'

    $hiddenConsoleSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Claude.Windows\Console\HiddenConsoleSession.cs') -Raw
    $legacyJobSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'AiUsageMonitor.Claude.Windows\Process\ProcessJobObject.cs') -Raw
    Assert-Contract (
        $hiddenConsoleSource.Contains('AiUsageMonitor.Platform.Windows.Process', [StringComparison]::Ordinal)
    ) 'Claude hidden console must use the shared Windows process lifetime implementation.'
    Assert-Contract (
        $legacyJobSource -notmatch '\bDllImport\b|\bLibraryImport\b'
    ) 'The Claude compatibility wrapper must not retain duplicate native Job Object bindings.'

    # macOS向けsolutionはnet10.0-windows TFMのプロジェクトを含んではならない。含まれていると
    # macOSでのrestoreがそのプロジェクトの時点で失敗する（Phase 7で本格運用するまでの回帰止め）。
    $macSlnxPath = Join-Path $projectRoot 'AiUsageMonitor.Mac.slnx'
    Assert-Contract (Test-Path -LiteralPath $macSlnxPath) 'AiUsageMonitor.Mac.slnx must exist to scope macOS restore/build.'
    if (Test-Path -LiteralPath $macSlnxPath) {
        [xml]$macSlnxXml = Get-Content -LiteralPath $macSlnxPath -Raw
        $macProjectPaths = @($macSlnxXml.SelectNodes('//Project') | ForEach-Object { [string]$_.Path })
        Assert-Contract ($macProjectPaths.Count -gt 0) 'AiUsageMonitor.Mac.slnx must list at least one project.'
        $windowsOnlyMacProjects = foreach ($relativePath in $macProjectPaths) {
            $csprojPath = Join-Path $projectRoot $relativePath
            if (-not (Test-Path -LiteralPath $csprojPath)) {
                $relativePath
                continue
            }
            $csprojContent = Get-Content -LiteralPath $csprojPath -Raw
            if ($csprojContent -match '<TargetFramework>[^<]*-windows') {
                $relativePath
            }
        }
        Assert-Contract (
            @($windowsOnlyMacProjects).Count -eq 0
        ) "AiUsageMonitor.Mac.slnx must not reference Windows-only or missing projects: $($windowsOnlyMacProjects -join ', ')"
    }

    # D9: UIコードはApp.UIへ一本化し、OS別thin hostだけが対応するPlatform / Claude実装を
    # 合成する。App.UIがOS固有プロジェクトを参照した時点でこの合成ルートは崩れ、macOSでの
    # restoreも通らなくなるため、参照方向をここで機械的に固定する。
    $appUiProjectPath = Join-Path $sourceRoot 'AiUsageMonitor.App.UI\AiUsageMonitor.App.UI.csproj'
    Assert-Contract (
        Test-Path -LiteralPath $appUiProjectPath
    ) 'AiUsageMonitor.App.UI must exist to hold the shared UI composition root.'
    if (Test-Path -LiteralPath $appUiProjectPath) {
        [xml]$appUiXml = Get-Content -LiteralPath $appUiProjectPath -Raw
        $appUiTargetFramework = [string]($appUiXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1)
        Assert-Contract (
            $appUiTargetFramework -ceq 'net10.0'
        ) "AiUsageMonitor.App.UI must target net10.0 so macOS can build it, but targets '$appUiTargetFramework'."

        $forbiddenAppUiReferences = @(
            'AiUsageMonitor.Platform.Windows',
            'AiUsageMonitor.Claude.Windows',
            'AiUsageMonitor.Platform.Mac',
            'AiUsageMonitor.Claude.Mac',
            'AiUsageMonitor.App'
        )
        $appUiReferenceNames = @(
            $appUiXml.SelectNodes('//ProjectReference') |
                ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.Include) }
        )
        $violations = @($appUiReferenceNames | Where-Object { $forbiddenAppUiReferences -ccontains $_ })
        Assert-Contract (
            $violations.Count -eq 0
        ) "AiUsageMonitor.App.UI must not reference OS-specific or host projects: $($violations -join ', ')"
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedFixtureParent) {
        Remove-Item -LiteralPath $resolvedFixtureParent -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Build contract checks passed.' -ForegroundColor Green
exit 0
