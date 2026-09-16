using DeskZone.Core.Models;

namespace DeskZone.Core.Contracts;

public interface IWorkspaceStore
{
    Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeSystem = false, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<Category?> GetSystemCategoryAsync(string systemKey, CancellationToken cancellationToken = default);
    Task<int> GetNextCategoryOrderAsync(CancellationToken cancellationToken = default);
    Task InsertCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(
        Guid categoryId,
        CategoryDeleteMode mode,
        Guid? targetCategoryId,
        Guid unclassifiedCategoryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DesktopItem>> GetItemsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<DesktopItem?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default);
    Task<DesktopItem?> FindItemByOriginalPathAsync(string originalPath, DesktopItemMode mode, CancellationToken cancellationToken = default);
    Task<int> GetNextItemOrderAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task InsertItemsAsync(IReadOnlyCollection<DesktopItem> items, CancellationToken cancellationToken = default);
    Task MoveItemsAsync(IReadOnlyCollection<Guid> itemIds, Guid targetCategoryId, CancellationToken cancellationToken = default);
    Task RemoveItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
    Task SetItemMissingStateAsync(Guid itemId, bool isMissing, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);

    Task<DesktopPanel?> GetPanelAsync(Guid panelId, CancellationToken cancellationToken = default);
    Task UpsertPanelAsync(DesktopPanel panel, CancellationToken cancellationToken = default);

    Task AppendOperationAsync(OperationLog operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationLog>> GetRecentOperationsAsync(int limit, CancellationToken cancellationToken = default);
}
