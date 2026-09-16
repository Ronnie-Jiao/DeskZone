using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public interface ICategoryService
{
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default);
    Task<Category> CreateAsync(string name, string? color = null, CancellationToken cancellationToken = default);
    Task<Category> RenameAsync(Guid categoryId, string name, CancellationToken cancellationToken = default);
    Task SetCollapsedAsync(Guid categoryId, bool isCollapsed, CancellationToken cancellationToken = default);
    Task ReorderAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid categoryId,
        CategoryDeleteMode mode = CategoryDeleteMode.RejectIfNotEmpty,
        Guid? targetCategoryId = null,
        CancellationToken cancellationToken = default);
}
