using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Win32;

namespace AiUsageMonitor.Windows.Startup;

public static class StartupRegistryService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Retain the legacy value name so startup updates the existing entry instead
    // of creating a duplicate launcher after the product rename.
    private const string ValueName = "CodexUsageMonitor";

    [ExcludeFromCodeCoverage(
        Justification = "Windows registry mutation boundary; command construction is covered separately.")]
    public static void Apply(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            string processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("現在の実行ファイルを特定できないため、自動起動を設定できません。");
            string? entryAssemblyPath = Assembly.GetEntryAssembly()?.GetName().Name is { } entryName
                ? Path.Combine(AppContext.BaseDirectory, entryName + ".dll")
                : null;
            key.SetValue(ValueName, CreateCommand(processPath, entryAssemblyPath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static string CreateCommand(string processPath, string? entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        string command = Quote(processPath);
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            command += " " + Quote(entryAssemblyPath);
        }

        return command;
    }

    private static string Quote(string path)
    {
        if (path.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException("起動パスに二重引用符を含めることはできません。", nameof(path));
        return $"\"{path}\"";
    }
}
