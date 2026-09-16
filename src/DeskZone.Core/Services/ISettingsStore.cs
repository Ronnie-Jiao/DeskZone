namespace DeskZone.Core.Services;

public interface ISettingsStore
{
    Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}
