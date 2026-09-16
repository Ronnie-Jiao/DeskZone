using DeskZone.Core.Contracts;
using DeskZone.Core.Exceptions;
using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public sealed class CategoryService : ICategoryService
{
    private const int MaxNameLength = 80;
    private readonly IWorkspaceStore _store;

    public CategoryService(IWorkspaceStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.GetCategoriesAsync(includeSystem: false, cancellationToken);

    public async Task<Category> CreateAsync(string name, string? color = null, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var now = DateTimeOffset.UtcNow;
        var category = new Category(
            Guid.NewGuid(),
            normalizedName,
            await _store.GetNextCategoryOrderAsync(cancellationToken),
            "grid",
            string.IsNullOrWhiteSpace(color) ? "#126FF7" : color.Trim(),
            false,
            "custom",
            null,
            now,
            now);

        await _store.InsertCategoryAsync(category, cancellationToken);
        return category;
    }

    public async Task<Category> RenameAsync(Guid categoryId, string name, CancellationToken cancellationToken = default)
    {
        var category = await RequireUserCategoryAsync(categoryId, cancellationToken);
        var updated = category with { Name = NormalizeName(name), UpdatedAt = DateTimeOffset.UtcNow };
        await _store.UpdateCategoryAsync(updated, cancellationToken);
        return updated;
    }

    public async Task SetCollapsedAsync(Guid categoryId, bool isCollapsed, CancellationToken cancellationToken = default)
    {
        var category = await RequireUserCategoryAsync(categoryId, cancellationToken);
        await _store.UpdateCategoryAsync(category with
        {
            IsCollapsed = isCollapsed,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken cancellationToken = default)
    {
        if (orderedCategoryIds.Count == 0)
        {
            return;
        }

        if (orderedCategoryIds.Distinct().Count() != orderedCategoryIds.Count)
        {
            throw new DeskZoneValidationException("分类排序列表中不能包含重复 ID。");
        }

        var current = await _store.GetCategoriesAsync(includeSystem: false, cancellationToken);
        var currentIds = current.Select(x => x.Id).OrderBy(x => x).ToArray();
        var requestedIds = orderedCategoryIds.OrderBy(x => x).ToArray();
        if (!currentIds.SequenceEqual(requestedIds))
        {
            throw new DeskZoneValidationException("分类排序必须包含全部可见分类，且不能包含未知分类。");
        }

        await _store.ReorderCategoriesAsync(orderedCategoryIds, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid categoryId,
        CategoryDeleteMode mode = CategoryDeleteMode.RejectIfNotEmpty,
        Guid? targetCategoryId = null,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireUserCategoryAsync(categoryId, cancellationToken);

        if (mode == CategoryDeleteMode.MoveItemsToCategory)
        {
            if (!targetCategoryId.HasValue || targetCategoryId.Value == categoryId)
            {
                throw new DeskZoneValidationException("删除非空分类时必须选择另一个目标分类。");
            }

            _ = await RequireUserCategoryAsync(targetCategoryId.Value, cancellationToken);
        }

        var unclassifiedCategoryId = Guid.Empty;
        if (mode == CategoryDeleteMode.MoveItemsToUnclassified)
        {
            unclassifiedCategoryId = (await EnsureUnclassifiedCategoryAsync(cancellationToken)).Id;
        }

        await _store.DeleteCategoryAsync(categoryId, mode, targetCategoryId, unclassifiedCategoryId, cancellationToken);
    }

    private async Task<Category> RequireUserCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await _store.GetCategoryAsync(categoryId, cancellationToken)
            ?? throw new DeskZoneValidationException("分类不存在或已被删除。");

        if (category.IsSystem)
        {
            throw new DeskZoneValidationException("系统分类不能通过普通分类操作修改。");
        }

        return category;
    }

    private async Task<Category> EnsureUnclassifiedCategoryAsync(CancellationToken cancellationToken)
    {
        var existing = await _store.GetSystemCategoryAsync(SystemCategoryKeys.Unclassified, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var category = new Category(
            Guid.NewGuid(),
            "未分类",
            int.MaxValue,
            "grid",
            "#7F8C9A",
            true,
            "custom",
            SystemCategoryKeys.Unclassified,
            now,
            now);
        await _store.InsertCategoryAsync(category, cancellationToken);
        return category;
    }

    private static string NormalizeName(string name)
    {
        var result = (name ?? string.Empty).Trim();
        if (result.Length == 0)
        {
            throw new DeskZoneValidationException("分类名称不能为空。");
        }

        if (result.Length > MaxNameLength)
        {
            throw new DeskZoneValidationException($"分类名称不能超过 {MaxNameLength} 个字符。");
        }

        return result;
    }
}
