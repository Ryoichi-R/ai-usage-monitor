using System.Text.Json;

namespace AiUsageMonitor.Core.Settings;

public sealed class FileSystemSettingsStore(string path) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public string Path { get; } = path;

    public void Dispose() => _saveGate.Dispose();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return await ReadAsync(Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            if (File.Exists(Path + ".bak"))
            {
                try
                {
                    return await ReadAsync(Path + ".bak", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception backupException) when (
                    backupException is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }
            // 本体・バックアップの双方が読めない場合でも起動を継続できるよう既定値へ
            // fallbackする。ここで例外を伝播すると呼び出し元（App.OnStartup）を
            // クラッシュさせかねない。
            TryArchiveCorrupted();
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        // 位置保存・設定ダイアログ保存・初回起動ウィザードなど複数経路から並行して
        // 呼ばれうる。直列化しないと同時書き込みで内容を取り違えたり、一時fileの
        // 奪い合いで失敗しうる。
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, settings.Normalized(), JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak", true);
                else File.Move(temporary, Path);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                }
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void TryArchiveCorrupted()
    {
        try
        {
            string corrupted = Path + ".corrupt";
            if (File.Exists(corrupted)) File.Delete(corrupted);
            File.Move(Path, corrupted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<AppSettings> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return (await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings()).Normalized();
        }
    }
}
