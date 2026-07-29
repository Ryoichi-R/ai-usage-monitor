using System.Text.Json;

namespace AiUsageMonitor.Core.Settings;

public sealed class FileSystemSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string Path { get; } = path;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return new AppSettings();
        try { return await ReadAsync(Path, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) when (File.Exists(Path + ".bak")) { return await ReadAsync(Path + ".bak", cancellationToken).ConfigureAwait(false); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = Path + ".tmp";
        FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings.Normalized(), JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak", true);
        else File.Move(temporary, Path);
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
