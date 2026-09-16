using System.Text.Json;
using DeskZone.Core.Services;

namespace DeskZone.Storage;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonSettingsStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskZone");
    }

    public async Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken);
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootDirectory);
        var path = GetPath(key);
        var temp = path + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    private string GetPath(string key)
    {
        var safe = string.Concat(key.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(_rootDirectory, safe + ".json");
    }
}
