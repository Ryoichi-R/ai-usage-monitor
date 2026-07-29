[CmdletBinding()]
param(
    [ValidateRange(50, 1000)]
    [int]$Count = 50,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$bridge = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\AiUsageMonitor.App\Assets\claude-statusline-bridge.ps1'))
if (-not (Test-Path -LiteralPath $bridge)) { throw "Bridge not found: $bridge" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot ('..\TestResults\bridge-measurement-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (-not $OutputPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be under the ai-usage-monitor project.'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null

$payload = '{"version":"measurement","rate_limits":null}'
$durations = [Collections.Generic.List[double]]::new()
$failures = 0
for ($index = 0; $index -lt $Count; $index++) {
    $pipe = [IO.Pipes.NamedPipeServerStream]::new(
        'CodexUsageMonitor-Claude-v1',
        [IO.Pipes.PipeDirection]::In,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $wait = $pipe.BeginWaitForConnection($null, $null)
        $startInfo = [Diagnostics.ProcessStartInfo]::new('pwsh')
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.CreateNoWindow = $true
        $startInfo.ArgumentList.Add('-NoLogo')
        $startInfo.ArgumentList.Add('-NoProfile')
        $startInfo.ArgumentList.Add('-File')
        $startInfo.ArgumentList.Add($bridge)
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $timer = [Diagnostics.Stopwatch]::StartNew()
        if (-not $process.Start()) { throw 'Failed to start bridge process.' }
        $process.StandardInput.Write($payload)
        $process.StandardInput.Close()
        if (-not $wait.AsyncWaitHandle.WaitOne(5000)) { throw 'Bridge did not connect within 5 seconds.' }
        $pipe.EndWaitForConnection($wait)
        $buffer = [byte[]]::new(16385)
        while ($pipe.Read($buffer, 0, $buffer.Length) -gt 0) { }
        $process.WaitForExit(5000) | Out-Null
        $timer.Stop()
        $durations.Add($timer.Elapsed.TotalMilliseconds)
    }
    catch {
        $failures++
    }
    finally {
        if ($null -ne $process) { $process.Dispose() }
        $pipe.Dispose()
    }
}

$sorted = @($durations | Sort-Object)
if ($sorted.Count -eq 0) { throw 'Every bridge measurement failed.' }
$p50 = $sorted[[Math]::Min($sorted.Count - 1, [Math]::Floor($sorted.Count * 0.50))]
$p95 = $sorted[[Math]::Min($sorted.Count - 1, [Math]::Floor($sorted.Count * 0.95))]
[ordered]@{
    measuredAt = (Get-Date).ToString('o')
    host = 'pwsh'
    hostVersion = $PSVersionTable.PSVersion.ToString()
    requestedRuns = $Count
    successfulRuns = $sorted.Count
    failures = $failures
    p50Milliseconds = [Math]::Round($p50, 2)
    p95Milliseconds = [Math]::Round($p95, 2)
} | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8

"Measurement receipt: $OutputPath"
