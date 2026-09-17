using System.Text.Json;
using DeskZone.Core.Contracts;
using DeskZone.Core.Exceptions;
using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public sealed class DesktopItemService : IDesktopItemService
{
    private readonly IWorkspaceStore _store;
    private readonly string _managedStorageDirectory;

    public DesktopItemService(IWorkspaceStore store, string managedStorageDirectory)
    {
        _store = store;
        _managedStorageDirectory = Path.GetFullPath(managedStorageDirectory);
    }

    public async Task<IReadOnlyList<DesktopItem>> ListByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        _ = await RequireCategoryAsync(categoryId, cancellationToken);
        return await _store.GetItemsByCategoryAsync(categoryId, cancellationToken);
    }

    public Task<IReadOnlyList<RecentOpenedItem>> ListRecentlyOpenedAsync(
        int maximumCount = 10,
        CancellationToken cancellationToken = default) =>
        _store.GetRecentlyOpenedItemsAsync(Math.Clamp(maximumCount, 1, 10), cancellationToken);

    public Task RecordOpenedAsync(DesktopItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _store.RecordRecentlyOpenedItemAsync(
            new RecentOpenedItem(item.Id, item.ActivePath, item.DisplayName, item.ItemType, DateTimeOffset.UtcNow),
            cancellationToken);
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

    public async Task ReorderAsync(
        Guid categoryId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireCategoryAsync(categoryId, cancellationToken);

        if (orderedItemIds.Count == 0)
        {
            return;
        }

        var orderedIds = orderedItemIds.ToArray();
        if (orderedIds.Distinct().Count() != orderedIds.Length)
        {
            throw new DeskZoneValidationException("项目排序失败：排序列表中包含重复项目。");
        }

        var items = await _store.GetItemsByCategoryAsync(categoryId, cancellationToken);
        var existingIds = items.Select(item => item.Id).ToHashSet();
        if (existingIds.Count != orderedIds.Length || orderedIds.Any(itemId => !existingIds.Contains(itemId)))
        {
            throw new DeskZoneValidationException("项目排序失败：分类内容已发生变化，请重试。");
        }

        await _store.ReorderItemsAsync(categoryId, orderedIds, cancellationToken);
    }

    public async Task<MoveItemsResult> MovePathsIntoCategoryAsync(
        Guid categoryId,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireCategoryAsync(categoryId, cancellationToken);

        var failures = new List<AddReferenceFailure>();
        var moved = 0;
        var nextOrder = await _store.GetNextItemOrderAsync(categoryId, cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetDirectory = Path.Combine(_managedStorageDirectory, categoryId.ToString("N"));

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

            var itemType = GetItemType(fullPath);
            if (itemType is null)
            {
                failures.Add(new AddReferenceFailure(fullPath, "文件、文件夹或快捷方式不存在。"));
                continue;
            }

            if (IsInsideDirectory(fullPath, _managedStorageDirectory))
            {
                failures.Add(new AddReferenceFailure(fullPath, "该项目已经位于 DeskZone 的收纳目录中。"));
                continue;
            }

            var displayName = GetDisplayName(fullPath);
            var existing = await _store.FindItemByOriginalPathAsync(fullPath, DesktopItemMode.Reference, cancellationToken);
            var targetPath = string.Empty;
            var movedOnDisk = false;

            try
            {
                Directory.CreateDirectory(targetDirectory);
                targetPath = GetUniqueDestinationPath(targetDirectory, displayName, itemType.Value == DesktopItemType.Folder);
                MovePath(fullPath, targetPath);
                movedOnDisk = true;

                var now = DateTimeOffset.UtcNow;
                if (existing is null)
                {
                    await _store.InsertItemsAsync(
                        new[]
                        {
                            new DesktopItem(
                                Guid.NewGuid(),
                                categoryId,
                                DesktopItemMode.Managed,
                                fullPath,
                                targetPath,
                                Path.GetFileName(Path.TrimEndingDirectorySeparator(targetPath)),
                                itemType.Value,
                                nextOrder++,
                                false,
                                now,
                                now)
                        },
                        cancellationToken);
                }
                else
                {
                    var updated = existing with
                    {
                        CategoryId = categoryId,
                        ItemMode = DesktopItemMode.Managed,
                        ManagedPath = targetPath,
                        DisplayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetPath)),
                        ItemType = itemType.Value,
                        CustomOrder = existing.CategoryId == categoryId ? existing.CustomOrder : nextOrder++,
                        IsMissing = false,
                        UpdatedAt = now
                    };
                    await _store.UpdateItemAsync(updated, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (movedOnDisk)
                {
                    TryMovePath(targetPath, fullPath);
                }

                failures.Add(new AddReferenceFailure(fullPath, ex.Message));
                continue;
            }

            try
            {
                await LogAsync(
                    OperationType.ManagedMove,
                    fullPath,
                    targetPath,
                    new { categoryId, itemType },
                    canUndo: true,
                    cancellationToken);
            }
            catch
            {
                // The file and database state are already consistent; logging must not undo the move.
            }

            moved++;
        }

        return new MoveItemsResult(moved, failures);
    }

    public async Task MoveItemToDirectoryAsync(
        Guid itemId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var item = await _store.GetItemAsync(itemId, cancellationToken)
            ?? throw new DeskZoneValidationException("要移动的文件入口不存在或已被移除。");

        var sourcePath = NormalizePath(item.ActivePath);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        var sourceName = GetDisplayName(sourcePath);
        var targetPath = Path.Combine(destinationRoot, sourceName);
        var sourceExists = PathExists(sourcePath);
        var samePath = PathsEqual(sourcePath, targetPath);
        if (sourceExists && !samePath && PathExists(targetPath))
        {
            targetPath = GetUniqueDestinationPath(destinationRoot, sourceName, item.ItemType == DesktopItemType.Folder);
        }

        var movedOnDisk = false;
        try
        {
            if (sourceExists && !samePath)
            {
                MovePath(sourcePath, targetPath);
                movedOnDisk = true;
            }
            else if (!sourceExists && !PathExists(targetPath))
            {
                throw new FileNotFoundException("原项目已经不存在，无法移动到桌面。", sourcePath);
            }

            await _store.DeleteItemsAsync(new[] { item.Id }, cancellationToken);
        }
        catch
        {
            if (movedOnDisk)
            {
                TryMovePath(targetPath, sourcePath);
            }

            throw;
        }

        try
        {
            await LogAsync(
                OperationType.ManagedMove,
                sourcePath,
                targetPath,
                new { itemId, destination = destinationRoot },
                canUndo: true,
                cancellationToken);
        }
        catch
        {
            // The file and database state are already consistent; logging must not undo the move.
        }
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

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return PathsEqual(normalizedPath, normalizedDirectory) ||
               normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueDestinationPath(string directory, string displayName, bool isDirectory)
    {
        var candidate = Path.Combine(directory, displayName);
        if (!PathExists(candidate))
        {
            return candidate;
        }

        var stem = isDirectory ? displayName : Path.GetFileNameWithoutExtension(displayName);
        var extension = isDirectory ? string.Empty : Path.GetExtension(displayName);
        for (var index = 2; index < 10000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"目标目录中已经存在太多同名项目：{displayName}");
    }

    private static void MovePath(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static void TryMovePath(string sourcePath, string destinationPath)
    {
        try
        {
            if (PathExists(sourcePath) && !PathExists(destinationPath))
            {
                MovePath(sourcePath, destinationPath);
            }
        }
        catch
        {
            // Best effort rollback; the caller still reports the original failure.
        }
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
