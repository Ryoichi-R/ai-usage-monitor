using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AiUsageMonitor.Core.Build;

/// <summary>利用者ローカルで稼働版を読み取るための、通常起動インスタンスごとの記録。</summary>
public sealed class RuntimeBuildIdentity : IDisposable
{
    private readonly string _path;

    private RuntimeBuildIdentity(string path) => _path = path;

    public static RuntimeBuildIdentity? TryPublish(string applicationDataDirectory)
    {
        string? temporary = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            ProductBuildInfo info = ProductBuildInfo.Current;
            string directory = Path.Combine(applicationDataDirectory, "runtime-build");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, process.Id.ToString(CultureInfo.InvariantCulture) + ".json");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, new
                {
                    SchemaVersion = 1,
                    ProductId = "ai-usage-monitor",
                    info.Version,
                    CommitSha = info.SourceIdentifier.Length == 40 ? info.SourceIdentifier : null,
                    info.HasUncommittedChanges,
                    ProcessId = process.Id,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                });
            }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return new RuntimeBuildIdentity(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (temporary is not null)
                TryDelete(temporary);
        }
    }

    public void Dispose() => TryDelete(_path);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
