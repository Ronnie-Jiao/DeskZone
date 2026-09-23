using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public interface IDesktopItemService
{
    Task<IReadOnlyList<DesktopItem>> ListByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecentOpenedItem>> ListRecentlyOpenedAsync(int maximumCount = 10, CancellationToken cancellationToken = default);
    Task RecordOpenedAsync(DesktopItem item, CancellationToken cancellationToken = default);
    Task<DesktopItem> RenameDisplayNameAsync(DesktopItem item, string displayName, CancellationToken cancellationToken = default);
    Task<AddReferencesResult> AddReferencesAsync(Guid categoryId, IEnumerable<string> paths, CancellationToken cancellationToken = default);
    Task<MoveItemsResult> MovePathsIntoCategoryAsync(Guid categoryId, IEnumerable<string> paths, CancellationToken cancellationToken = default);
    Task MoveItemToDirectoryAsync(Guid itemId, string destinationDirectory, CancellationToken cancellationToken = default);
    Task MoveToCategoryAsync(IReadOnlyCollection<Guid> itemIds, Guid targetCategoryId, CancellationToken cancellationToken = default);
    Task ReorderAsync(Guid categoryId, IReadOnlyList<Guid> orderedItemIds, CancellationToken cancellationToken = default);
    Task RemoveReferencesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
    Task<int> RefreshMissingStateAsync(Guid categoryId, CancellationToken cancellationToken = default);
}
