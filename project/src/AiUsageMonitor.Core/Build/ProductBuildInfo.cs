using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AiUsageMonitor.Core.Build;

/// <summary>ビルド時の埋め込み情報。ソースツリー、Git、ファイル更新日時には依存しない。</summary>
public sealed record ProductBuildInfo(
    string AppName,
    string Version,
    string SourceIdentifier,
    bool? HasUncommittedChanges,
    DateTimeOffset BuildTimeUtc)
{
    public static ProductBuildInfo Current { get; } = LoadEmbedded();

    public IReadOnlyList<KeyValuePair<string, string>> GetDisplayFields() =>
    [
        new("アプリ名", AppName),
        new("バージョン", Version),
        new("ソース識別子", SourceIdentifier),
        new("未コミット変更の有無", HasUncommittedChanges switch { true => "あり", false => "なし", null => "不明" }),
        new("ビルド日時", BuildTimeUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture)),
        new("実行版", GetExecutionVariant()),
    ];

    public string ToDisplayText() => string.Join(Environment.NewLine, GetDisplayFields().Select(field => $"{field.Key}: {field.Value}"));

    private static string GetExecutionVariant()
    {
        string os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "不明";
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "x86",
            Architecture.Arm => "ARM",
            _ => "不明",
        };
        return $"{os}／{architecture}";
    }

    private static ProductBuildInfo LoadEmbedded()
    {
        using Stream stream = typeof(ProductBuildInfo).Assembly.GetManifestResourceStream("AiUsageMonitor.BuildInfo.json")
            ?? throw new InvalidOperationException("Embedded build information is missing.");
        return JsonSerializer.Deserialize<ProductBuildInfo>(stream)
            ?? throw new InvalidOperationException("Embedded build information is invalid.");
    }
}
