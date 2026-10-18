# ASCII only. Claude Code invokes this bridge through Windows PowerShell 5.1,
# which decodes BOM-less files as ANSI. Non-ASCII characters break parsing there.
# Optional -PipeName lets the monitor use a per-launch private pipe for its own session.
# The default keeps the fixed pipe used by long-running user sessions.
param(
    [string]$PipeName = 'CodexUsageMonitor-Claude-v1'
)

$ErrorActionPreference = 'Stop'
try {
    if ([string]::IsNullOrWhiteSpace($PipeName)) { exit 0 }
    $maximumChars = 16385
    $buffer = [char[]]::new($maximumChars)
    $count = [Console]::In.ReadBlock($buffer, 0, $maximumChars)
    if ($count -eq 0 -or $count -ge $maximumChars) { exit 0 }
    $text = [string]::new($buffer, 0, $count)
    if ([Text.Encoding]::UTF8.GetByteCount($text) -gt 16384) { exit 0 }
    $source = $text | ConvertFrom-Json
    $minimal = [ordered]@{
        protocol = 1
        version = if ($source.version -is [string]) { $source.version } else { $null }
        rate_limits = $source.rate_limits
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($minimal | ConvertTo-Json -Depth 5 -Compress))
    if ($bytes.Length -gt 16384) { exit 0 }
    # CurrentUserOnly exists in modern .NET, but not in the .NET Framework used by
    # Windows PowerShell 5.1. Use it when available and retain the server-side
    # CurrentUserOnly boundary plus the legacy client constructor on 5.1.
    $pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous
    if ([Enum]::GetNames([IO.Pipes.PipeOptions]) -contains 'CurrentUserOnly') {
        $pipeOptions = $pipeOptions -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
    }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::Out, $pipeOptions)
    try {
        $pipe.Connect(100)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
    }
    finally {
        $pipe.Dispose()
    }
}
catch {
    exit 0
}
