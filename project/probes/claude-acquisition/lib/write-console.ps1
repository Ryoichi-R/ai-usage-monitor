# Phase 0 probe helper: 対象プロセスのコンソール入力バッファへキー入力を注入する。
# 送出できるのは -Text と -Key に限定し、任意文字列の送出経路を作らない。
param(
    [Parameter(Mandatory)][int]$TargetPid,
    [string]$Text = '',
    [ValidateSet('None', 'Enter', 'Escape')][string]$Key = 'None',
    [Parameter(Mandatory)][string]$OutFile
)

$ErrorActionPreference = 'Stop'

$signature = @'
using System;
using System.Runtime.InteropServices;
public static class ProbeInput {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint dwProcessId);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
  [StructLayout(LayoutKind.Sequential)]
  public struct KEY_EVENT_RECORD {
    [MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
    public ushort RepeatCount; public ushort VirtualKeyCode; public ushort VirtualScanCode;
    public char UnicodeChar; public uint ControlKeyState;
  }
  [StructLayout(LayoutKind.Explicit)]
  public struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] buffer, uint length, out uint written);
}
'@

$result = [ordered]@{ ok = $false; reason = $null; written = 0 }

function New-ProbeKeyRecord {
    param([char]$Char, [uint16]$VirtualKey, [bool]$Down)
    $record = New-Object 'ProbeInput+INPUT_RECORD'
    $record.EventType = 1
    $key = New-Object 'ProbeInput+KEY_EVENT_RECORD'
    $key.KeyDown = $Down
    $key.RepeatCount = 1
    $key.VirtualKeyCode = $VirtualKey
    $key.VirtualScanCode = 0
    $key.UnicodeChar = $Char
    $key.ControlKeyState = 0
    $record.KeyEvent = $key
    return $record
}

try {
    Add-Type -TypeDefinition $signature -Language CSharp

    [void][ProbeInput]::FreeConsole()
    if (-not [ProbeInput]::AttachConsole([uint32]$TargetPid)) {
        $result.reason = 'ATTACH_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    }
    else {
        $handle = [ProbeInput]::CreateFileW('CONIN$', ([uint32]3221225472), 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
        if ($handle -eq [IntPtr]::new(-1)) {
            $result.reason = 'CONIN_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        }
        else {
            $records = [Collections.Generic.List[object]]::new()
            foreach ($char in $Text.ToCharArray()) {
                $records.Add((New-ProbeKeyRecord -Char $char -VirtualKey 0 -Down $true))
                $records.Add((New-ProbeKeyRecord -Char $char -VirtualKey 0 -Down $false))
            }
            switch ($Key) {
                'Enter' {
                    $records.Add((New-ProbeKeyRecord -Char ([char]13) -VirtualKey 13 -Down $true))
                    $records.Add((New-ProbeKeyRecord -Char ([char]13) -VirtualKey 13 -Down $false))
                }
                'Escape' {
                    $records.Add((New-ProbeKeyRecord -Char ([char]27) -VirtualKey 27 -Down $true))
                    $records.Add((New-ProbeKeyRecord -Char ([char]27) -VirtualKey 27 -Down $false))
                }
            }
            if ($records.Count -eq 0) {
                $result.reason = 'NOTHING_TO_SEND'
            }
            else {
                $buffer = [object[]]$records.ToArray()
                $typed = [Array]::CreateInstance([type]'ProbeInput+INPUT_RECORD', $buffer.Length)
                for ($i = 0; $i -lt $buffer.Length; $i++) { $typed.SetValue($buffer[$i], $i) }
                $written = 0
                if ([ProbeInput]::WriteConsoleInputW($handle, $typed, [uint32]$typed.Length, [ref]$written)) {
                    $result.ok = $true
                    $result.written = [int]$written
                }
                else {
                    $result.reason = 'WRITE_FAILED_' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                }
            }
        }
    }
}
catch {
    $result.reason = 'EXCEPTION_' + $_.Exception.GetType().Name
}

[IO.File]::WriteAllText($OutFile, ($result | ConvertTo-Json -Depth 3 -Compress), [Text.UTF8Encoding]::new($false))
