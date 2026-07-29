[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int]$Count = 5,

    [ValidateRange(1, 120)]
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $projectRoot 'tests\AiUsageMonitor.Claude.Windows.Tests\AiUsageMonitor.Claude.Windows.Tests.csproj'
$testName = 'AiUsageMonitor.Claude.Windows.Tests.ActualClaudeCliSmokeTests.ApprovedOfficialCliReturnsTwoStrictlyParsedWindows'
$claudePath = [Environment]::GetEnvironmentVariable('CODEX_USAGE_MONITOR_ACTUAL_CLAUDE')
$helperPath = [Environment]::GetEnvironmentVariable('CODEX_USAGE_MONITOR_ACTUAL_HELPER')

if ([string]::IsNullOrWhiteSpace($claudePath) -or -not (Test-Path -LiteralPath $claudePath -PathType Leaf)) {
    throw 'CODEX_USAGE_MONITOR_ACTUAL_CLAUDE must name the approved official Claude executable.'
}
if ([string]::IsNullOrWhiteSpace($helperPath) -or -not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw 'CODEX_USAGE_MONITOR_ACTUAL_HELPER must name the approved AI Usage Monitor helper executable.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $claudePath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Claude executable signature is not valid: $($signature.Status)"
}
$publisher = $signature.SignerCertificate.Subject
if ([string]::IsNullOrWhiteSpace($publisher)) {
    throw 'Claude executable publisher is unavailable.'
}

& dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE."
}

$previousTimeout = [Environment]::GetEnvironmentVariable(
    'CODEX_USAGE_MONITOR_ACTUAL_STARTUP_TIMEOUT_SECONDS',
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'CODEX_USAGE_MONITOR_ACTUAL_STARTUP_TIMEOUT_SECONDS',
    $StartupTimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    [EnvironmentVariableTarget]::Process)

$results = [Collections.Generic.List[object]]::new()
function ConvertTo-UtcCreationTime {
    param([object]$Value)
    if ($Value -is [DateTime]) {
        return ([DateTime]$Value).ToUniversalTime()
    }
    return [System.Management.ManagementDateTimeConverter]::ToDateTime(
        [string]$Value).ToUniversalTime()
}

try {
    for ($iteration = 1; $iteration -le $Count; $iteration++) {
        $startedAt = [DateTimeOffset]::Now
        $arguments = @(
            'test',
            $testProject,
            '-c', 'Release',
            '--no-build',
            '--nologo',
            '--filter', "FullyQualifiedName=$testName"
        )
        $rootProcess = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -WindowStyle Hidden
        $owned = [Collections.Generic.Dictionary[int, DateTime]]::new()
        $ownedNames = [Collections.Generic.Dictionary[int, string]]::new()
        $ownedParents = [Collections.Generic.Dictionary[int, int]]::new()
        $owned[$rootProcess.Id] = $rootProcess.StartTime.ToUniversalTime()
        $ownedNames[$rootProcess.Id] = 'dotnet.exe'
        $ownedParents[$rootProcess.Id] = 0
        $claudeSessions = [Collections.Generic.HashSet[int]]::new()
        $maxConcurrentClaude = 0

        while (-not $rootProcess.HasExited) {
            $processes = @(Get-CimInstance Win32_Process)
            $added = $true
            while ($added) {
                $added = $false
                foreach ($process in $processes) {
                    $pidValue = [int]$process.ProcessId
                    $parentPid = [int]$process.ParentProcessId
                    if ($owned.ContainsKey($pidValue) -or -not $owned.ContainsKey($parentPid)) {
                        continue
                    }
                    $creation = ConvertTo-UtcCreationTime $process.CreationDate
                    if ($creation -lt $startedAt.UtcDateTime.AddSeconds(-1)) {
                        continue
                    }
                    $owned[$pidValue] = $creation
                    $ownedNames[$pidValue] = [string]$process.Name
                    $ownedParents[$pidValue] = $parentPid
                    $added = $true
                    $parentIsClaude = $ownedNames.ContainsKey($parentPid) -and
                        $ownedNames[$parentPid] -ieq 'claude.exe'
                    if ([string]$process.Name -ieq 'claude.exe' -and -not $parentIsClaude) {
                        [void]$claudeSessions.Add($pidValue)
                    }
                }
            }
            $runningClaude = @(
                $processes | Where-Object {
                    $pidValue = [int]$_.ProcessId
                    if ([string]$_.Name -ine 'claude.exe' -or -not $owned.ContainsKey($pidValue)) {
                        return $false
                    }
                    $parentPid = $ownedParents[$pidValue]
                    return -not (
                        $ownedNames.ContainsKey($parentPid) -and
                        $ownedNames[$parentPid] -ieq 'claude.exe')
                }
            ).Count
            $maxConcurrentClaude = [Math]::Max($maxConcurrentClaude, $runningClaude)
            Start-Sleep -Milliseconds 100
            $rootProcess.Refresh()
        }

        $rootProcess.WaitForExit()
        $wallTime = [DateTimeOffset]::Now - $startedAt
        Start-Sleep -Seconds 10
        $remaining = 0
        foreach ($entry in $owned.GetEnumerator()) {
            $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$($entry.Key)" -ErrorAction SilentlyContinue
            if ($null -eq $candidate) {
                continue
            }
            $creation = ConvertTo-UtcCreationTime $candidate.CreationDate
            if ($creation -eq $entry.Value) {
                $remaining++
            }
        }

        $results.Add([pscustomobject]@{
            Iteration = $iteration
            WallSeconds = [Math]::Round($wallTime.TotalSeconds, 3)
            ExitCode = $rootProcess.ExitCode
            Timeout = $rootProcess.ExitCode -ne 0
            OwnedClaudePids = @($claudeSessions)
            OwnedClaudeSessionRoots = @(
                $claudeSessions | ForEach-Object {
                    "{0}@{1:O}" -f $_, $owned[$_]
                }
            )
            MaxConcurrentClaude = $maxConcurrentClaude
            OwnedProcessesAfter10Seconds = $remaining
        })
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'CODEX_USAGE_MONITOR_ACTUAL_STARTUP_TIMEOUT_SECONDS',
        $previousTimeout,
        [EnvironmentVariableTarget]::Process)
}

$results | Format-Table Iteration, WallSeconds, ExitCode, Timeout, MaxConcurrentClaude, OwnedProcessesAfter10Seconds
foreach ($result in $results) {
    Write-Output (
        "Iteration {0} session roots: {1}" -f
        $result.Iteration,
        ($result.OwnedClaudeSessionRoots -join ', '))
}
$wallValues = @($results | ForEach-Object WallSeconds | Sort-Object)
$maximum = ($wallValues | Measure-Object -Maximum).Maximum
$p50Index = [Math]::Min($wallValues.Count - 1, [Math]::Floor(($wallValues.Count - 1) * 0.50))
$p95Index = [Math]::Min($wallValues.Count - 1, [Math]::Floor(($wallValues.Count - 1) * 0.95))
Write-Output ("Publisher: {0}" -f $publisher)
Write-Output ("p50={0:N3}s p95={1:N3}s max={2:N3}s" -f $wallValues[$p50Index], $wallValues[$p95Index], $maximum)

$failed = @(
    $results | Where-Object {
        $_.ExitCode -ne 0 -or
        $_.Timeout -or
        $_.MaxConcurrentClaude -gt 1 -or
        $_.OwnedProcessesAfter10Seconds -ne 0 -or
        $_.WallSeconds -ge 45
    }
)
if ($failed.Count -ne 0) {
    throw "AC-26 failed for $($failed.Count) of $Count iterations."
}

Write-Output "AC-26 passed: count=$Count, timeout=0, maxWall<45s, concurrentClaude<=1, ownedProcessesAfter10Seconds=0."
