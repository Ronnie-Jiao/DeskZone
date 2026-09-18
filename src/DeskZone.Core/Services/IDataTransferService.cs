namespace DeskZone.Core.Services;

public interface IDataTransferService
{
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);

    Task ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
}
