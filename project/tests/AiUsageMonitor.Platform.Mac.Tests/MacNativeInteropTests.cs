using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace AiUsageMonitor.Platform.Mac.Tests;

/// <summary>NSWindowを要しない範囲で、実OSのinteropとprocess境界の失敗分岐を検証する。</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacNativeInteropTests
{
    [Fact]
    public void ShellOpenerPassesThePathAsOneArgumentToOpen()
    {
        ProcessStartInfo? captured = null;
        var opener = new MacShellOpener(info => { captured = info; return null; });
        opener.OpenFolder("/tmp/a folder; rm -rf ~");
        Assert.NotNull(captured);
        Assert.Equal("/usr/bin/open", captured.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.Equal(["/tmp/a folder; rm -rf ~"], captured.ArgumentList);
    }

    [Fact]
    public void DefaultPathsResolveUnderTheCurrentUserHome()
    {
        var paths = new MacAppPathProvider();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), paths.UserHomeDirectory);
        Assert.StartsWith(Path.Combine(paths.UserHomeDirectory, "Library", "Application Support", MacAppPathProvider.AppDirectoryName), paths.ApplicationSupportDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void MainDisplayHasAStableColorSyncUuid()
    {
        uint display = CGMainDisplayID();
        Assert.NotEqual(0u, display);
        string? id = MacDisplayIdentity.TryGetStableId((nint)display, MacDisplayIdentity.PlatformHandleDescriptor);
        Assert.NotNull(id);
        Assert.Matches(UuidPattern(), id);
        Assert.Equal(id, MacDisplayIdentity.TryGetStableId((nint)display, MacDisplayIdentity.PlatformHandleDescriptor));
    }

    [Fact]
    public void ObjCLayerApiRejectsNonWindowObjectsAndOrdersLevels()
    {
        ObjCWindowLayerApi api = ObjCWindowLayerApi.Instance;
        Assert.Equal(0, api.ResolveWindow(0));
        nint instance = objc_msgSend(objc_getClass("NSObject\0"u8.ToArray()), sel_registerName("new\0"u8.ToArray()));
        try { Assert.Equal(0, api.ResolveWindow(instance)); }
        finally { objc_msgSend(instance, sel_registerName("release\0"u8.ToArray())); }
        long bottom = api.LevelFor(WidgetLayerMode.AlwaysOnBottom);
        long normal = api.LevelFor(WidgetLayerMode.Normal);
        long top = api.LevelFor(WidgetLayerMode.AlwaysOnTop);
        Assert.True(bottom < normal && normal < top, $"{bottom} < {normal} < {top}");
    }

    [Theory]
    [InlineData("F")]
    [InlineData("X")]
    [InlineData("")]
    public async Task MissingOrUnknownCleanupReportIsSupervisionFailure(string reply)
    {
        string root = NewRoot();
        try
        {
            string helper = FakeHelper(root, reply);
            var launcher = new MacManagedProcessLauncher(helper, Path.Combine(root, "journal"));
            var session = await launcher.StartAsync(new ProcessStartInfo("/usr/bin/true"), CancellationToken.None);
            await session.DisposeAsync();
            Assert.Equal(ManagedProcessOutcome.SupervisionFailed, session.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HelperThatIsNotReadyAndFailedSweepAreRejected()
    {
        string root = NewRoot();
        try
        {
            var notReady = new MacManagedProcessLauncher(FakeHelper(root, "C", ready: "Z"), Path.Combine(root, "journal"));
            Assert.Equal("PROCESS_SUPERVISOR_NOT_READY", (await Assert.ThrowsAsync<IOException>(() =>
                notReady.StartAsync(new ProcessStartInfo("/usr/bin/true"), CancellationToken.None))).Message);
            string journal = Path.Combine(root, "failing-journal");
            Directory.CreateDirectory(Path.Combine(journal, Guid.NewGuid().ToString("N")));
            var failing = new MacManagedProcessLauncher(FakeHelper(root, "C", sweepExit: 74), journal);
            Assert.Equal("PROCESS_SWEEP_FAILED", (await Assert.ThrowsAsync<IOException>(() => failing.SweepAsync())).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    // A scripted supervisor: it follows the control protocol only, spawns nothing and kills nothing.
    private static string FakeHelper(string root, string reply, string ready = "R", int sweepExit = 0)
    {
        string path = Path.Combine(root, "fake-supervisor-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(path, $$"""
            #!/usr/bin/python3
            import socket, sys
            if sys.argv[1] == "--sweep":
                sys.exit({{sweepExit}})
            control = socket.socket(socket.AF_UNIX)
            control.connect(sys.argv[3])
            control.sendall(b"{{ready}}")
            control.recv(1)
            control.sendall(b"{{reply}}")
            control.close()
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string NewRoot()
    {
        string root = Path.Combine("/private/tmp", "aiusage-interop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    [GeneratedRegex("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$")]
    private static partial Regex UuidPattern();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(byte[] nullTerminatedUtf8);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(byte[] nullTerminatedUtf8);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_msgSend(nint receiver, nint selector);
}
