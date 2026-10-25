namespace DeskZone.Core.Services;

public sealed record CategoryFolderExportResult(
    string OutputDirectory,
    int ExportedCategoryCount,
    int ExportedItemCount);

public interface ICategoryFolderExportService
{
    Task<CategoryFolderExportResult> ExportAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
