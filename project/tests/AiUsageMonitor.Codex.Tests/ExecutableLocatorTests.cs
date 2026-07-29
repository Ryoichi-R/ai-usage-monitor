using System.Runtime.InteropServices;
using AiUsageMonitor.Codex.Process;

namespace AiUsageMonitor.Codex.Tests;

public sealed class ExecutableLocatorTests
{
    [Fact]
    public void NativeOverrideWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "codex.exe");
            File.WriteAllBytes(executable, []);
            Assert.Equal(Path.GetFullPath(executable), CodexExecutableLocator.Locate(executable, string.Empty, Architecture.X64));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NpmShimResolvesCurrentNativePackageManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "codex.cmd"), string.Empty);
            string packageRoot = Path.Combine(root, "node_modules", "@openai", "codex");
            Directory.CreateDirectory(packageRoot);
            File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"@openai/codex\"}");

            string nativeRoot = Path.Combine(packageRoot, "node_modules", "@openai", "codex-win32-x64");
            Directory.CreateDirectory(nativeRoot);
            File.WriteAllText(Path.Combine(nativeRoot, "package.json"), "{\"name\":\"@openai/codex\"}");
            string executable = Path.Combine(nativeRoot, "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllBytes(executable, []);

            Assert.Equal(Path.GetFullPath(executable), CodexExecutableLocator.Locate(null, root, Architecture.X64));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NpmShimRejectsUnexpectedNativePackageManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "codex.cmd"), string.Empty);
            string packageRoot = Path.Combine(root, "node_modules", "@openai", "codex");
            Directory.CreateDirectory(packageRoot);
            File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"@openai/codex\"}");

            string nativeRoot = Path.Combine(packageRoot, "node_modules", "@openai", "codex-win32-x64");
            Directory.CreateDirectory(nativeRoot);
            File.WriteAllText(Path.Combine(nativeRoot, "package.json"), "{\"name\":\"unrelated-package\"}");
            string executable = Path.Combine(nativeRoot, "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllBytes(executable, []);

            Assert.Null(CodexExecutableLocator.Locate(null, root, Architecture.X64));
        }
        finally { Directory.Delete(root, true); }
    }
}
