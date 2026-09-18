using DeskZone.Core.Services;
using Microsoft.Data.Sqlite;

namespace DeskZone.Storage.SQLite;

public sealed class SqliteStorageInitializer : IStorageInitializer
{
    public const int TargetSchemaVersion = 2;

    private readonly DataPaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly IBackupService? _backupService;

    public SqliteStorageInitializer(DataPaths paths, SqliteConnectionFactory connections, IBackupService? backupService = null)
    {
        _paths = paths;
        _connections = connections;
        _backupService = backupService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                schema_version INTEGER NOT NULL PRIMARY KEY,
                migration_id TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL
            );
            """, cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(connection, cancellationToken);
        if (currentVersion > TargetSchemaVersion)
        {
            throw new InvalidOperationException($"当前数据库版本 {currentVersion} 高于应用支持版本 {TargetSchemaVersion}。请使用更新版本的 DeskZone。 ");
        }

        if (currentVersion > 0 && currentVersion < TargetSchemaVersion && _backupService is not null)
        {
            await _backupService.CreateBackupAsync(BackupReason.BeforeMigration, cancellationToken);
        }

        if (currentVersion < 1)
        {
            await ApplyV1Async(connection, cancellationToken);
        }

        if (currentVersion < 2)
        {
            await ApplyV2Async(connection, cancellationToken);
        }

        await VerifyAsync(connection, cancellationToken);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(schema_version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ApplyV1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var sql = """
                CREATE TABLE panel_state (
                    id TEXT NOT NULL PRIMARY KEY,
                    monitor_id TEXT NULL,
                    dpi_x REAL NOT NULL DEFAULT 96,
                    dpi_y REAL NOT NULL DEFAULT 96,
                    left_dip REAL NOT NULL,
                    top_dip REAL NOT NULL,
                    width_dip REAL NOT NULL,
                    height_dip REAL NOT NULL,
                    opacity REAL NOT NULL DEFAULT 1.0,
                    is_collapsed INTEGER NOT NULL DEFAULT 0,
                    is_locked INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE categories (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    order_index INTEGER NOT NULL,
                    layout_type TEXT NOT NULL DEFAULT 'grid',
                    color TEXT NULL,
                    is_collapsed INTEGER NOT NULL DEFAULT 0,
                    sort_mode TEXT NOT NULL DEFAULT 'custom',
                    system_key TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE UNIQUE INDEX ux_categories_system_key
                    ON categories(system_key)
                    WHERE system_key IS NOT NULL;

                CREATE INDEX ix_categories_order
                    ON categories(order_index, created_at);

                CREATE TABLE items (
                    id TEXT NOT NULL PRIMARY KEY,
                    category_id TEXT NOT NULL,
                    item_mode TEXT NOT NULL,
                    original_path TEXT NOT NULL,
                    managed_path TEXT NULL,
                    display_name TEXT NOT NULL,
                    item_type TEXT NOT NULL,
                    custom_order INTEGER NOT NULL,
                    is_missing INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(category_id) REFERENCES categories(id) ON DELETE RESTRICT
                );

                CREATE INDEX ix_items_category_order
                    ON items(category_id, custom_order, created_at);

                CREATE INDEX ix_items_original_path
                    ON items(original_path COLLATE NOCASE);

                CREATE UNIQUE INDEX ux_items_reference_original_path
                    ON items(original_path COLLATE NOCASE)
                    WHERE item_mode = 'Reference';

                CREATE TABLE operation_log (
                    id TEXT NOT NULL PRIMARY KEY,
                    operation_type TEXT NOT NULL,
                    source_path TEXT NULL,
                    destination_path TEXT NULL,
                    metadata_json TEXT NULL,
                    status TEXT NOT NULL,
                    can_undo INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    completed_at TEXT NULL
                );

                CREATE INDEX ix_operation_log_created_at
                    ON operation_log(created_at DESC);
                """;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText = """
                    INSERT INTO schema_migrations(schema_version, migration_id, applied_at)
                    VALUES (1, '001_initial_local_backend', $appliedAt);
                    """;
                migration.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task ApplyV2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS recently_opened_items (
                        item_id TEXT NOT NULL PRIMARY KEY,
                        item_path TEXT NOT NULL,
                        display_name TEXT NOT NULL,
                        item_type TEXT NOT NULL,
                        opened_at TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS ix_recently_opened_items_opened_at
                        ON recently_opened_items(opened_at DESC);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText = """
                    INSERT INTO schema_migrations(schema_version, migration_id, applied_at)
                    VALUES (2, '002_recently_opened_items', $appliedAt);
                    """;
                migration.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = await QuickCheckAsync(connection, cancellationToken);
        if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // SQLite can report a damaged auto-index even when the table rows are
        // still readable. Rebuild only the affected table indexes after taking
        // a consistent backup, so startup can recover without dropping user data.
        if (result?.Contains("sqlite_autoindex_recently_opened_items_1", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_backupService is not null)
            {
                await _backupService.CreateBackupAsync(BackupReason.BeforeRepair, cancellationToken);
            }

            await ExecuteAsync(connection, "REINDEX recently_opened_items;", cancellationToken);
            result = await QuickCheckAsync(connection, cancellationToken);
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"DeskZone 本地数据库完整性检查失败：{result}");
        }
    }

    private static async Task<string?> QuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
