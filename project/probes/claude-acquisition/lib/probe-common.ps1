# Phase 0 probe 共通関数。dot-source して使用する。
# 製品コードではない。使い捨ての検証 harness であり、製品 solution に含めない。

$script:ProbeRoot = Split-Path -Parent $PSScriptRoot

function Get-ProbeHostPath {
    $path = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($path)) { return 'powershell.exe' }
    return $path
}

function Resolve-ProbeClaudeExecutable {
    <#
    .SYNOPSIS
        公式インストール経路から claude.exe を解決し、Authenticode 署名を検証する。
    #>
    param([string]$ExecutablePath)

    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $candidates.Add($ExecutablePath)
    }
    else {
        $candidates.Add((Join-Path $env:USERPROFILE '.local\bin\claude.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\claude\claude.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'ClaudeCode\claude.exe'))
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $full = [IO.Path]::GetFullPath($candidate)
        if ($full.StartsWith('\\')) { continue }  # UNC は自動検出の対象にしない
        $signature = Get-AuthenticodeSignature -LiteralPath $full
        $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
        return [pscustomobject]@{
            Path            = $full
            SignatureStatus = [string]$signature.Status
            PublisherOk     = ($signature.Status -eq 'Valid' -and $subject -match 'Anthropic, PBC')
            Version         = (& $full --version 2>$null | Select-Object -First 1)
        }
    }
    return $null
}

function New-ProbeWorkspace {
    <#
    .SYNOPSIS
        監視アプリ専用ディレクトリに相当する空の作業ディレクトリを用意する。
    #>
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Join-Path $env:LOCALAPPDATA 'AiUsageMonitor\claude-probe-workspace'
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    return [IO.Path]::GetFullPath($Path)
}

function Get-ProbeResidueSnapshot {
    <#
    .SYNOPSIS
        ~/.claude 配下の残存物（session transcript と prompt history）を一覧する。
        内容は読まず、パスとサイズだけを記録する。
    #>
    $claudeRoot = Join-Path $env:USERPROFILE '.claude'
    $projects = Join-Path $claudeRoot 'projects'
    $history = Join-Path $claudeRoot 'history.jsonl'

    $files = @()
    if (Test-Path -LiteralPath $projects) {
        $files = @(Get-ChildItem -LiteralPath $projects -Recurse -File -ErrorAction SilentlyContinue |
            ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Length = $_.Length } })
    }
    $historyLength = if (Test-Path -LiteralPath $history) { (Get-Item -LiteralPath $history).Length } else { -1 }

    return [pscustomobject]@{
        SessionFiles  = $files
        HistoryLength = $historyLength
    }
}

function Compare-ProbeResidue {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After
    )

    $beforePaths = [Collections.Generic.HashSet[string]]::new(
        [string[]]@($Before.SessionFiles | ForEach-Object { $_.Path }), [StringComparer]::OrdinalIgnoreCase)
    $added = @($After.SessionFiles | Where-Object { -not $beforePaths.Contains($_.Path) })

    $beforeSizes = @{}
    foreach ($file in $Before.SessionFiles) { $beforeSizes[$file.Path] = $file.Length }
    $grown = @($After.SessionFiles | Where-Object {
            $beforeSizes.ContainsKey($_.Path) -and $_.Length -ne $beforeSizes[$_.Path]
        })
    $addedBytes = 0
    foreach ($file in $added) { $addedBytes += [long]$file.Length }

    return [pscustomobject]@{
        AddedSessionFileCount = $added.Count
        AddedSessionBytes     = $addedBytes
        GrownSessionFileCount = $grown.Count
        HistoryDeltaBytes     = $After.HistoryLength - $Before.HistoryLength
    }
}

function Start-ProbeClaude {
    <#
    .SYNOPSIS
        隠しコンソールで claude.exe を起動する。stdin/stdout は redirect しない。
    #>
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$Arguments,
        [hashtable]$Environment
    )

    $restore = @{}
    if ($Environment) {
        foreach ($key in $Environment.Keys) {
            $restore[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $Environment[$key], 'Process')
        }
    }
    try {
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList $Arguments `
            -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $restore.Keys) {
            [Environment]::SetEnvironmentVariable($key, $restore[$key], 'Process')
        }
    }
    return $process
}

function Stop-ProbeClaude {
    param($Process)

    if (-not $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction Stop
        }
    }
    catch {
        # 既に終了している場合は正常系として扱う。
    }
}

function Read-ProbeScreen {
    <#
    .SYNOPSIS
        子プロセス経由で対象コンソールの画面バッファを読む。
    #>
    param([Parameter(Mandatory)][int]$TargetPid)

    $outFile = Join-Path ([IO.Path]::GetTempPath()) ('probe-screen-' + [Guid]::NewGuid().ToString('N') + '.json')
    $script = Join-Path $PSScriptRoot 'read-console.ps1'
    try {
        Start-Process -FilePath (Get-ProbeHostPath) -WindowStyle Hidden -Wait -ArgumentList @(
            '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
            '-TargetPid', $TargetPid, '-OutFile', $outFile) | Out-Null
        if (-not (Test-Path -LiteralPath $outFile)) {
            return [pscustomobject]@{ ok = $false; reason = 'HELPER_NO_OUTPUT'; lines = @() }
        }
        # bounded read: helper 応答が上限を超えた場合は fail-closed。
        $length = (Get-Item -LiteralPath $outFile).Length
        if ($length -gt 65536) {
            return [pscustomobject]@{ ok = $false; reason = 'HELPER_OUTPUT_TOO_LARGE'; lines = @() }
        }
        return (Get-Content -LiteralPath $outFile -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    }
}

function Send-ProbeInput {
    param(
        [Parameter(Mandatory)][int]$TargetPid,
        [string]$Text = '',
        [ValidateSet('None', 'Enter', 'Escape')][string]$Key = 'None'
    )

    $outFile = Join-Path ([IO.Path]::GetTempPath()) ('probe-input-' + [Guid]::NewGuid().ToString('N') + '.json')
    $script = Join-Path $PSScriptRoot 'write-console.ps1'
    try {
        Start-Process -FilePath (Get-ProbeHostPath) -WindowStyle Hidden -Wait -ArgumentList @(
            '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
            '-TargetPid', $TargetPid, '-Text', $Text, '-Key', $Key, '-OutFile', $outFile) | Out-Null
        if (-not (Test-Path -LiteralPath $outFile)) {
            return [pscustomobject]@{ ok = $false; reason = 'HELPER_NO_OUTPUT' }
        }
        return (Get-Content -LiteralPath $outFile -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
    }
}

function Get-ProbeScreenSignature {
    <#
    .SYNOPSIS
        画面署名を判定する。数値抽出は行わない。C# 側 ClaudeCliScreenStateMachine と同じ分類を返す。
    #>
    param([string[]]$Lines)

    if (-not $Lines -or $Lines.Count -eq 0) { return 'Unknown' }
    $text = ($Lines -join "`n")

    if ($text -match 'trust this folder' -or $text -match 'Is this a project you created or one you trust' -or
        $text -match 'このフォルダーを信頼') { return 'TrustPrompt' }
    if ($text -match 'Sign in to Claude' -or $text -match 'Please run /login' -or
        $text -match '/login' -and $text -match 'expired') { return 'SignedOut' }
    if ($text -match 'Current session' -or $text -match '現在のセッション' -or
        ($text -match 'All models' -and $text -match 'resets')) { return 'UsageScreen' }
    if ($text -match 'Choose the text style' -or $text -match 'Select login method' -or
        $text -match 'Press Enter to continue') { return 'SetupScreen' }
    if ($text -match '│\s*>' -or $text -match '\? for shortcuts' -or $text -match 'Try "' -or
        ($text -match '\[Screen Reader Mode: on via flag\]' -and $text -match '(?m)^\$$')) { return 'Ready' }

    return 'Unknown'
}

function Protect-ProbeScreenText {
    <#
    .SYNOPSIS
        画面テキストから数値と URL を伏せる。fixture 化・記録用。
    #>
    param([string[]]$Lines)

    return @($Lines | ForEach-Object {
            $protected = ($_ -replace '\d', '#') -replace 'https?://\S+', '<url>'
            $protected = $protected -replace '[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}', '<email>'
            $protected = $protected -replace '(?i)^[ \t]*Welcome back .+!$', 'Welcome back <user>!'
            $protected = $protected -replace '(?i)^.*Organization.*$', '<account context redacted>'
            $protected = $protected -replace '(?i)[A-Z]:\\[^\s]+', '<path>'
            $protected = $protected -replace '(?i)~\\[^\s]+', '<path>'
            $protected
        })
}

function Wait-ProbePipePayload {
    <#
    .SYNOPSIS
        probe 専用 named pipe を立て、bridge からの payload を待つ。
        rate_limits が非 null の payload が届いた時点で成功として返す。
    #>
    param(
        [Parameter(Mandatory)][string]$PipeName,
        [int]$TimeoutSeconds = 90,
        [IO.Pipes.NamedPipeServerStream]$InitialServer,
        [Threading.Tasks.Task]$InitialConnect
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $payloads = [Collections.Generic.List[string]]::new()
    $first = $true

    while ((Get-Date) -lt $deadline) {
        if ($first -and $InitialServer) {
            $server = $InitialServer
            $connect = $InitialConnect
            $first = $false
        }
        else {
            $listener = New-ProbePipeListener -PipeName $PipeName
            $server = $listener.Server
            $connect = $listener.Connect
        }
        try {
            $remaining = [int]([Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds))
            if (-not $connect.Wait($remaining)) { break }

            $buffer = [byte[]]::new(16384)
            $memory = [IO.MemoryStream]::new()
            while ($true) {
                $read = $server.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                if ($memory.Length + $read -gt 16384) { break }
                $memory.Write($buffer, 0, $read)
            }
            $text = [Text.Encoding]::UTF8.GetString($memory.ToArray())
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $payloads.Add($text)
                $parsed = $null
                try { $parsed = $text | ConvertFrom-Json } catch { $parsed = $null }
                if ($parsed -and $parsed.PSObject.Properties.Name -contains 'rate_limits' -and $null -ne $parsed.rate_limits) {
                    return [pscustomobject]@{
                        Received       = $true
                        HasRateLimits  = $true
                        PayloadCount   = $payloads.Count
                        WindowKeys     = @($parsed.rate_limits.PSObject.Properties.Name)
                        ClientVersion  = [string]$parsed.version
                    }
                }
            }
        }
        finally {
            $server.Dispose()
        }
    }

    return [pscustomobject]@{
        Received      = ($payloads.Count -gt 0)
        HasRateLimits = $false
        PayloadCount  = $payloads.Count
        WindowKeys    = @()
        ClientVersion = $null
    }
}

function New-ProbePipeListener {
    param([Parameter(Mandatory)][string]$PipeName)

    $server = [IO.Pipes.NamedPipeServerStream]::new(
        $PipeName, [IO.Pipes.PipeDirection]::In, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    return [pscustomobject]@{
        Server  = $server
        Connect = $server.WaitForConnectionAsync()
    }
}

function Write-ProbeReceipt {
    <#
    .SYNOPSIS
        redacted receipt を docs/verification/ へ保存する。
        使用率の実値、account 情報、session ID、生画面を含めない。
    #>
    param(
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$Verdict,
        [Parameter(Mandatory)][hashtable]$Facts
    )

    $directory = Join-Path (Split-Path -Parent (Split-Path -Parent $script:ProbeRoot)) 'docs\verification'
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $path = Join-Path $directory ("claude-acquisition-{0}-{1}.md" -f $Scenario.ToLowerInvariant(), $stamp)

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("# Phase 0 receipt: $Scenario")
    $lines.Add('')
    $lines.Add(("- 実行時刻 (UTC): {0}" -f $stamp))
    $lines.Add(("- 判定: {0}" -f $Verdict))
    foreach ($key in ($Facts.Keys | Sort-Object)) {
        $lines.Add(("- {0}: {1}" -f $key, $Facts[$key]))
    }
    $lines.Add('')
    $lines.Add('このreceiptには使用率の実値、account情報、session ID、生画面を含めない。')

    [IO.File]::WriteAllLines($path, $lines.ToArray(), [Text.UTF8Encoding]::new($false))
    return $path
}
