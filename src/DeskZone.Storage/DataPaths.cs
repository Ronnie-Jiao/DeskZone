namespace DeskZone.Storage;

public sealed class DataPaths
{
    public DataPaths(string? localRoot = null, string? managedStorageRoot = null)
    {
        RootDirectory = localRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskZone");

        ManagedStorageDirectory = managedStorageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DeskZone",
            "Storage");

        DatabasePath = Path.Combine(RootDirectory, "deskzone.db");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        BackupsDirectory = Path.Combine(RootDirectory, "backups");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string BackupsDirectory { get; }
    public string LogsDirectory { get; }
    public string CacheDirectory { get; }
    public string ManagedStorageDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ManagedStorageDirectory);
    }
}
