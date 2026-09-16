using System.Text.Json;
using DeskZone.Core.Contracts;
using DeskZone.Core.Exceptions;
using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public sealed class DesktopItemService : IDesktopItemService
{
    private readonly IWorkspaceStore _store;

    public DesktopItemService(IWorkspaceStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<DesktopItem>> ListByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        _ = await RequireCategoryAsync(categoryId, cancellationToken);
        return await _store.GetItemsByCategoryAsync(categoryId, cancellationToken);
    }

    public async Task<AddReferencesResult> AddReferencesAsync(Guid categoryId, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        _ = await RequireCategoryAsync(categoryId, cancellationToken);

        var failures = new List<AddReferenceFailure>();
        var newItems = new List<DesktopItem>();
        var moved = 0;
        var already = 0;
        var nextOrder = await _store.GetNextItemOrderAsync(categoryId, cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath;
            try
            {
                fullPath = NormalizePath(rawPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add(new AddReferenceFailure(rawPath, "路径格式无效。"));
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            var type = GetItemType(fullPath);
            if (type is null)
            {
                failures.Add(new AddReferenceFailure(fullPath, "文件、文件夹或快捷方式不存在。"));
                continue;
            }

            var existing = await _store.FindItemByOriginalPathAsync(fullPath, DesktopItemMode.Reference, cancellationToken);
            if (existing is not null)
            {
                if (existing.CategoryId == categoryId)
                {
                    already++;
                }
                else
                {
                    await _store.MoveItemsAsync(new[] { existing.Id }, categoryId, cancellationToken);
                    await LogAsync(
                        OperationType.MoveBetweenCategories,
                        existing.OriginalPath,
                        null,
                        new { itemId = existing.Id, fromCategoryId = existing.CategoryId, toCategoryId = categoryId },
                        canUndo: true,
                        cancellationToken);
                    moved++;
                }
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            newItems.Add(new DesktopItem(
                Guid.NewGuid(),
                categoryId,
                DesktopItemMode.Reference,
                fullPath,
                null,
                GetDisplayName(fullPath),
                type.Value,
                nextOrder++,
                false,
                now,
                now));
        }

        if (newItems.Count > 0)
        {
            await _store.InsertItemsAsync(newItems, cancellationToken);
            foreach (var item in newItems)
            {
                await LogAsync(
                    OperationType.AddReference,
                    item.OriginalPath,
                    null,
                    new { itemId = item.Id, categoryId = item.CategoryId },
                    canUndo: true,
                    cancellationToken);
            }
        }

        return new AddReferencesResult(newItems.Count, moved, already, failures);
    }

    public async Task MoveToCategoryAsync(IReadOnlyCollection<Guid> itemIds, Guid targetCategoryId, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        _ = await RequireCategoryAsync(targetCategoryId, cancellationToken);
        var uniqueIds = itemIds.Distinct().ToArray();
        var before = new List<DesktopItem>(uniqueIds.Length);
        foreach (var id in uniqueIds)
        {
            var item = await _store.GetItemAsync(id, cancellationToken)
                ?? throw new DeskZoneValidationException("要移动的文件入口不存在或已被移除。");
            before.Add(item);
        }

        await _store.MoveItemsAsync(uniqueIds, targetCategoryId, cancellationToken);
        foreach (var item in before.Where(x => x.CategoryId != targetCategoryId))
        {
            await LogAsync(
                OperationType.MoveBetweenCategories,
                item.ActivePath,
                null,
                new { itemId = item.Id, fromCategoryId = item.CategoryId, toCategoryId = targetCategoryId },
                canUndo: true,
                cancellationToken);
        }
    }

    public async Task RemoveReferencesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var uniqueIds = itemIds.Distinct().ToArray();
        var items = new List<DesktopItem>(uniqueIds.Length);
        foreach (var id in uniqueIds)
        {
            var item = await _store.GetItemAsync(id, cancellationToken);
            if (item is null)
            {
                continue;
            }

            if (item.ItemMode == DesktopItemMode.Managed)
            {
                throw new DeskZoneValidationException("收纳模式文件不能直接作为普通引用移除；请先执行安全解除收纳流程。");
            }
            items.Add(item);
        }

        await _store.RemoveItemsAsync(items.Select(x => x.Id).ToArray(), cancellationToken);
        foreach (var item in items)
        {
            await LogAsync(
                OperationType.RemoveReference,
                item.OriginalPath,
                null,
                new { itemId = item.Id, categoryId = item.CategoryId, snapshot = item },
                canUndo: true,
                cancellationToken);
        }
    }

    public async Task<int> RefreshMissingStateAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var items = await _store.GetItemsByCategoryAsync(categoryId, cancellationToken);
        var changed = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = File.Exists(item.ActivePath) || Directory.Exists(item.ActivePath);
            if (item.IsMissing == !exists)
            {
                continue;
            }

            await _store.SetItemMissingStateAsync(item.Id, !exists, now, cancellationToken);
            changed++;
        }
        return changed;
    }

    private async Task<Category> RequireCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        await _store.GetCategoryAsync(categoryId, cancellationToken)
        ?? throw new DeskZoneValidationException("目标分类不存在或已删除。");

    private async Task LogAsync(
        OperationType type,
        string? sourcePath,
        string? destinationPath,
        object metadata,
        bool canUndo,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _store.AppendOperationAsync(new OperationLog(
            Guid.NewGuid(),
            type,
            sourcePath,
            destinationPath,
            JsonSerializer.Serialize(metadata),
            OperationStatus.Succeeded,
            canUndo,
            now,
            now), cancellationToken);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is empty.", nameof(path));
        }
        return Path.GetFullPath(path.Trim().Trim('"'));
    }

    private static DesktopItemType? GetItemType(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return DesktopItemType.Folder;
        }
        if (!File.Exists(fullPath))
        {
            return null;
        }
        return string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? DesktopItemType.Shortcut
            : DesktopItemType.File;
    }

    private static string GetDisplayName(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
