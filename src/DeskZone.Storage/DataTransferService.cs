using System.IO.Compression;
using System.Text.Json;
using DeskZone.Core.Services;
using DeskZone.Storage.SQLite;
using Microsoft.Data.Sqlite;

namespace DeskZone.Storage;

public sealed class DataTransferService : IDataTransferService
{
    private const int CurrentFormatVersion = 1;
    private readonly DataPaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly IBackupService _backup;

    public DataTransferService(
        DataPaths paths,
        SqliteConnectionFactory connections,
        IBackupService backup)
    {
        _paths = paths;
        _connections = connections;
        _backup = backup;
    }

    public async Task ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = GetFullPath(destinationPath, nameof(destinationPath));
        var temporaryRoot = CreateTemporaryDirectory("export");
        var temporaryArchive = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            var databasePath = Path.Combine(temporaryRoot, "deskzone.db");
            await CreateConsistentDatabaseCopyAsync(databasePath, cancellationToken);

            var settingsPath = Path.Combine(temporaryRoot, "settings.json");
            if (File.Exists(_paths.SettingsPath))
            {
                File.Copy(_paths.SettingsPath, settingsPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(settingsPath, "{}", cancellationToken);
            }

            var manifest = new
            {
                product = "DeskZone",
                formatVersion = CurrentFormatVersion,
                schemaVersion = SqliteStorageInitializer.TargetSchemaVersion,
                createdAt = DateTimeOffset.UtcNow,
                includes = new[] { "deskzone.db", "settings.json" }
            };
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }

            ZipFile.CreateFromDirectory(temporaryRoot, temporaryArchive, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(temporaryArchive, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteDirectory(temporaryRoot);
        }
    }

    public async Task ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = GetFullPath(sourcePath, nameof(sourcePath));
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("找不到要导入的数据文件。", source);
        }

        var temporaryRoot = CreateTemporaryDirectory("import");
        try
        {
            using var archive = ZipFile.OpenRead(source);
            var manifestEntry = FindEntry(archive, "manifest.json")
                ?? throw new InvalidDataException("数据文件缺少 manifest.json。请先使用 DeskZone 导出的数据文件。");
            var databaseEntry = FindEntry(archive, "deskzone.db")
                ?? throw new InvalidDataException("数据文件缺少 deskzone.db，无法导入。");

            using (var manifestStream = manifestEntry.Open())
            using (var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken))
            {
                var root = manifest.RootElement;
                var product = root.TryGetProperty("product", out var productElement)
                    ? productElement.GetString()
                    : null;
                var formatVersion = root.TryGetProperty("formatVersion", out var formatElement)
                    ? formatElement.GetInt32()
                    : 0;
                var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement)
                    ? schemaElement.GetInt32()
                    : 0;

                if (!string.Equals(product, "DeskZone", StringComparison.Ordinal) ||
                    formatVersion != CurrentFormatVersion)
                {
                    throw new InvalidDataException("这不是受支持的 DeskZone 数据文件。");
                }

                if (schemaVersion < 1 || schemaVersion > SqliteStorageInitializer.TargetSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"数据文件版本为 {schemaVersion}，当前版本只支持 1 到 {SqliteStorageInitializer.TargetSchemaVersion}。");
                }
            }

            var importedDatabasePath = Path.Combine(temporaryRoot, "deskzone.db");
            databaseEntry.ExtractToFile(importedDatabasePath, overwrite: true);
            var importedSettingsPath = Path.Combine(temporaryRoot, "settings.json");
            var settingsEntry = FindEntry(archive, "settings.json");
            settingsEntry?.ExtractToFile(importedSettingsPath, overwrite: true);

            await ValidateDatabaseAsync(importedDatabasePath, cancellationToken);
            if (settingsEntry is not null)
            {
                await ValidateJsonAsync(importedSettingsPath, cancellationToken);
            }

            await _backup.CreateBackupAsync(BackupReason.BeforeImport, cancellationToken);
            SqliteConnection.ClearAllPools();

            ReplaceFile(importedDatabasePath, _paths.DatabasePath);
            DeleteDatabaseSidecar(_paths.DatabasePath + "-wal");
            DeleteDatabaseSidecar(_paths.DatabasePath + "-shm");

            if (settingsEntry is not null)
            {
                ReplaceFile(importedSettingsPath, _paths.SettingsPath);
            }
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task CreateConsistentDatabaseCopyAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = await _connections.OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task ValidateDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await quickCheck.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("导入文件中的数据库校验失败，原有数据未被修改。");
        }

        await using var schemaCheck = connection.CreateCommand();
        schemaCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';";
        if (Convert.ToInt32(await schemaCheck.ExecuteScalarAsync(cancellationToken)) < 1)
        {
            throw new InvalidDataException("导入文件不是有效的 DeskZone 数据库，原有数据未被修改。");
        }
    }

    private static async Task ValidateJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), name, StringComparison.Ordinal));

    private static string CreateTemporaryDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DeskZone-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetFullPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static void ReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryDestination = destination + $".{Guid.NewGuid():N}.tmp";
        File.Copy(source, temporaryDestination, overwrite: true);
        try
        {
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryDestination);
        }
    }

    private static void DeleteDatabaseSidecar(string path)
    {
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
