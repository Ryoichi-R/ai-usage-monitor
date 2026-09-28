namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsDiagnosticLogTests
{
    [Fact]
    public void RecordsReasonAndExceptionTypeWithoutPrivateMessageOrInjectedLines()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "diagnostics.log");
            var log = new WindowsDiagnosticLog(path);
            log.Write("widget-layer-apply-failure", new IOException("private-fixture-message"));
            log.Write("bad\ninjected", null);
            string text = File.ReadAllText(path);
            Assert.Contains("widget-layer-apply-failure IOException:", text);
            Assert.Contains("invalid-diagnostic-code", text);
            Assert.DoesNotContain("private-fixture-message", text);
            Assert.DoesNotContain("injected", text);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RotatesWithinBoundAndSurvivesUnwritableDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "diagnostics.log");
            var log = new WindowsDiagnosticLog(path, 256);
            for (int i = 0; i < 20; i++) log.Write("widget-layer-apply-failure", null);
            Assert.InRange(new FileInfo(path).Length, 1, 256);
            Assert.InRange(new FileInfo(path + ".1").Length, 1, 256);
            Assert.Equal(2, Directory.GetFiles(root).Length);
            var blocked = new WindowsDiagnosticLog(Path.Combine(path, "blocked.log"));
            blocked.Write("widget-layer-apply-failure", new IOException("fixture"));
        }
        finally { Directory.Delete(root, true); }
    }
}
