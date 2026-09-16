namespace DeskZone.Core.Services;

public interface IShellService
{
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
    Task RevealInExplorerAsync(string path, CancellationToken cancellationToken = default);
}
