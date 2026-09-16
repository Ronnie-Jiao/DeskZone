using System.Text.Json;
using DeskZone.Core.Services;
using DeskZone.Storage.SQLite;
using Microsoft.Data.Sqlite;

namespace DeskZone.Storage.Backup;

public sealed class SqliteBackupService : IBackupService
{
    private readonly DataPaths _paths;
    private readonly SqliteConnectionFactory _connections;

    public SqliteBackupService(DataPaths paths, SqliteConnectionFactory connections)
    {
        _paths = paths;
        _connections = connections;
    }

    public async Task<string> CreateBackupAsync(BackupReason reason, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDirectory = Path.Combine(_paths.BackupsDirectory, stamp);
        Directory.CreateDirectory(backupDirectory);

        if (File.Exists(_paths.DatabasePath))
        {
            var destinationPath = Path.Combine(backupDirectory, "deskzone.db");
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

        if (File.Exists(_paths.SettingsPath))
        {
            File.Copy(_paths.SettingsPath, Path.Combine(backupDirectory, "settings.json"), overwrite: true);
        }

        var manifest = new
        {
            product = "DeskZone",
            reason = reason.ToString(),
            createdAt = DateTimeOffset.UtcNow,
            database = File.Exists(Path.Combine(backupDirectory, "deskzone.db")),
            settings = File.Exists(Path.Combine(backupDirectory, "settings.json"))
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        await PruneAsync(8, cancellationToken);
        return backupDirectory;
    }

    public Task PruneAsync(int keepLatest = 8, CancellationToken cancellationToken = default)
    {
        if (keepLatest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLatest));
        }

        if (!Directory.Exists(_paths.BackupsDirectory))
        {
            return Task.CompletedTask;
        }

        var directories = new DirectoryInfo(_paths.BackupsDirectory)
            .EnumerateDirectories()
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .Skip(keepLatest)
            .ToArray();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Backup retention must never block application startup.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the backup if Windows denies deletion.
            }
        }

        return Task.CompletedTask;
    }
}
