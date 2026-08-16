using System.Text;
using System.IO.Pipes;
using System.Text.Json;
using AiUsageMonitor.Claude.Windows.Console;

string executableName =
    Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
if (args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal) &&
    executableName.Contains("helper-hang", StringComparison.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
if (args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal) &&
    executableName.Contains("helper-invalid", StringComparison.Ordinal))
{
    System.Console.WriteLine("not-json");
    return;
}
if (args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal) &&
    executableName.Contains("helper-at-limit", StringComparison.Ordinal))
{
    WriteExactLengthHelperResponse(ConsoleHelperClient.MaximumResponseChars);
    return;
}
if (args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal) &&
    executableName.Contains("helper-oversized", StringComparison.Ordinal))
{
    WriteExactLengthHelperResponse(ConsoleHelperClient.MaximumResponseChars + 1);
    return;
}

if (args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal) &&
    args.Contains("read", StringComparer.Ordinal) &&
    executableName.Contains("helper-read-fails", StringComparison.Ordinal))
{
    var failure = new ConsoleHelperResponse(false, "SCREEN_READ_FAILED", [], 0, 0, 0);
    System.Console.Write(JsonSerializer.Serialize(failure));
    return;
}

if (ClaudeConsoleHelper.TryHandle(args, System.Console.Out))
    return;

if (args.Contains("--help", StringComparer.Ordinal))
{
    if (executableName.Contains("capability-hang", StringComparison.Ordinal))
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        return;
    }
    if (executableName.Contains("capability-oversized", StringComparison.Ordinal))
    {
        System.Console.WriteLine(new string('x', 129 * 1024));
        return;
    }
    if (executableName.Contains("capability-missing", StringComparison.Ordinal))
    {
        System.Console.WriteLine("--settings");
        return;
    }
    System.Console.WriteLine("--setting-sources --settings --tools --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader");
    return;
}
if (args.Contains("--version", StringComparer.Ordinal))
{
    System.Console.WriteLine("2.1.218 (fake)");
    return;
}

System.Console.OutputEncoding = Encoding.Unicode;
string? settingsPath = ValueAfter(args, "--settings");
string mode = ReadMode();
switch (mode)
{
    case "exit":
        return;
    case "trust":
        RenderTrust();
        break;
    case "signed-out":
        RenderSignedOut();
        break;
    case "setup":
        RenderSetup();
        break;
    case "usage":
        RenderUsage();
        break;
    case "unknown":
        RenderUnexpected();
        break;
    default:
        RenderReady();
        break;
}
var command = new StringBuilder();
while (true)
{
    ConsoleKeyInfo key = System.Console.ReadKey(intercept: true);
    if (key.Key == ConsoleKey.Escape)
        return;
    if (key.Key == ConsoleKey.Enter)
    {
        if (string.Equals(command.ToString(), "/usage", StringComparison.Ordinal))
        {
            if (string.Equals(mode, "partial-usage", StringComparison.Ordinal))
                await RenderPartialUsageAsync();
            else
                RenderUsage();
            if (!string.Equals(mode, "no-payload", StringComparison.Ordinal))
                SendStatusLine(settingsPath);
            command.Clear();
            continue;
        }
        RenderUnexpected();
        command.Clear();
        continue;
    }
    if (key.KeyChar != '\0')
        command.Append(key.KeyChar);
}

static void RenderReady()
{
    System.Console.Clear();
    System.Console.WriteLine("╭────────────────────────────────────────────────────────╮");
    System.Console.WriteLine("│ >                                                      │");
    System.Console.WriteLine("╰────────────────────────────────────────────────────────╯");
    System.Console.WriteLine("? for shortcuts");
}

static void RenderUsage()
{
    DateTimeOffset now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
    System.Console.Clear();
    System.Console.WriteLine("Usage");
    System.Console.WriteLine("Current session");
    System.Console.WriteLine("48% used");
    System.Console.WriteLine($"Resets {now.AddHours(1).ToString("htt", System.Globalization.CultureInfo.InvariantCulture)} (Asia/Tokyo)");
    System.Console.WriteLine("Current week (all models)");
    System.Console.WriteLine("32% used");
    System.Console.WriteLine($"Resets {now.AddDays(3).ToString("MMM d, htt", System.Globalization.CultureInfo.InvariantCulture)} (Asia/Tokyo)");
    System.Console.WriteLine("+50% weekly limits promo through Aug 28");
}

static async Task RenderPartialUsageAsync()
{
    System.Console.Clear();
    System.Console.WriteLine("Usage");
    System.Console.WriteLine("Current session");
    await Task.Delay(350);
    RenderUsage();
}

static void RenderTrust()
{
    System.Console.Clear();
    System.Console.WriteLine("Do you trust this folder?");
    System.Console.WriteLine("1. Yes, proceed");
    System.Console.WriteLine("2. No, exit");
}

static void RenderSignedOut()
{
    System.Console.Clear();
    System.Console.WriteLine("Sign in to Claude");
}

static void RenderSetup()
{
    System.Console.Clear();
    System.Console.WriteLine("Choose the text style that looks best");
}

static void RenderUnexpected()
{
    System.Console.Clear();
    System.Console.WriteLine("UNEXPECTED INPUT");
}

static string ReadMode()
{
    string marker = Path.Combine(Environment.CurrentDirectory, "fake-mode.txt");
    return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "ready";
}

static void WriteExactLengthHelperResponse(int totalChars)
{
    // ConsoleHelperClient.MaximumResponseCharsの境界を1文字単位で検証するため、
    // Reasonフィールドをエスケープ不要なASCII文字で埋めてJSON全体の長さを逆算する。
    var empty = new ConsoleHelperResponse(true, string.Empty, [], 0, 0, 0);
    int baseLength = JsonSerializer.Serialize(empty).Length;
    int paddingLength = Math.Max(0, totalChars - baseLength);
    var response = new ConsoleHelperResponse(true, new string('x', paddingLength), [], 0, 0, 0);
    System.Console.Write(JsonSerializer.Serialize(response));
}

static string? ValueAfter(string[] values, string name)
{
    int index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static void SendStatusLine(string? settingsPath)
{
    if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        return;
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
    string? statusCommand = document.RootElement
        .GetProperty("statusLine")
        .GetProperty("command")
        .GetString();
    const string marker = "-PipeName ";
    int markerIndex = statusCommand?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
    if (markerIndex < 0) return;
    string pipeName = statusCommand![(markerIndex + marker.Length)..].Trim();
    using var pipe = new NamedPipeClientStream(
        ".",
        pipeName,
        PipeDirection.Out,
        PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        protocol = 1,
        version = "2.1.218",
        rate_limits = new
        {
            five_hour = new { used_percentage = 48, resets_at = now + 3600 },
            seven_day = new { used_percentage = 32, resets_at = now + 86400 },
        },
    }));
    pipe.Write(payload);
    pipe.Flush();
}
