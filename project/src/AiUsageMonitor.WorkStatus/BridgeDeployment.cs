using System.Security.Cryptography;
using System.Text;

namespace AiUsageMonitor.WorkStatus;

public static class BridgeDeployment
{
    public static string Prepare(string packageDirectory, string managedDirectory)
    {
        string binary = OperatingSystem.IsWindows() ? "ai-usage-work-status-bridge.exe" : "ai-usage-work-status-bridge";
        string executable = WorkStatusPaths.Inside(packageDirectory, Path.Combine(packageDirectory, binary));
        if (!File.Exists(executable)) throw new IOException("WORK_STATUS_BRIDGE_MISSING");
        // Keep the Mac .app's signing and quarantine boundary; moving it needs reconnection.
        if (OperatingSystem.IsMacOS()) return executable;
        string[] files = [.. Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            WorkStatusPaths.Inside(packageDirectory, file);
            digest.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(packageDirectory, file)));
            digest.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }
        string build = Convert.ToHexString(digest.GetHashAndReset())[..24];
        string final = WorkStatusPaths.Inside(managedDirectory, Path.Combine(managedDirectory, "v1", build));
        string result = Path.Combine(final, binary);
        if (File.Exists(Path.Combine(final, ".complete")))
        {
            foreach (string file in files)
            {
                string existing = WorkStatusPaths.Inside(final, Path.Combine(final, Path.GetRelativePath(packageDirectory, file)));
                if (!File.Exists(existing) || !SHA256.HashData(File.ReadAllBytes(file)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(existing))))
                    throw new IOException("WORK_STATUS_BRIDGE_IDENTITY_MISMATCH");
            }
            return WorkStatusPaths.Inside(managedDirectory, result);
        }
        string staging = WorkStatusPaths.Inside(managedDirectory, final + ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (string file in files)
        {
            string destination = WorkStatusPaths.Inside(staging, Path.Combine(staging, Path.GetRelativePath(packageDirectory, file)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
            if (!SHA256.HashData(File.ReadAllBytes(file)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(destination))))
                throw new IOException("WORK_STATUS_BRIDGE_COPY_MISMATCH");
        }
        File.WriteAllText(Path.Combine(staging, ".complete"), build);
        Directory.Move(staging, final);
        return result;
    }
}
