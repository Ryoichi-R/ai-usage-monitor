using System.Text;
using System.Xml;

using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// ~/Library/LaunchAgents/&lt;label&gt;.plistでログイン時自動起動を制御するIStartupServiceのmacOS実装。
/// plistの作成・削除だけを行い、launchctlで即時にloadしない（loadすると現在のセッションで2つ目のインスタンスが起動する）。
/// 次回ログイン時にlaunchdが読み込む。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentStartupService : IStartupService
{
    public const string DefaultLabel = "io.github.ryoichi-r.ai-usage-monitor";

    private readonly string _launchAgentsDirectory;
    private readonly string _label;
    private readonly Func<IReadOnlyList<string>> _programArguments;

    public LaunchAgentStartupService(
        string launchAgentsDirectory,
        Func<IReadOnlyList<string>> programArguments,
        string label = DefaultLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchAgentsDirectory);
        ArgumentNullException.ThrowIfNull(programArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _launchAgentsDirectory = launchAgentsDirectory;
        _programArguments = programArguments;
        _label = label;
    }

    public string PlistPath => Path.Combine(_launchAgentsDirectory, _label + ".plist");

    public void Apply(bool startWithSystem)
    {
        if (!startWithSystem)
        {
            // 自分のlabelのplistだけを削除する。存在しなければ何もしない。
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }

        IReadOnlyList<string> arguments = _programArguments();
        if (arguments.Count == 0 || arguments.Any(string.IsNullOrWhiteSpace) || !Path.IsPathFullyQualified(arguments[0]))
            throw new InvalidOperationException("The startup command must start with an absolute executable path.");
        string content = CreatePlist(_label, arguments);
        if (File.Exists(PlistPath) && File.ReadAllText(PlistPath) == content) return;

        Directory.CreateDirectory(_launchAgentsDirectory);
        string temporary = PlistPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, PlistPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string CreatePlist(string label, IReadOnlyList<string> programArguments)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
        };
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        using (XmlWriter writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartElement("plist");
            writer.WriteAttributeString("version", "1.0");
            writer.WriteStartElement("dict");
            writer.WriteElementString("key", "Label");
            writer.WriteElementString("string", label);
            writer.WriteElementString("key", "ProgramArguments");
            writer.WriteStartElement("array");
            foreach (string argument in programArguments) writer.WriteElementString("string", argument);
            writer.WriteEndElement();
            writer.WriteElementString("key", "RunAtLoad");
            writer.WriteElementString("true", null);
            writer.WriteElementString("key", "LimitLoadToSessionType");
            writer.WriteElementString("string", "Aqua");
            writer.WriteElementString("key", "ProcessType");
            writer.WriteElementString("string", "Interactive");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return builder.Append('\n').ToString();
    }

    /// <summary>
    /// 自動起動で実行するコマンドを決める。.appの実行ファイルならそのまま、dotnet host経由（開発実行）なら
    /// hostとentry assemblyの組にする。
    /// </summary>
    public static IReadOnlyList<string> ResolveProgramArguments(string processPath, string entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        string name = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entryAssemblyPath)
            ? [processPath, entryAssemblyPath]
            : [processPath];
    }
}
