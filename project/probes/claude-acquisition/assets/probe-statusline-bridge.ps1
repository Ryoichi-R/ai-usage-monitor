# ASCII only. Claude Code invokes this bridge through Windows PowerShell 5.1,
# which decodes BOM-less files as ANSI. Non-ASCII characters break parsing there.
# Phase 0 probe bridge: forwards the same minimal payload as the shipped bridge,
# but to a probe-private pipe so passive observations cannot be mistaken for the
# active /usage response.
param(
    [Parameter(Mandatory)][string]$PipeName
)

$ErrorActionPreference = 'Stop'
try {
    $maximumChars = 16385
    $buffer = [char[]]::new($maximumChars)
    $count = [Console]::In.ReadBlock($buffer, 0, $maximumChars)
    if ($count -eq 0 -or $count -ge $maximumChars) { exit 0 }
    $text = [string]::new($buffer, 0, $count)
    if ([Text.Encoding]::UTF8.GetByteCount($text) -gt 16384) { exit 0 }
    $source = $text | ConvertFrom-Json
    $minimal = [ordered]@{
        protocol    = 1
        version     = if ($source.version -is [string]) { $source.version } else { $null }
        rate_limits = $source.rate_limits
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($minimal | ConvertTo-Json -Depth 5 -Compress))
    if ($bytes.Length -gt 16384) { exit 0 }
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::Out)
    try {
        $pipe.Connect(100)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
    }
    finally {
        $pipe.Dispose()
    }
    Write-Output 'probe'
}
catch {
    exit 0
}
