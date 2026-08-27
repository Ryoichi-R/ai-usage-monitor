namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsExecutableTrustVerifierTests
{
    [Fact]
    public void RejectsAnUntrustedNonAnthropicSignedExecutable()
    {
        string executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var verifier = new WindowsExecutableTrustVerifier();

        ExecutableTrustResult result = verifier.Verify(executable);

        Assert.False(result.Valid);
        Assert.Equal("UNTRUSTED_EXECUTABLE", result.FailureReason);
    }

    [Fact]
    public void RejectsAMissingFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe");
        var verifier = new WindowsExecutableTrustVerifier();

        ExecutableTrustResult result = verifier.Verify(missing);

        Assert.False(result.Valid);
        Assert.Null(result.Publisher);
        Assert.Equal("UNTRUSTED_EXECUTABLE", result.FailureReason);
    }
}
