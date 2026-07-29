[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$facadeRoot = Split-Path -Parent $projectRoot
$launcherName = 'ここから開始 - AI Usage Monitorを導入・更新.bat'
$launcherPath = Join-Path $facadeRoot $launcherName
$dispatcherPath = Join-Path $projectRoot 'rebuild-ai-usage-monitor.bat'
$failures = [Collections.Generic.List[string]]::new()

function Assert-Contract {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        $failures.Add($Message)
    }
}

function Assert-BatchSource {
    param([string]$Path, [bool]$IsRouter)

    $source = Get-Content -LiteralPath $Path -Raw
    $leaf = Split-Path -Leaf $Path
    Assert-Contract ($source.Contains('setlocal DisableDelayedExpansion', [StringComparison]::Ordinal)) (
        "$leaf must disable delayed expansion."
    )
    Assert-Contract ($source.Contains('set "SELF=%~dp0"', [StringComparison]::Ordinal)) (
        "$leaf must capture its own directory."
    )
    Assert-Contract ($source.Contains('if "%SELF:~0,2%"=="\\"', [StringComparison]::Ordinal)) (
        "$leaf must reject UNC paths."
    )
    Assert-Contract ($source.Contains('exit /b 4', [StringComparison]::Ordinal)) (
        "$leaf must use exit code 4 for UNC paths."
    )
    Assert-Contract (-not ($source -match '(?im)^\s*echo[^\r\n]*%~?f?1')) (
        "$leaf must not echo a raw user path."
    )
    if ($IsRouter) {
        Assert-Contract (-not $source.Contains('call ', [StringComparison]::OrdinalIgnoreCase)) (
            "$leaf must not use call for batch delegation."
        )
        Assert-Contract (-not $source.Contains('%*', [StringComparison]::Ordinal)) (
            "$leaf must not forward arguments with %*."
        )
        Assert-Contract ($source.Contains('if not "%~2"==""', [StringComparison]::Ordinal)) (
            "$leaf must reject multiple arguments."
        )
    }
}

function Set-AsciiFile {
    param([string]$Path, [string]$Value)
    Set-Content -LiteralPath $Path -Value $Value -Encoding ascii
}

function New-Runner {
    param(
        [string]$Path,
        [string]$FixtureRoot,
        [string]$Architecture,
        [AllowEmptyString()][string]$ArchitectureW6432,
        [ValidateSet('launcher-one', 'launcher-two', 'launcher-none', 'dispatcher-one', 'dispatcher-two')]
        [string]$Mode
    )

    $targetPath = if ($Mode.StartsWith('dispatcher', [StringComparison]::Ordinal)) {
        Join-Path $FixtureRoot 'project\rebuild-ai-usage-monitor.bat'
    }
    else {
        Join-Path $FixtureRoot $launcherName
    }
    $script:currentRunnerTarget = $targetPath
    $target = '"%LAUNCHER_TEST_TARGET%"'
    $invocation = switch ($Mode) {
        'launcher-one' { "$target `"%LAUNCHER_TEST_ARG1%`"" }
        'launcher-two' { "$target `"%LAUNCHER_TEST_ARG1%`" `"%LAUNCHER_TEST_ARG2%`"" }
        'launcher-none' { $target }
        'dispatcher-one' { "$target `"%LAUNCHER_TEST_ARG1%`"" }
        'dispatcher-two' { "$target `"%LAUNCHER_TEST_ARG1%`" `"%LAUNCHER_TEST_ARG2%`"" }
    }
    $runner = @"
@echo off
setlocal DisableDelayedExpansion
set "PROCESSOR_ARCHITEW6432=$ArchitectureW6432"
set "PROCESSOR_ARCHITECTURE=$Architecture"
$invocation
"@
    Set-AsciiFile -Path $Path -Value $runner
}

function Invoke-Runner {
    param([string]$Path, [string[]]$Arguments = @())

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $env:ComSpec
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $startInfo.Environment["LAUNCHER_TEST_ARG$($index + 1)"] = $Arguments[$index]
    }
    $startInfo.Environment['LAUNCHER_TEST_TARGET'] = $script:currentRunnerTarget
    $startInfo.Arguments = '/d /s /c ""' + $Path + '""'
    $process = [Diagnostics.Process]::Start($startInfo)
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Read-RouteToken {
    param([string]$FixtureProject)
    $path = Join-Path $FixtureProject 'route-token.txt'
    if (-not (Test-Path -LiteralPath $path)) {
        return ''
    }
    return (Get-Content -LiteralPath $path -Raw -Encoding ascii).Trim()
}

function Clear-RouteToken {
    param([string]$FixtureProject)
    $path = Join-Path $FixtureProject 'route-token.txt'
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

Assert-Contract (Test-Path -LiteralPath $launcherPath -PathType Leaf) 'The facade launcher is missing.'
Assert-Contract (Test-Path -LiteralPath $dispatcherPath -PathType Leaf) 'The architecture dispatcher is missing.'
Assert-BatchSource -Path $launcherPath -IsRouter $true
Assert-BatchSource -Path $dispatcherPath -IsRouter $true
Assert-BatchSource -Path (Join-Path $projectRoot 'rebuild-ai-usage-monitor-x64.bat') -IsRouter $false
Assert-BatchSource -Path (Join-Path $projectRoot 'rebuild-ai-usage-monitor-arm64.bat') -IsRouter $false

$launcherSource = Get-Content -LiteralPath $launcherPath -Raw
$dispatcherSource = Get-Content -LiteralPath $dispatcherPath -Raw
Assert-Contract (
    $launcherSource.Contains('"%~dp0project\rebuild-ai-usage-monitor.bat"', [StringComparison]::Ordinal)
) 'The launcher must delegate relative to %~dp0.'
Assert-Contract (-not ($launcherSource -match '(?i)\b[A-Z]:\\|\\Users\\|\\drive\\coding\\')) (
    'The launcher must not contain a machine-specific absolute path.'
)
foreach ($architecture in 'x64', 'arm64') {
    $target = ('"%~dp0rebuild-ai-usage-monitor-{0}.bat"' -f $architecture)
    Assert-Contract ($dispatcherSource.Contains($target, [StringComparison]::Ordinal)) (
        "The dispatcher must resolve the $architecture target relative to %~dp0."
    )
    Assert-Contract (
        $dispatcherSource.Contains($target + "`r`nexit /b 1", [StringComparison]::Ordinal) -or
        $dispatcherSource.Contains($target + "`nexit /b 1", [StringComparison]::Ordinal)
    ) "The $architecture delegation must have an unreachable failure guard."
}
Assert-Contract ($dispatcherSource.Contains('PROCESSOR_ARCHITEW6432', [StringComparison]::Ordinal)) (
    'The dispatcher must prefer PROCESSOR_ARCHITEW6432.'
)
Assert-Contract ($dispatcherSource.Contains('PROCESSOR_ARCHITECTURE', [StringComparison]::Ordinal)) (
    'The dispatcher must fall back to PROCESSOR_ARCHITECTURE.'
)
Assert-Contract ($dispatcherSource.Contains('if /i "%NATIVE_ARCH%"=="AMD64" goto :x64', [StringComparison]::Ordinal)) (
    'AMD64 must route to x64.'
)
Assert-Contract ($dispatcherSource.Contains('if /i "%NATIVE_ARCH%"=="ARM64" goto :arm64', [StringComparison]::Ordinal)) (
    'ARM64 must route to ARM64.'
)

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
$testResultsRoot = Join-Path $workspaceRoot 'TestResults'
$ownedRoot = Join-Path $testResultsRoot ('launcher-portability-' + [Guid]::NewGuid().ToString('N'))
$metacharacterSegment = 'space 日本語 & caret^ percent%marker bang!'
$fixtureRoot = Join-Path $ownedRoot $metacharacterSegment
$safeWorkspaceRoot = $workspaceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)
$safeOwnedRoot = [IO.Path]::GetFullPath($ownedRoot)
if (-not $safeOwnedRoot.StartsWith(
        $safeWorkspaceRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Launcher fixture escaped the workspace root.'
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $fixtureProject = Join-Path $fixtureRoot 'project'
    [IO.Directory]::CreateDirectory($fixtureProject) | Out-Null
    Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $fixtureRoot $launcherName)
    Copy-Item -LiteralPath $dispatcherPath -Destination (Join-Path $fixtureProject 'rebuild-ai-usage-monitor.bat')

    $x64Stub = @'
@echo off
setlocal DisableDelayedExpansion
cd /d "%~dp0"
if not "%~1"=="" if not exist "%~f1\marker-name" exit /b 9
> "%~dp0route-token.txt" echo PASS_X64
if exist "%~dp0exit-2" exit /b 2
if exist "%~dp0exit-3" exit /b 3
if exist "%~dp0exit-17" exit /b 17
exit /b 0
'@
    $arm64Stub = @'
@echo off
setlocal DisableDelayedExpansion
cd /d "%~dp0"
if not "%~1"=="" if not exist "%~f1\marker-name" exit /b 9
> "%~dp0route-token.txt" echo PASS_ARM64
if exist "%~dp0exit-2" exit /b 2
if exist "%~dp0exit-3" exit /b 3
if exist "%~dp0exit-17" exit /b 17
exit /b 0
'@
    $x64StubPath = Join-Path $fixtureProject 'rebuild-ai-usage-monitor-x64.bat'
    $arm64StubPath = Join-Path $fixtureProject 'rebuild-ai-usage-monitor-arm64.bat'
    Set-AsciiFile -Path $x64StubPath -Value $x64Stub
    Set-AsciiFile -Path $arm64StubPath -Value $arm64Stub

    $argumentPath = Join-Path $fixtureProject 'output space 日本語 & caret^ percent%arg bang!'
    [IO.Directory]::CreateDirectory($argumentPath) | Out-Null
    Set-AsciiFile -Path (Join-Path $argumentPath 'marker-name') -Value 'marker'
    $relativeArgument = [IO.Path]::GetRelativePath($fixtureProject, $argumentPath)

    # Keep the runner itself in the ASCII-only owned root. The launcher,
    # dispatcher, stubs, and argument remain under the metacharacter fixture.
    $runner = Join-Path $ownedRoot 'case-runner.bat'

    # Cases 1 and 5: fallback AMD64 route and full metacharacter path propagation.
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-one
    $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
    Assert-Contract ($result.ExitCode -eq 0) (
        "AMD64 route failed (exit $($result.ExitCode)); stdout=$($result.StdOut.Trim()); stderr=$($result.StdErr.Trim())"
    )
    Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq 'PASS_X64') (
        'AMD64 must call only the x64 stub.'
    )

    # Case 2: PROCESSOR_ARCHITEW6432 takes precedence.
    Clear-RouteToken -FixtureProject $fixtureProject
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'x86' -ArchitectureW6432 'ARM64' -Mode launcher-one
    $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
    Assert-Contract ($result.ExitCode -eq 0) "W6432 ARM64 route failed (exit $($result.ExitCode))."
    Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq 'PASS_ARM64') (
        'PROCESSOR_ARCHITEW6432=ARM64 must call only the ARM64 stub.'
    )

    # Case 3: fallback ARM64 route.
    Clear-RouteToken -FixtureProject $fixtureProject
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'ARM64' -ArchitectureW6432 '' -Mode launcher-one
    $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
    Assert-Contract ($result.ExitCode -eq 0) "ARM64 fallback route failed (exit $($result.ExitCode))."
    Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq 'PASS_ARM64') (
        'PROCESSOR_ARCHITECTURE=ARM64 must call only the ARM64 stub.'
    )

    # Case 4: unknown architecture must fail before a target runs.
    Clear-RouteToken -FixtureProject $fixtureProject
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'UNKNOWN' -ArchitectureW6432 '' -Mode launcher-none
    $result = Invoke-Runner -Path $runner
    Assert-Contract ($result.ExitCode -ne 0) 'Unknown architecture must return non-zero.'
    Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq '') (
        'Unknown architecture must not run an architecture stub.'
    )

    # Case 6: both router layers independently reject multiple arguments.
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-two
    $result = Invoke-Runner -Path $runner -Arguments @($argumentPath, $argumentPath)
    Assert-Contract ($result.ExitCode -eq 2) 'The facade launcher must reject two arguments with exit 2.'
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode dispatcher-two
    $result = Invoke-Runner -Path $runner -Arguments @($argumentPath, $argumentPath)
    Assert-Contract ($result.ExitCode -eq 2) 'The dispatcher must reject two arguments with exit 2.'

    # Case 7: target exit codes survive both router layers.
    foreach ($exitCode in 2, 3, 17) {
        $exitMarker = Join-Path $fixtureProject "exit-$exitCode"
        Set-AsciiFile -Path $exitMarker -Value 'marker'
        try {
            New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-one
            $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
            Assert-Contract ($result.ExitCode -eq $exitCode) (
                "Target exit $exitCode must propagate through dispatcher and launcher."
            )
        }
        finally {
            Remove-Item -LiteralPath $exitMarker -Force
        }
    }

    # Case 10: missing dispatcher must fail without starting a stub.
    Clear-RouteToken -FixtureProject $fixtureProject
    $fixtureDispatcher = Join-Path $fixtureProject 'rebuild-ai-usage-monitor.bat'
    $savedDispatcher = Join-Path $fixtureProject 'saved-dispatcher.bat'
    Move-Item -LiteralPath $fixtureDispatcher -Destination $savedDispatcher
    try {
        New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-one
        $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
        Assert-Contract ($result.ExitCode -ne 0) 'A missing dispatcher must return non-zero.'
        Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq '') (
            'A missing dispatcher must not run a stub.'
        )
    }
    finally {
        Move-Item -LiteralPath $savedDispatcher -Destination $fixtureDispatcher
    }

    # Case 11: a missing selected target must not fall through to another target.
    Clear-RouteToken -FixtureProject $fixtureProject
    $savedX64 = Join-Path $fixtureProject 'saved-x64.bat'
    Move-Item -LiteralPath $x64StubPath -Destination $savedX64
    try {
        New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-one
        $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
        Assert-Contract ($result.ExitCode -ne 0) 'A missing x64 target must return non-zero.'
        Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq '') (
            'A missing x64 target must not fall through to ARM64.'
        )
    }
    finally {
        Move-Item -LiteralPath $savedX64 -Destination $x64StubPath
    }
    Clear-RouteToken -FixtureProject $fixtureProject
    $savedArm64 = Join-Path $fixtureProject 'saved-arm64.bat'
    Move-Item -LiteralPath $arm64StubPath -Destination $savedArm64
    try {
        New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'x86' -ArchitectureW6432 'ARM64' -Mode launcher-one
        $result = Invoke-Runner -Path $runner -Arguments @($argumentPath)
        Assert-Contract ($result.ExitCode -ne 0) 'A missing ARM64 target must return non-zero.'
        Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq '') (
            'A missing ARM64 target must not fall through to x64.'
        )
    }
    finally {
        Move-Item -LiteralPath $savedArm64 -Destination $arm64StubPath
    }

    # Case 15: relative arguments resolve from the architecture batch project directory.
    Clear-RouteToken -FixtureProject $fixtureProject
    New-Runner -Path $runner -FixtureRoot $fixtureRoot -Architecture 'AMD64' -ArchitectureW6432 '' -Mode launcher-one
    $result = Invoke-Runner -Path $runner -Arguments @($relativeArgument)
    Assert-Contract ($result.ExitCode -eq 0) 'Relative output path resolution failed.'
    Assert-Contract ((Read-RouteToken -FixtureProject $fixtureProject) -eq 'PASS_X64') (
        'Relative path case must reach the x64 target.'
    )
}
finally {
    if ((Split-Path -Leaf $safeOwnedRoot) -like 'launcher-portability-*' -and
        (Test-Path -LiteralPath $safeOwnedRoot)) {
        Remove-Item -LiteralPath $safeOwnedRoot -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Rebuild launcher contract checks passed.' -ForegroundColor Green
exit 0
