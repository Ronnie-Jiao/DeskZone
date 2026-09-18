using DeskZone.Core.Contracts;
using DeskZone.Core.Services;
using DeskZone.Storage.Backup;
using DeskZone.Storage.SQLite;

namespace DeskZone.Storage;

public sealed class LocalBackend
{
    private LocalBackend(
        DataPaths paths,
        IStorageInitializer initializer,
        IWorkspaceStore workspaceStore,
        ISettingsStore settings,
        IBackupService backup,
        IDataTransferService dataTransfer,
        ICategoryService categories,
        IDesktopItemService items,
        ILayoutService layout,
        IFileSystemChangeMonitor fileSystemChanges)
    {
        Paths = paths;
        Initializer = initializer;
        WorkspaceStore = workspaceStore;
        Settings = settings;
        Backup = backup;
        DataTransfer = dataTransfer;
        Categories = categories;
        Items = items;
        Layout = layout;
        FileSystemChanges = fileSystemChanges;
    }

    public DataPaths Paths { get; }
    public IStorageInitializer Initializer { get; }
    public IWorkspaceStore WorkspaceStore { get; }
    public ISettingsStore Settings { get; }
    public IBackupService Backup { get; }
    public IDataTransferService DataTransfer { get; }
    public ICategoryService Categories { get; }
    public IDesktopItemService Items { get; }
    public ILayoutService Layout { get; }
    public IFileSystemChangeMonitor FileSystemChanges { get; }

    public static LocalBackend CreateDefault()
    {
        var paths = new DataPaths();
        var connections = new SqliteConnectionFactory(paths);
        var backup = new SqliteBackupService(paths, connections);
        var dataTransfer = new DataTransferService(paths, connections, backup);
        var initializer = new SqliteStorageInitializer(paths, connections, backup);
        var workspace = new SqliteWorkspaceStore(connections);
        var settings = new JsonSettingsStore(paths.RootDirectory);

        return new LocalBackend(
            paths,
            initializer,
            workspace,
            settings,
            backup,
            dataTransfer,
            new CategoryService(workspace),
            new DesktopItemService(workspace, paths.ManagedStorageDirectory),
            new LayoutService(workspace),
            new FileSystemChangeMonitor());
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Initializer.InitializeAsync(cancellationToken);

    public void Dispose() => FileSystemChanges.Dispose();
}
