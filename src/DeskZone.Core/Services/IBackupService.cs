namespace DeskZone.Core.Services;

public enum BackupReason
{
    Manual,
    BeforeMigration,
    BeforeImport,
    UpgradeStartup,
    DailyStartup
}

public interface IBackupService
{
    Task<string> CreateBackupAsync(BackupReason reason, CancellationToken cancellationToken = default);
    Task PruneAsync(int keepLatest = 8, CancellationToken cancellationToken = default);
}
