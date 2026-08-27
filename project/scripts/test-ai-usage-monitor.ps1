[CmdletBinding()]
param(
    [switch]$Coverage,
    [ValidateRange(0, 100)]
    [double]$Threshold = 90,
    [string]$OutputRoot,
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$resolver = Join-Path $PSScriptRoot 'resolve-ai-usage-monitor-build-paths.ps1'
. $resolver
# Dot-sourcing the resolver leaks its Set-StrictMode into this scope for the rest of the
# script's execution. The Cobertura aggregation below relies on plain PowerShell null-property
# semantics (an absent XML element reading back as $null rather than throwing), so restore the
# non-strict default explicitly instead of changing that unrelated logic.
Set-StrictMode -Off

# Test/build/coverage output must never land inside the public release candidate tree: a
# normal test run would otherwise change the tree's identity on every invocation. When
# -OutputRoot is omitted this falls back to the OS temporary directory, never to a path
# inside the project.
$requestedOutputParent = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    [IO.Path]::GetTempPath()
} elseif ([IO.Path]::IsPathFullyQualified($OutputRoot.Trim())) {
    $OutputRoot.Trim()
} else {
    Join-Path $projectRoot $OutputRoot.Trim()
}
$resolvedOutputParent = Assert-AiUsageMonitorTestOutputParent -Path $requestedOutputParent -ProjectRoot $projectRoot
$purpose = if ($Coverage) { 'coverage' } else { 'test' }
$testOutput = New-AiUsageMonitorTestOutputRoot -OutputParent $resolvedOutputParent -Purpose $purpose

$solution = Join-Path $PSScriptRoot '..\AiUsageMonitor.slnx'
$originalArtifactsRootEnv = $env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT
$succeeded = $false

try {
    # The FakeExecutableLocator used by the Codex/Claude test projects reads this
    # environment variable from the dotnet test child process to resolve fake executables
    # under the isolated artifacts root instead of the standard source-relative bin/ layout.
$env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT = $testOutput.ArtifactsPath

    # Pin the SDK artifacts property explicitly for solution-level restore/test.
    # The CLI switch alone is not sufficient on every SDK path, while the
    # SDK-generated per-project bin/obj layout remains collision-free.
    $isolationProperties = @(
        ('-p:ArtifactsPath=' + $testOutput.ArtifactsPath)
    )

    $buildContract = Join-Path $PSScriptRoot 'test-build-contract.ps1'
    & $buildContract -FixtureRoot (Join-Path $testOutput.OutputRoot 'build-contract-fixtures')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $launcherContract = Join-Path $PSScriptRoot 'test-rebuild-launcher-contract.ps1'
    & $launcherContract
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not $Coverage) {
        dotnet restore $solution --nologo --artifacts-path $testOutput.ArtifactsPath @isolationProperties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet test $solution -c Release --artifacts-path $testOutput.ArtifactsPath --results-directory $testOutput.ResultsPath --no-restore @isolationProperties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $succeeded = $true
        return
    }

    $coverageTargets = @(
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Core'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Core.Tests\AiUsageMonitor.Core.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Codex'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Codex.Tests\AiUsageMonitor.Codex.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Claude'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Claude.Tests\AiUsageMonitor.Claude.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Claude.Cli'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Claude.Cli.Tests\AiUsageMonitor.Claude.Cli.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Claude.Windows'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Claude.Windows.Tests\AiUsageMonitor.Claude.Windows.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Platform'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Platform.Tests\AiUsageMonitor.Platform.Tests.csproj'
        },
        [pscustomobject]@{
            Package = 'AiUsageMonitor.Platform.Windows'
            Project = Join-Path $PSScriptRoot '..\tests\AiUsageMonitor.Platform.Windows.Tests\AiUsageMonitor.Platform.Windows.Tests.csproj'
        }
    )

    # 母集団から意図せず外れたproduction assemblyがあると、閾値を満たしていても実際には
    # 未検証のコードが混ざる。src配下の実在プロジェクトとcoverageTargetsを突き合わせ、
    # どちらにも属さないものが現れた時点で失敗させる。除外は理由付きでここに明示する。
    $intentionallyUncoveredPackages = [ordered]@{
        # WPF UI。Phase 2でAvaloniaへ移植し、App.Windows / App.UIとして母集団へ入れる。
        'AiUsageMonitor.App'    = 'WPF UI is verified by App.Tests and manual acceptance until the Phase 2 Avalonia port.'
        # D9の参照方向を先に固定するための器。Phase 1時点で計測対象の実装を持たない。
        'AiUsageMonitor.App.UI' = 'Holds no implementation until the Phase 2 Avalonia port.'
    }
    $productionPackages = @(
        Get-ChildItem -Path (Join-Path $projectRoot 'src') -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj") } |
            ForEach-Object { $_.Name }
    )
    $measuredPackages = @($coverageTargets | ForEach-Object { $_.Package })
    $unaccountedPackages = @(
        $productionPackages | Where-Object {
            $measuredPackages -notcontains $_ -and -not $intentionallyUncoveredPackages.Contains($_)
        }
    )
    if ($unaccountedPackages.Count -gt 0) {
        throw ("Production assemblies are neither measured nor explicitly excluded: " +
            "$($unaccountedPackages -join ', '). Add a coverage target or record the exclusion reason.")
    }
    $staleExclusions = @(
        $intentionallyUncoveredPackages.Keys | Where-Object { $productionPackages -notcontains $_ }
    )
    if ($staleExclusions.Count -gt 0) {
        throw "Coverage exclusions name projects that no longer exist: $($staleExclusions -join ', ')."
    }

    # An isolated artifacts root starts with no restored obj/project.assets.json, unlike the
    # project tree where an earlier ordinary restore is normally already present. Restore once
    # up front so the per-target --no-restore clean/build/test sequence below has something to
    # build from.
    dotnet restore $solution --nologo --artifacts-path $testOutput.ArtifactsPath @isolationProperties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # 同じproduction assemblyを複数test hostから同時にinstrumentすると、Coverletが
    # reportからmodule全体を不定に欠落させる。各packageを所有するtest projectごとに
    # clean buildして単独計測し、依存packageの副次coverageは集計しない。
    foreach ($target in $coverageTargets) {
        $targetResultDirectory = Join-Path $testOutput.ResultsPath $target.Package
        dotnet clean $target.Project -c Release --nologo --verbosity quiet --artifacts-path $testOutput.ArtifactsPath @isolationProperties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build $target.Project -c Release --no-restore --nologo --verbosity minimal --artifacts-path $testOutput.ArtifactsPath @isolationProperties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet test $target.Project -c Release --no-build --no-restore --artifacts-path $testOutput.ArtifactsPath @isolationProperties `
            --collect:'XPlat Code Coverage' `
            --results-directory $targetResultDirectory
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $reports = @(Get-ChildItem -Path $testOutput.ResultsPath -Recurse -Filter 'coverage.cobertura.xml')
    if ($reports.Count -ne $coverageTargets.Count) {
        throw "Expected $($coverageTargets.Count) Cobertura reports but found $($reports.Count)."
    }

    $lineCoverage = @{}
    foreach ($target in $coverageTargets) {
        $targetResultDirectory = Join-Path $testOutput.ResultsPath $target.Package
        $targetReports = @(
            Get-ChildItem -Path $targetResultDirectory -Recurse -Filter 'coverage.cobertura.xml'
        )
        $foundPackage = $false
        foreach ($report in $targetReports) {
            [xml]$coverage = Get-Content -Path $report.FullName -Raw
            foreach ($package in @($coverage.coverage.packages.package)) {
                $packageName = [string]$package.name
                if ($packageName -cne $target.Package) { continue }
                $foundPackage = $true
            foreach ($class in @($package.classes.class)) {
                $sourcePath = ([string]$class.filename).Replace('/', '\')
                if ($sourcePath -match '(^|\\)(obj|bin)\\') {
                    continue
                }

                $packagePrefix = $packageName.TrimEnd('\') + '\'
                if (-not $sourcePath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    # Coverletは実行時にロードされた依存production assembly（Core/Claude等）の
                    # クラスも同じpackage要素の下に列挙することがある。それらは依存先自身の
                    # coverageTargetエントリで計測されるため、ここではこのpackage自身の
                    # productionプロジェクト配下のsourceだけを対象にする。
                    continue
                }
                $sourcePath = $sourcePath.Substring($packagePrefix.Length)

                foreach ($line in @($class.lines.line)) {
                    $key = '{0}|{1}|{2}' -f $packageName, $sourcePath, $line.number
                    $hit = [int]$line.hits -gt 0
                    if (-not $lineCoverage.ContainsKey($key)) {
                        $lineCoverage[$key] = $hit
                    }
                    elseif ($hit) {
                        $lineCoverage[$key] = $true
                    }
                }
            }
            }
        }
        if (-not $foundPackage) {
            throw "Coverage report did not contain target package $($target.Package)."
        }
    }

    $valid = $lineCoverage.Count
    if ($valid -eq 0) { throw 'No AiUsageMonitor source lines were found in coverage reports.' }
    $covered = @($lineCoverage.Values | Where-Object { $_ }).Count
    $percentage = [Math]::Round(($covered * 100.0) / $valid, 2)

    # OS別の計測母集団をmanifestとして残す。どのassemblyを測り、どれをなぜ除外したかが
    # 後から追えないと、閾値だけ見て「検証済み」と誤読されうる。
    $manifestPath = Join-Path $testOutput.ResultsPath 'coverage-manifest.json'
    [pscustomobject]@{
        generatedAtUtc         = [DateTimeOffset]::UtcNow.ToString('o')
        operatingSystem        = if ($IsWindows) { 'Windows' } elseif ($IsMacOS) { 'macOS' } else { 'Other' }
        thresholdPercent       = $Threshold
        coveragePercent        = $percentage
        coveredLines           = $covered
        totalLines             = $valid
        measuredPackages       = @($measuredPackages | Sort-Object)
        intentionallyUncovered = @(
            $intentionallyUncoveredPackages.Keys | Sort-Object | ForEach-Object {
                [pscustomobject]@{ package = $_; reason = $intentionallyUncoveredPackages[$_] }
            }
        )
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    "Coverage scope: $([string]::Join(', ', @($coverageTargets.Package | Sort-Object)))"
    "Coverage: $percentage% ($covered/$valid lines)"
    "Excluded from the measured population: $([string]::Join(', ', @($intentionallyUncoveredPackages.Keys | Sort-Object)))"
    "Reports: $($testOutput.ResultsPath)"
    "Manifest: $manifestPath"
    if ($percentage -lt $Threshold) {
        throw "Coverage $percentage% is below threshold $Threshold%."
    }
    $succeeded = $true
}
finally {
    if ($null -eq $originalArtifactsRootEnv) {
        Remove-Item Env:\AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT -ErrorAction SilentlyContinue
    } else {
        $env:AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT = $originalArtifactsRootEnv
    }
    if ($succeeded -and -not $KeepOutput) {
        Remove-AiUsageMonitorTestOutputRoot -OutputRoot $testOutput.OutputRoot -OutputParent $resolvedOutputParent
    } elseif (-not $succeeded) {
        Write-Host "Test output retained for evidence: $($testOutput.OutputRoot)" -ForegroundColor Yellow
    }
}
