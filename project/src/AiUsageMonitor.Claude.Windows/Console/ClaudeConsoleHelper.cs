using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AiUsageMonitor.Claude.Windows.Console;

public sealed record ConsoleHelperResponse(
    bool Ok,
    string? Reason,
    IReadOnlyList<string> Lines,
    int Width,
    int Height,
    int Written);

/// <summary>
/// Appをhelper modeで自己起動し、対象CLIのconsoleだけをboundedに読み書きする。
/// 書込み操作は固定の/usageとEscapeに限定する。
/// </summary>
[ExcludeFromCodeCoverage]
public static class ClaudeConsoleHelper
{
    public const string Marker = "--claude-console-helper";
    public const int MaximumWidth = 400;
    public const int MaximumHeight = 120;

    public static bool TryHandle(IReadOnlyList<string> args, TextWriter output)
    {
        if (args.Count == 0 || !string.Equals(args[0], Marker, StringComparison.Ordinal))
            return false;

        ConsoleHelperResponse result;
        try
        {
            if (args.Count != 3 ||
                !int.TryParse(args[2], out int targetPid) ||
                targetPid <= 0)
            {
                result = Failure("INVALID_ARGUMENTS");
            }
            else
            {
                result = args[1] switch
                {
                    "read" => Read(targetPid),
                    "usage" => Write(targetPid, "/usage", 13),
                    "escape" => Write(targetPid, string.Empty, 27),
                    _ => Failure("INVALID_OPERATION"),
                };
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            IOException or
            InvalidOperationException)
        {
            result = Failure("HELPER_EXCEPTION");
        }

        output.Write(JsonSerializer.Serialize(result));
        return true;
    }

    private static ConsoleHelperResponse Read(int targetPid)
    {
        if (!Attach(targetPid, out string? reason))
            return Failure(reason!);
        try
        {
            using SafeFileHandle output = Native.CreateFile(
                "CONOUT$",
                0xC0000000,
                3,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero);
            if (output.IsInvalid)
                return Failure("CONOUT_FAILED");
            if (!Native.GetConsoleScreenBufferInfo(output, out ConsoleScreenBufferInfo info))
                return Failure("SCREEN_INFO_FAILED");

            // screen buffer全体の物理末尾ではなく、現在のwindow（viewport）下端を基準に
            // 遡って直近最大MaximumHeight行を読む。consoleのwindowはカーソル追従で自動
            // スクロールするため、Window.Bottom付近が常に最新の描画位置に近い（buffer全体の
            // 末尾は、Console.Clear()相当の全面再描画直後は未使用の空白域になりうる）。
            // window矩形の可視行数だけでは/usageの可変長描画でCurrent sessionが領域外へ
            // 押し出されるため、可視行数を超えた直近scrollbackも対象に含める。最新frameの
            // 切り出しはClaudeCliUsageScreenParser側で行う。
            int windowBottom = info.Window.Bottom;
            if (!TryGetReadWidth(info.Size.X, out int width) || windowBottom < 0)
                return Failure("SCREEN_BOUNDS_INVALID");

            int height = Math.Min(windowBottom + 1, MaximumHeight);
            short startY = checked((short)(windowBottom + 1 - height));

            var lines = new List<string>(height);
            for (short y = startY; y <= windowBottom; y++)
            {
                var builder = new StringBuilder(width);
                var position = new Coord(0, y);
                if (!Native.ReadConsoleOutputCharacter(
                        output,
                        builder,
                        (uint)width,
                        position,
                        out uint read))
                    return Failure("SCREEN_READ_FAILED");
                lines.Add(builder.ToString(0, checked((int)read)).TrimEnd());
            }
            return new(true, null, lines, width, height, 0);
        }
        finally
        {
            Native.FreeConsole();
        }
    }

    private static ConsoleHelperResponse Write(int targetPid, string text, ushort virtualKey)
    {
        if (!Attach(targetPid, out string? reason))
            return Failure(reason!);
        try
        {
            using SafeFileHandle input = Native.CreateFile(
                "CONIN$",
                0xC0000000,
                3,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero);
            if (input.IsInvalid)
                return Failure("CONIN_FAILED");

            var records = new List<InputRecord>((text.Length + 1) * 2);
            foreach (char character in text)
            {
                records.Add(Key(character, 0, true));
                records.Add(Key(character, 0, false));
            }
            if (virtualKey != 0)
            {
                char character = virtualKey == 13 ? '\r' : (char)27;
                records.Add(Key(character, virtualKey, true));
                records.Add(Key(character, virtualKey, false));
            }
            InputRecord[] buffer = records.ToArray();
            if (!Native.WriteConsoleInput(input, buffer, (uint)buffer.Length, out uint written) ||
                written != buffer.Length)
                return Failure("INPUT_WRITE_FAILED");
            return new(true, null, [], 0, 0, checked((int)written));
        }
        finally
        {
            Native.FreeConsole();
        }
    }

    private static bool Attach(int targetPid, out string? reason)
    {
        Native.FreeConsole();
        if (!Native.AttachConsole((uint)targetPid))
        {
            reason = "ATTACH_FAILED";
            return false;
        }
        reason = null;
        return true;
    }

    private static InputRecord Key(char character, ushort virtualKey, bool down) =>
        new()
        {
            EventType = 1,
            KeyEvent = new KeyEventRecord
            {
                KeyDown = down,
                RepeatCount = 1,
                VirtualKeyCode = virtualKey,
                UnicodeChar = character,
            },
        };

    private static ConsoleHelperResponse Failure(string reason) =>
        new(false, reason, [], 0, 0, 0);

    internal static bool TryGetReadWidth(int bufferWidth, out int width)
    {
        if (bufferWidth <= 0)
        {
            width = 0;
            return false;
        }
        width = Math.Min(bufferWidth, MaximumWidth);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;
        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetConsoleScreenBufferInfo(
            SafeFileHandle output,
            out ConsoleScreenBufferInfo info);

#pragma warning disable CA1838 // Win32 ReadConsoleOutputCharacterW requires a writable UTF-16 buffer.
        [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadConsoleOutputCharacter(
            SafeFileHandle output,
            StringBuilder characters,
            uint length,
            Coord position,
            out uint read);
#pragma warning restore CA1838

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteConsoleInput(
            SafeFileHandle input,
            InputRecord[] buffer,
            uint length,
            out uint written);
    }
}
