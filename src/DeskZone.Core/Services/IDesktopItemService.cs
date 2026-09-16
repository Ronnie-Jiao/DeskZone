using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public interface IDesktopItemService
{
    Task<IReadOnlyList<DesktopItem>> ListByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<AddReferencesResult> AddReferencesAsync(Guid categoryId, IEnumerable<string> paths, CancellationToken cancellationToken = default);
    Task MoveToCategoryAsync(IReadOnlyCollection<Guid> itemIds, Guid targetCategoryId, CancellationToken cancellationToken = default);
    Task RemoveReferencesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
    Task<int> RefreshMissingStateAsync(Guid categoryId, CancellationToken cancellationToken = default);
}
