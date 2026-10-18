# Phase 0 probe helper: 対象プロセスのコンソール画面バッファを読み取り JSON で出力する。
# 親プロセスから独立した子プロセスとして起動されることを前提とする
# （AttachConsole はプロセスにつき 1 つのコンソールしか接続できないため）。
param(
    [Parameter(Mandatory)][int]$TargetPid,
    [Parameter(Mandatory)][string]$OutFile
)

$ErrorActionPreference = 'Stop'

$signature = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ProbeConsole {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint dwProcessId);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
  [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
  [StructLayout(LayoutKind.Sequential)] public struct SMALL_RECT { public short L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct CSBI { public COORD Size; public COORD Cur; public ushort Attr; public SMALL_RECT Win; public COORD Max; }
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CSBI info);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern bool ReadConsoleOutputCharacterW(IntPtr h, StringBuilder buf, uint len, COORD pos, out uint read);
}
'@

$result = [ordered]@{ ok = $false; reason = $null; lines = @(); width = 0; height = 0 }

try {
    Add-Type -TypeDefinition $signature -Language CSharp

    [void][ProbeConsole]::FreeConsole()
    if (-not [ProbeConsole]::AttachConsole([uint32]$TargetPid)) {
        $result.reason = 'ATTACH_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    }
    else {
        # GENERIC_READ | GENERIC_WRITE = 0xC0000000。PowerShell では負の int になるため uint32 で渡す。
        $handle = [ProbeConsole]::CreateFileW('CONOUT$', ([uint32]3221225472), 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
        if ($handle -eq [IntPtr]::new(-1)) {
            $result.reason = 'CONOUT_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        }
        else {
            $info = New-Object 'ProbeConsole+CSBI'
            if (-not [ProbeConsole]::GetConsoleScreenBufferInfo($handle, [ref]$info)) {
                $result.reason = 'CSBI_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            }
            else {
                $width = $info.Size.X
                $lines = [Collections.Generic.List[string]]::new()
                # バッファ全体ではなくウィンドウ矩形だけを読む。
                for ($y = $info.Win.T; $y -le $info.Win.B; $y++) {
                    $builder = New-Object System.Text.StringBuilder ($width + 1)
                    $position = New-Object 'ProbeConsole+COORD'
                    $position.X = 0
                    $position.Y = [int16]$y
                    $read = 0
                    if ([ProbeConsole]::ReadConsoleOutputCharacterW($handle, $builder, [uint32]$width, $position, [ref]$read)) {
                        $lines.Add($builder.ToString(0, [int]$read).TrimEnd())
                    }
                }
                $result.ok = $true
                $result.lines = $lines.ToArray()
                $result.width = $width
                $result.height = $info.Win.B - $info.Win.T + 1
            }
        }
    }
}
catch {
    $result.reason = 'EXCEPTION_' + $_.Exception.GetType().Name
}

[IO.File]::WriteAllText($OutFile, ($result | ConvertTo-Json -Depth 4 -Compress), [Text.UTF8Encoding]::new($false))
