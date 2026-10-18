using System.Text.Json;
using AiUsageMonitor.Claude.Windows.Console;

// ConsoleHelperClientはhost実行ファイルを --claude-console-helper <operation> <pid> で起動する。
// 実運用ではmonitor本体自身がこのhostを兼ねる。テストではこの実行ファイルがその役を担い、
// ファイル名に埋め込まれた変種名で異常系（timeout / 不正JSON / 上限超過 / 読取り失敗）を再現する。
string executableName =
    Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
bool isHelperInvocation = args.Contains(ClaudeConsoleHelper.Marker, StringComparer.Ordinal);

if (isHelperInvocation && executableName.Contains("helper-hang", StringComparison.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}
if (isHelperInvocation && executableName.Contains("helper-invalid", StringComparison.Ordinal))
{
    System.Console.WriteLine("not-json");
    return;
}
if (isHelperInvocation && executableName.Contains("helper-at-limit", StringComparison.Ordinal))
{
    WriteExactLengthHelperResponse(ConsoleHelperClient.MaximumResponseChars);
    return;
}
if (isHelperInvocation && executableName.Contains("helper-oversized", StringComparison.Ordinal))
{
    WriteExactLengthHelperResponse(ConsoleHelperClient.MaximumResponseChars + 1);
    return;
}
if (isHelperInvocation &&
    args.Contains("read", StringComparer.Ordinal) &&
    executableName.Contains("helper-read-fails", StringComparison.Ordinal))
{
    var failure = new ConsoleHelperResponse(false, "SCREEN_READ_FAILED", [], 0, 0, 0);
    System.Console.Write(JsonSerializer.Serialize(failure));
    return;
}

if (ClaudeConsoleHelper.TryHandle(args, System.Console.Out))
    return;

System.Console.Error.WriteLine(
    "This executable only serves the Claude console helper protocol. " +
    $"Expected the first argument to be {ClaudeConsoleHelper.Marker}.");
Environment.ExitCode = 2;

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
