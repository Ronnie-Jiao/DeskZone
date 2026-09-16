namespace DeskZone.Core.Services;

public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
