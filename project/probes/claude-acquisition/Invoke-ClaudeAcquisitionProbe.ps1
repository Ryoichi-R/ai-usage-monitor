<#
.SYNOPSIS
    Claude 利用率取得経路の Phase 0 検証 probe。

.DESCRIPTION
    plans/old/20260724_ai-usage-monitor-claude-desktop-usage-acquisition-plan.md の Phase 0 を実行する
    使い捨て harness。製品 solution には含めず、製品コードへ流用しない。

    model prompt を送らず、送出する入力は "/usage" と終了操作だけに限定する。
    ワークスペース信頼ダイアログを自動承認しない（検出したら入力を送らず終了する）。

.PARAMETER Scenario
    Setup   : 信頼承認の手順を表示する（承認操作は代行しない）
    B1      : /usage 実行後に statusLine へ rate_limits が載るかを判定する（最優先実験）
    Screens : 画面署名を採取し、数値を伏せた fixture を保存する
    Residue : ~/.claude 配下の残存物を計測する

.EXAMPLE
    pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Setup
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Setup', 'B1', 'Screens', 'Residue')]
    [string]$Scenario,

    [string]$ExecutablePath,
    [string]$WorkspacePath,
    [int]$TimeoutSeconds = 90,
    [switch]$SkipPromptHistory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\probe-common.ps1')

$probeRoot = $PSScriptRoot
$capturedRoot = Join-Path $probeRoot 'captured'

function Resolve-ProbeContext {
    $executable = Resolve-ProbeClaudeExecutable -ExecutablePath $ExecutablePath
    if (-not $executable) { throw 'claude.exe を解決できません。-ExecutablePath で明示してください。' }
    $workspace = New-ProbeWorkspace -Path $WorkspacePath
    return [pscustomobject]@{ Executable = $executable; Workspace = $workspace }
}

function New-ProbeSettingsFile {
    param([Parameter(Mandatory)][string]$PipeName)

    $bridge = Join-Path $probeRoot 'assets\probe-statusline-bridge.ps1'
    $command = 'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}" -PipeName {1}' -f $bridge, $PipeName
    $settings = @{
        statusLine = @{
            type            = 'command'
            command         = $command
            refreshInterval = 10
        }
    }
    $path = Join-Path ([IO.Path]::GetTempPath()) ('probe-settings-' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    return $path
}

function Get-ProbeArguments {
    param(
        [string]$SettingsPath,
        [switch]$Accessibility
    )

    $arguments = '--tools "" --no-chrome --strict-mcp-config'
    if ($Accessibility) {
        $arguments += ' --safe-mode --ax-screen-reader'
    }
    if ($SettingsPath) {
        $arguments = '--setting-sources "" --settings "{0}" {1}' -f $SettingsPath, $arguments
    }
    return $arguments
}

function Wait-ProbeReadyScreen {
    param(
        [Parameter(Mandatory)]$Process,
        [int]$AttemptCount = 12,
        [int]$DelaySeconds = 3
    )

    for ($attempt = 1; $attempt -le $AttemptCount; $attempt++) {
        Start-Sleep -Seconds $DelaySeconds
        if ($Process.HasExited) {
            return [pscustomobject]@{ Signature = 'Exited'; Screen = $null }
        }
        $screen = Read-ProbeScreen -TargetPid $Process.Id
        if (-not $screen.ok) { continue }
        $signature = Get-ProbeScreenSignature -Lines $screen.lines
        if ($signature -ne 'Unknown') {
            return [pscustomobject]@{ Signature = $signature; Screen = $screen }
        }
    }
    return [pscustomobject]@{ Signature = 'Unknown'; Screen = $null }
}

function Save-ProbeCapture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    if (-not (Test-Path -LiteralPath $capturedRoot)) {
        New-Item -ItemType Directory -Path $capturedRoot -Force | Out-Null
    }
    $path = Join-Path $capturedRoot ("{0}.txt" -f $Name)
    [IO.File]::WriteAllLines($path, (Protect-ProbeScreenText -Lines $Lines), [Text.UTF8Encoding]::new($false))
    return $path
}

function Invoke-SetupScenario {
    $context = Resolve-ProbeContext
    Write-Host ''
    Write-Host '== Phase 0 セットアップ手順 ==' -ForegroundColor Cyan
    Write-Host ''
    Write-Host ("claude.exe        : {0}" -f $context.Executable.Path)
    Write-Host ("署名              : {0} / publisher OK = {1}" -f $context.Executable.SignatureStatus, $context.Executable.PublisherOk)
    Write-Host ("version           : {0}" -f $context.Executable.Version)
    Write-Host ("専用ディレクトリ  : {0}" -f $context.Workspace)
    Write-Host ''
    Write-Host '次のコマンドを利用者自身が実行し、信頼ダイアログで「1. Yes, I trust this folder」を選んでください。'
    Write-Host 'probe はこの承認を代行しません。承認後は Ctrl+C か /exit で終了して構いません。'
    Write-Host ''
    Write-Host ("  cd `"{0}`"" -f $context.Workspace) -ForegroundColor Yellow
    Write-Host ("  `"{0}`"" -f $context.Executable.Path) -ForegroundColor Yellow
    Write-Host ''
    Write-Host '承認後に次を実行してください:'
    Write-Host ('  pwsh -NoProfile -File "{0}" -Scenario B1' -f $PSCommandPath) -ForegroundColor Yellow
    Write-Host ''
}

function Invoke-B1Scenario {
    $context = Resolve-ProbeContext
    if (-not $context.Executable.PublisherOk) {
        throw ("実行ファイルの署名を確認できません（{0}）。自動実行しません。" -f $context.Executable.SignatureStatus)
    }

    $pipeName = 'AiUsageMonitor-Claude-probe-' + [Guid]::NewGuid().ToString('N')
    $settingsPath = New-ProbeSettingsFile -PipeName $pipeName
    $residueBefore = Get-ProbeResidueSnapshot
    $environment = @{}
    if ($SkipPromptHistory) { $environment['CLAUDE_CODE_SKIP_PROMPT_HISTORY'] = '1' }

    $process = $null
    $verdict = 'NO-GO'
    $facts = @{}
    $started = Get-Date
    try {
        $process = Start-ProbeClaude -ExecutablePath $context.Executable.Path `
            -WorkingDirectory $context.Workspace -Arguments (Get-ProbeArguments -SettingsPath $settingsPath) `
            -Environment $environment
        $facts['cli_version'] = $context.Executable.Version
        $facts['signature'] = $context.Executable.SignatureStatus

        $ready = Wait-ProbeReadyScreen -Process $process
        $facts['first_screen_signature'] = $ready.Signature

        switch ($ready.Signature) {
            'TrustPrompt' {
                $verdict = 'BLOCKED_TRUST_PROMPT'
                Write-Warning '信頼ダイアログを検出しました。入力を送らず終了します。-Scenario Setup を先に実行してください。'
                return
            }
            'SetupScreen' {
                $verdict = 'BLOCKED_SETUP_SCREEN'
                Write-Warning 'セットアップ画面を検出しました。入力を送らず終了します。'
                return
            }
            'SignedOut' {
                $verdict = 'BLOCKED_SIGNED_OUT'
                Write-Warning '未認証を検出しました。'
                return
            }
            'Ready' { }
            default {
                $verdict = 'BLOCKED_UNKNOWN_SCREEN'
                Write-Warning ('画面を判定できません（{0}）。fail-closed で終了します。' -f $ready.Signature)
                return
            }
        }

        Write-Host '/usage を送出します（送出する文字列はこれと終了操作だけです）。'
        $initialPipe = New-ProbePipeListener -PipeName $pipeName
        $sent = Send-ProbeInput -TargetPid $process.Id -Text '/usage' -Key 'Enter'
        $facts['input_sent'] = $sent.ok
        if (-not $sent.ok) {
            $initialPipe.Server.Dispose()
            $verdict = 'INPUT_FAILED'
            $facts['input_reason'] = $sent.reason
            return
        }

        $result = Wait-ProbePipePayload -PipeName $pipeName -TimeoutSeconds $TimeoutSeconds `
            -InitialServer $initialPipe.Server -InitialConnect $initialPipe.Connect
        $facts['statusline_payload_count'] = $result.PayloadCount
        $facts['statusline_has_rate_limits'] = $result.HasRateLimits
        $facts['rate_limit_window_keys'] = ($result.WindowKeys -join ',')
        $verdict = if ($result.HasRateLimits) { 'GO' } elseif ($result.Received) { 'NO-GO_NO_RATE_LIMITS' } else { 'NO-GO_NO_PAYLOAD' }

        Start-Sleep -Seconds 2
        $after = Read-ProbeScreen -TargetPid $process.Id
        if ($after.ok) {
            $facts['screen_after_usage'] = Get-ProbeScreenSignature -Lines $after.lines
            Save-ProbeCapture -Name 'usage-screen' -Lines $after.lines | Out-Null
        }
    }
    finally {
        if ($process) {
            Send-ProbeInput -TargetPid $process.Id -Key 'Escape' | Out-Null
            Start-Sleep -Milliseconds 500
            Stop-ProbeClaude -Process $process
        }
        Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue

        $residue = Compare-ProbeResidue -Before $residueBefore -After (Get-ProbeResidueSnapshot)
        $facts['residue_added_session_files'] = $residue.AddedSessionFileCount
        $facts['residue_added_session_bytes'] = $residue.AddedSessionBytes
        $facts['residue_history_delta_bytes'] = $residue.HistoryDeltaBytes
        $facts['skip_prompt_history'] = [bool]$SkipPromptHistory
        $facts['elapsed_seconds'] = [int]((Get-Date) - $started).TotalSeconds

        $receipt = Write-ProbeReceipt -Scenario 'b1' -Verdict $verdict -Facts $facts
        Write-Host ''
        Write-Host ("判定: {0}" -f $verdict) -ForegroundColor Cyan
        Write-Host ("receipt: {0}" -f $receipt)
    }
}

function Invoke-ScreensScenario {
    $context = Resolve-ProbeContext
    $process = $null
    $facts = @{}
    $signatures = [Collections.Generic.List[string]]::new()
    $usageSent = $false
    try {
        $process = Start-ProbeClaude -ExecutablePath $context.Executable.Path `
            -WorkingDirectory $context.Workspace -Arguments (Get-ProbeArguments -SettingsPath $null -Accessibility)

        for ($attempt = 1; $attempt -le 8; $attempt++) {
            Start-Sleep -Seconds 3
            if ($process.HasExited) { break }
            $screen = Read-ProbeScreen -TargetPid $process.Id
            if (-not $screen.ok) { continue }
            $signature = Get-ProbeScreenSignature -Lines $screen.lines
            if (-not $signatures.Contains($signature)) {
                $signatures.Add($signature)
                if ($signature -ne 'Ready') {
                    Save-ProbeCapture -Name ("screen-{0}" -f $signature.ToLowerInvariant()) -Lines $screen.lines | Out-Null
                }
            }
            if ($signature -eq 'Ready' -and -not $usageSent) {
                $sent = Send-ProbeInput -TargetPid $process.Id -Text '/usage' -Key 'Enter'
                $usageSent = [bool]$sent.ok
            }
        }
        $facts['signatures_seen'] = ($signatures -join ',')
        $facts['usage_sent'] = $usageSent
        $facts['capture_directory'] = $capturedRoot
    }
    finally {
        Stop-ProbeClaude -Process $process
        $verdict = if ($signatures.Count -gt 0 -and -not $signatures.Contains('Unknown')) { 'GO' } else { '条件付きGO' }
        $receipt = Write-ProbeReceipt -Scenario 'screens' -Verdict $verdict -Facts $facts
        Write-Host ("receipt: {0}" -f $receipt)
    }
}

function Invoke-ResidueScenario {
    $context = Resolve-ProbeContext
    $before = Get-ProbeResidueSnapshot
    $process = $null
    $facts = @{}
    try {
        $environment = @{}
        if ($SkipPromptHistory) { $environment['CLAUDE_CODE_SKIP_PROMPT_HISTORY'] = '1' }
        $process = Start-ProbeClaude -ExecutablePath $context.Executable.Path `
            -WorkingDirectory $context.Workspace -Arguments (Get-ProbeArguments -SettingsPath $null) `
            -Environment $environment
        $ready = Wait-ProbeReadyScreen -Process $process
        $facts['screen_signature'] = $ready.Signature
    }
    finally {
        Stop-ProbeClaude -Process $process
        Start-Sleep -Seconds 2
        $residue = Compare-ProbeResidue -Before $before -After (Get-ProbeResidueSnapshot)
        $facts['added_session_files'] = $residue.AddedSessionFileCount
        $facts['added_session_bytes'] = $residue.AddedSessionBytes
        $facts['grown_session_files'] = $residue.GrownSessionFileCount
        $facts['history_delta_bytes'] = $residue.HistoryDeltaBytes
        $facts['skip_prompt_history'] = [bool]$SkipPromptHistory
        $verdict = if ($residue.AddedSessionFileCount -eq 0 -and $residue.HistoryDeltaBytes -le 0) { '残存なし' } else { '残存あり' }
        $receipt = Write-ProbeReceipt -Scenario 'residue' -Verdict $verdict -Facts $facts
        Write-Host ("判定: {0}" -f $verdict) -ForegroundColor Cyan
        Write-Host ("receipt: {0}" -f $receipt)
    }
}

switch ($Scenario) {
    'Setup' { Invoke-SetupScenario }
    'B1' { Invoke-B1Scenario }
    'Screens' { Invoke-ScreensScenario }
    'Residue' { Invoke-ResidueScenario }
}
