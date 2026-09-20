using System.Globalization;
using DeskZone.Core.Contracts;
using DeskZone.Core.Exceptions;
using DeskZone.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeskZone.Storage.SQLite;

public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    private readonly SqliteConnectionFactory _connections;

    public SqliteWorkspaceStore(SqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeSystem = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = includeSystem
            ? "SELECT * FROM categories ORDER BY order_index, created_at;"
            : "SELECT * FROM categories WHERE system_key IS NULL ORDER BY order_index, created_at;";
        return await ReadCategoriesAsync(command, cancellationToken);
    }

    public async Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM categories WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
        var rows = await ReadCategoriesAsync(command, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<Category?> GetSystemCategoryAsync(string systemKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM categories WHERE system_key = $systemKey LIMIT 1;";
        command.Parameters.AddWithValue("$systemKey", systemKey);
        var rows = await ReadCategoriesAsync(command, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(order_index), -1) + 1 FROM categories WHERE system_key IS NULL;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InsertCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO categories(id, name, order_index, layout_type, color, is_collapsed, sort_mode, system_key, created_at, updated_at)
            VALUES ($id, $name, $orderIndex, $layoutType, $color, $isCollapsed, $sortMode, $systemKey, $createdAt, $updatedAt);
            """;
        AddCategoryParameters(command, category);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE categories
            SET name = $name,
                order_index = $orderIndex,
                layout_type = $layoutType,
                color = $color,
                is_collapsed = $isCollapsed,
                sort_mode = $sortMode,
                system_key = $systemKey,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddCategoryParameters(command, category);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new DeskZoneValidationException("分类更新失败：目标分类不存在。");
        }
    }

    public async Task ReorderCategoriesAsync(IReadOnlyList<Guid> orderedCategoryIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            for (var index = 0; index < orderedCategoryIds.Count; index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE categories SET order_index = $orderIndex, updated_at = $updatedAt WHERE id = $id AND system_key IS NULL;";
                command.Parameters.AddWithValue("$orderIndex", index);
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", orderedCategoryIds[index].ToString("D"));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new DeskZoneValidationException("分类排序失败：包含不存在的分类。");
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteCategoryAsync(
        Guid categoryId,
        CategoryDeleteMode mode,
        Guid? targetCategoryId,
        Guid unclassifiedCategoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var count = await CountItemsInCategoryAsync(connection, transaction, categoryId, cancellationToken);
            if (count > 0)
            {
                if (mode == CategoryDeleteMode.RejectIfNotEmpty)
                {
                    throw new DeskZoneValidationException("分类中仍有内容。请选择移动到其他分类或解除分类后再删除。");
                }

                var destination = mode switch
                {
                    CategoryDeleteMode.MoveItemsToCategory when targetCategoryId.HasValue => targetCategoryId.Value,
                    CategoryDeleteMode.MoveItemsToUnclassified => unclassifiedCategoryId,
                    _ => throw new DeskZoneValidationException("删除分类的内容处理方式无效。")
                };

                await using var move = connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText = "UPDATE items SET category_id = $destination, updated_at = $updatedAt WHERE category_id = $source;";
                move.Parameters.AddWithValue("$destination", destination.ToString("D"));
                move.Parameters.AddWithValue("$source", categoryId.ToString("D"));
                move.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                await move.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM categories WHERE id = $id AND system_key IS NULL;";
            delete.Parameters.AddWithValue("$id", categoryId.ToString("D"));
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new DeskZoneValidationException("分类删除失败：目标分类不存在或为系统分类。");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<DesktopItem>> GetItemsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM items WHERE category_id = $categoryId ORDER BY custom_order, created_at;";
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
        return await ReadItemsAsync(command, cancellationToken);
    }

    public async Task<DesktopItem?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM items WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        var rows = await ReadItemsAsync(command, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<DesktopItem?> FindItemByOriginalPathAsync(string originalPath, DesktopItemMode mode, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM items WHERE item_mode = $mode AND original_path = $path COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$path", originalPath);
        var rows = await ReadItemsAsync(command, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<RecentOpenedItem>> GetRecentlyOpenedItemsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, item_path, display_name, item_type, opened_at
            FROM recently_opened_items
            ORDER BY opened_at DESC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", Math.Max(maximumCount, 1));

        var result = new List<RecentOpenedItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RecentOpenedItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<DesktopItemType>(reader.GetString(3), true),
                ParseDate(reader.GetString(4))));
        }

        return result;
    }

    public async Task RecordRecentlyOpenedItemAsync(RecentOpenedItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recently_opened_items(item_id, item_path, display_name, item_type, opened_at)
            VALUES ($itemId, $itemPath, $displayName, $itemType, $openedAt)
            ON CONFLICT(item_id) DO UPDATE SET
                item_path = excluded.item_path,
                display_name = excluded.display_name,
                item_type = excluded.item_type,
                opened_at = excluded.opened_at;
            """;
        command.Parameters.AddWithValue("$itemId", item.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$itemPath", item.Path);
        command.Parameters.AddWithValue("$displayName", item.DisplayName);
        command.Parameters.AddWithValue("$itemType", item.ItemType.ToString());
        command.Parameters.AddWithValue("$openedAt", item.OpenedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetNextItemOrderAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(custom_order), -1) + 1 FROM items WHERE category_id = $categoryId;";
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InsertItemsAsync(IReadOnlyCollection<DesktopItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO items(id, category_id, item_mode, original_path, managed_path, display_name, item_type, custom_order, is_missing, created_at, updated_at)
                    VALUES ($id, $categoryId, $itemMode, $originalPath, $managedPath, $displayName, $itemType, $customOrder, $isMissing, $createdAt, $updatedAt);
                    """;
                AddItemParameters(command, item);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task ReorderItemsAsync(
        Guid categoryId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default)
    {
        if (orderedItemIds.Count == 0)
        {
            return;
        }

        if (orderedItemIds.Distinct().Count() != orderedItemIds.Count)
        {
            throw new DeskZoneValidationException("项目排序失败：排序列表中包含重复项目。");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.Transaction = transaction;
                countCommand.CommandText = "SELECT COUNT(*) FROM items WHERE category_id = $categoryId;";
                countCommand.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
                var itemCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
                if (itemCount != orderedItemIds.Count)
                {
                    throw new DeskZoneValidationException("项目排序失败：分类内容已发生变化，请重试。");
                }
            }

            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            for (var index = 0; index < orderedItemIds.Count; index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE items
                    SET custom_order = $customOrder, updated_at = $updatedAt
                    WHERE id = $id AND category_id = $categoryId;
                    """;
                command.Parameters.AddWithValue("$customOrder", index);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.Parameters.AddWithValue("$id", orderedItemIds[index].ToString("D"));
                command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new DeskZoneValidationException("项目排序失败：包含不存在或不属于当前分类的项目。");
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateItemAsync(DesktopItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items
            SET category_id = $categoryId,
                item_mode = $itemMode,
                original_path = $originalPath,
                managed_path = $managedPath,
                display_name = $displayName,
                item_type = $itemType,
                custom_order = $customOrder,
                is_missing = $isMissing,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddItemParameters(command, item);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new DeskZoneValidationException("更新文件入口失败：目标入口不存在。");
        }
    }

    public async Task MoveItemsAsync(IReadOnlyCollection<Guid> itemIds, Guid targetCategoryId, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var nextOrder = await GetNextItemOrderAsync(connection, transaction, targetCategoryId, cancellationToken);
            foreach (var itemId in itemIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE items
                    SET category_id = $categoryId, custom_order = $customOrder, updated_at = $updatedAt
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$categoryId", targetCategoryId.ToString("D"));
                command.Parameters.AddWithValue("$customOrder", nextOrder++);
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", itemId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new DeskZoneValidationException("移动失败：某个文件入口不存在。");
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task RemoveItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var itemId in itemIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM items WHERE id = $id AND item_mode = 'Reference';";
                command.Parameters.AddWithValue("$id", itemId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var itemId in itemIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM items WHERE id = $id;";
                command.Parameters.AddWithValue("$id", itemId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SetItemMissingStateAsync(Guid itemId, bool isMissing, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET is_missing = $isMissing, updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$isMissing", isMissing ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DesktopPanel?> GetPanelAsync(Guid panelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM panel_state WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", panelId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DesktopPanel(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            GetNullableString(reader, "monitor_id"),
            reader.GetDouble(reader.GetOrdinal("dpi_x")),
            reader.GetDouble(reader.GetOrdinal("dpi_y")),
            reader.GetDouble(reader.GetOrdinal("left_dip")),
            reader.GetDouble(reader.GetOrdinal("top_dip")),
            reader.GetDouble(reader.GetOrdinal("width_dip")),
            reader.GetDouble(reader.GetOrdinal("height_dip")),
            reader.GetDouble(reader.GetOrdinal("opacity")),
            reader.GetInt32(reader.GetOrdinal("is_collapsed")) != 0,
            reader.GetInt32(reader.GetOrdinal("is_locked")) != 0,
            ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
            ParseDate(reader.GetString(reader.GetOrdinal("updated_at"))));
    }

    public async Task UpsertPanelAsync(DesktopPanel panel, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO panel_state(id, monitor_id, dpi_x, dpi_y, left_dip, top_dip, width_dip, height_dip, opacity, is_collapsed, is_locked, created_at, updated_at)
            VALUES ($id, $monitorId, $dpiX, $dpiY, $leftDip, $topDip, $widthDip, $heightDip, $opacity, $isCollapsed, $isLocked, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                monitor_id = excluded.monitor_id,
                dpi_x = excluded.dpi_x,
                dpi_y = excluded.dpi_y,
                left_dip = excluded.left_dip,
                top_dip = excluded.top_dip,
                width_dip = excluded.width_dip,
                height_dip = excluded.height_dip,
                opacity = excluded.opacity,
                is_collapsed = excluded.is_collapsed,
                is_locked = excluded.is_locked,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", panel.Id.ToString("D"));
        command.Parameters.AddWithValue("$monitorId", (object?)panel.MonitorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dpiX", panel.DpiX);
        command.Parameters.AddWithValue("$dpiY", panel.DpiY);
        command.Parameters.AddWithValue("$leftDip", panel.LeftDip);
        command.Parameters.AddWithValue("$topDip", panel.TopDip);
        command.Parameters.AddWithValue("$widthDip", panel.WidthDip);
        command.Parameters.AddWithValue("$heightDip", panel.HeightDip);
        command.Parameters.AddWithValue("$opacity", panel.Opacity);
        command.Parameters.AddWithValue("$isCollapsed", panel.IsCollapsed ? 1 : 0);
        command.Parameters.AddWithValue("$isLocked", panel.IsLocked ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", panel.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", panel.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendOperationAsync(OperationLog operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_log(id, operation_type, source_path, destination_path, metadata_json, status, can_undo, created_at, completed_at)
            VALUES ($id, $operationType, $sourcePath, $destinationPath, $metadataJson, $status, $canUndo, $createdAt, $completedAt);
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$operationType", operation.OperationType.ToString());
        command.Parameters.AddWithValue("$sourcePath", (object?)operation.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$destinationPath", (object?)operation.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", (object?)operation.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", operation.Status.ToString());
        command.Parameters.AddWithValue("$canUndo", operation.CanUndo ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", operation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", (object?)operation.CompletedAt?.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationLog>> GetRecentOperationsAsync(int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM operation_log ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<OperationLog>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OperationLog(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Enum.Parse<OperationType>(reader.GetString(reader.GetOrdinal("operation_type")), true),
                GetNullableString(reader, "source_path"),
                GetNullableString(reader, "destination_path"),
                GetNullableString(reader, "metadata_json"),
                Enum.Parse<OperationStatus>(reader.GetString(reader.GetOrdinal("status")), true),
                reader.GetInt32(reader.GetOrdinal("can_undo")) != 0,
                ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
                GetNullableDate(reader, "completed_at")));
        }
        return result;
    }

    private static async Task<int> CountItemsInCategoryAsync(SqliteConnection connection, SqliteTransaction transaction, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM items WHERE category_id = $categoryId;";
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> GetNextItemOrderAsync(SqliteConnection connection, SqliteTransaction transaction, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(custom_order), -1) + 1 FROM items WHERE category_id = $categoryId;";
        command.Parameters.AddWithValue("$categoryId", categoryId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<Category>> ReadCategoriesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<Category>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Category(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetInt32(reader.GetOrdinal("order_index")),
                reader.GetString(reader.GetOrdinal("layout_type")),
                GetNullableString(reader, "color"),
                reader.GetInt32(reader.GetOrdinal("is_collapsed")) != 0,
                reader.GetString(reader.GetOrdinal("sort_mode")),
                GetNullableString(reader, "system_key"),
                ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
                ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<DesktopItem>> ReadItemsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<DesktopItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DesktopItem(
                Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Guid.Parse(reader.GetString(reader.GetOrdinal("category_id"))),
                Enum.Parse<DesktopItemMode>(reader.GetString(reader.GetOrdinal("item_mode")), true),
                reader.GetString(reader.GetOrdinal("original_path")),
                GetNullableString(reader, "managed_path"),
                reader.GetString(reader.GetOrdinal("display_name")),
                Enum.Parse<DesktopItemType>(reader.GetString(reader.GetOrdinal("item_type")), true),
                reader.GetInt32(reader.GetOrdinal("custom_order")),
                reader.GetInt32(reader.GetOrdinal("is_missing")) != 0,
                ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
                ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))));
        }
        return result;
    }

    private static void AddCategoryParameters(SqliteCommand command, Category category)
    {
        command.Parameters.AddWithValue("$id", category.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$orderIndex", category.OrderIndex);
        command.Parameters.AddWithValue("$layoutType", category.LayoutType);
        command.Parameters.AddWithValue("$color", (object?)category.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$isCollapsed", category.IsCollapsed ? 1 : 0);
        command.Parameters.AddWithValue("$sortMode", category.SortMode);
        command.Parameters.AddWithValue("$systemKey", (object?)category.SystemKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", category.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", category.UpdatedAt.ToString("O"));
    }

    private static void AddItemParameters(SqliteCommand command, DesktopItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$categoryId", item.CategoryId.ToString("D"));
        command.Parameters.AddWithValue("$itemMode", item.ItemMode.ToString());
        command.Parameters.AddWithValue("$originalPath", item.OriginalPath);
        command.Parameters.AddWithValue("$managedPath", (object?)item.ManagedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", item.DisplayName);
        command.Parameters.AddWithValue("$itemType", item.ItemType.ToString());
        command.Parameters.AddWithValue("$customOrder", item.CustomOrder);
        command.Parameters.AddWithValue("$isMissing", item.IsMissing ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
    }

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? GetNullableDate(SqliteDataReader reader, string name)
    {
        var value = GetNullableString(reader, name);
        return value is null ? null : ParseDate(value);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
