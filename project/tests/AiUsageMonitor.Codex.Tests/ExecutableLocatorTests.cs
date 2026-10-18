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
            Assert.Equal(Path.GetFullPath(executable), LocateWindows(executable, string.Empty));
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

            Assert.Equal(Path.GetFullPath(executable), LocateWindows(null, root));
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

            Assert.Null(LocateWindows(null, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PublicLocateUsesTheCurrentPlatformRules()
    {
        string root = NewRoot();
        try
        {
            string executable = Path.Combine(root, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            WriteMachO(executable);
            Assert.Equal(Path.GetFullPath(executable), CodexExecutableLocator.Locate(executable, string.Empty, Architecture.Arm64));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MacAcceptsANativeMachOOnPathAndRejectsScripts()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string scriptDirectory = Path.Combine(root, "scripts");
            string nativeDirectory = Path.Combine(root, "native");
            Directory.CreateDirectory(scriptDirectory);
            Directory.CreateDirectory(nativeDirectory);
            string script = Path.Combine(scriptDirectory, "codex");
            File.WriteAllText(script, "#!/bin/sh\nexec codex \"$@\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string native = Path.Combine(nativeDirectory, "codex");
            WriteMachO(native);

            string path = scriptDirectory + Path.PathSeparator + nativeDirectory;
            Assert.Equal(native, LocateMac(null, path, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MacRejectsAMachOWithoutExecutePermission()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string native = Path.Combine(root, "codex");
            WriteMachO(native);
            File.SetUnixFileMode(native, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Null(LocateMac(native, string.Empty, root));
            Assert.False(CodexExecutableLocator.IsMachOExecutable(native));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBF }, true)]
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62 }, false)]
    [InlineData(new byte[] { 0xCF, 0xFA }, false)]
    public void MachOHeaderDetection(byte[] header, bool expected)
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string file = Path.Combine(root, "candidate");
            File.WriteAllBytes(file, header);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal(expected, CodexExecutableLocator.IsMachOExecutable(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MacFollowsTheNpmShimSymlinkToTheDarwinNativePackage()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string packageRoot = Path.Combine(root, "lib", "node_modules", "@openai", "codex");
            Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
            File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"@openai/codex\"}");
            string shim = Path.Combine(packageRoot, "bin", "codex.js");
            File.WriteAllText(shim, "#!/usr/bin/env node\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string nativeRoot = Path.Combine(packageRoot, "node_modules", "@openai", "codex-darwin-arm64");
            Directory.CreateDirectory(nativeRoot);
            File.WriteAllText(Path.Combine(nativeRoot, "package.json"), "{\"name\":\"@openai/codex\"}");
            string native = Path.Combine(nativeRoot, "vendor", "aarch64-apple-darwin", "codex", "codex");
            WriteMachO(native);
            string bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.CreateSymbolicLink(Path.Combine(bin, "codex"), Path.Combine("..", "lib", "node_modules", "@openai", "codex", "bin", "codex.js"));

            Assert.Equal(Path.GetFullPath(native), LocateMac(null, bin, root));
            Assert.Null(CodexExecutableLocator.Locate(null, bin, Architecture.X86, CodexLocatorPlatform.MacOS, root, null, null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MacNpmShimRejectsAnUnrelatedPackage()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string packageRoot = Path.Combine(root, "pkg");
            Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
            File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\":\"not-codex\"}");
            string shim = Path.Combine(packageRoot, "bin", "codex");
            File.WriteAllText(shim, "#!/usr/bin/env node\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string native = Path.Combine(root, "codex-darwin-arm64", "vendor", "aarch64-apple-darwin", "bin", "codex");
            WriteMachO(native);
            File.WriteAllText(Path.Combine(root, "codex-darwin-arm64", "package.json"), "{\"name\":\"@openai/codex\"}");

            Assert.Null(LocateMac(null, Path.Combine(packageRoot, "bin"), root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MacFallsBackToWellKnownDirectoriesAndTheChatGptBundle()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        try
        {
            string applications = Path.Combine(root, "Applications");
            string bundled = Path.Combine(applications, "ChatGPT.app", "Contents", "Resources", "codex-cli", "CodexCLI.app", "Contents", "MacOS", "codex");
            WriteMachO(bundled);
            string home = Path.Combine(root, "home");
            Assert.Equal(bundled, CodexExecutableLocator.Locate(null, string.Empty, Architecture.Arm64, CodexLocatorPlatform.MacOS, home, applications, []));

            string userBundled = Path.Combine(home, "Applications", "ChatGPT.app", "Contents", "Resources", "codex-cli", "CodexCLI.app", "Contents", "MacOS", "codex");
            WriteMachO(userBundled);
            Assert.Equal(userBundled, CodexExecutableLocator.Locate(null, string.Empty, Architecture.Arm64, CodexLocatorPlatform.MacOS, home, null, []));

            string wellKnown = Path.Combine(root, "brew");
            WriteMachO(Path.Combine(wellKnown, "codex"));
            Assert.Equal(Path.Combine(wellKnown, "codex"), CodexExecutableLocator.Locate(null, string.Empty, Architecture.Arm64, CodexLocatorPlatform.MacOS, home, applications, [wellKnown]));

            Assert.Null(CodexExecutableLocator.Locate(null, string.Empty, Architecture.Arm64, CodexLocatorPlatform.MacOS, null, null, null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("/Volumes/share/codex", "/Volumes/share", true)]
    [InlineData("/Volumes/share", "/Volumes/share", true)]
    [InlineData("/Volumes/shared/codex", "/Volumes/share", false)]
    [InlineData("/usr/bin/codex", "/", true)]
    public void MountPrefixMatchingRespectsPathBoundaries(string path, string mount, bool expected) =>
        Assert.Equal(expected, CodexExecutableLocator.IsUnderMount(path, mount));

    private static string? LocateWindows(string? overridePath, string pathEnvironment) =>
        CodexExecutableLocator.Locate(overridePath, pathEnvironment, Architecture.X64, CodexLocatorPlatform.Windows, null, null, null);

    private static string? LocateMac(string? overridePath, string pathEnvironment, string root) =>
        CodexExecutableLocator.Locate(overridePath, pathEnvironment, Architecture.Arm64, CodexLocatorPlatform.MacOS, Path.Combine(root, "no-home"), null, []);

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteMachO(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
