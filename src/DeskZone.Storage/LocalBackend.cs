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
        ICategoryService categories,
        IDesktopItemService items,
        ILayoutService layout)
    {
        Paths = paths;
        Initializer = initializer;
        WorkspaceStore = workspaceStore;
        Settings = settings;
        Backup = backup;
        Categories = categories;
        Items = items;
        Layout = layout;
    }

    public DataPaths Paths { get; }
    public IStorageInitializer Initializer { get; }
    public IWorkspaceStore WorkspaceStore { get; }
    public ISettingsStore Settings { get; }
    public IBackupService Backup { get; }
    public ICategoryService Categories { get; }
    public IDesktopItemService Items { get; }
    public ILayoutService Layout { get; }

    public static LocalBackend CreateDefault()
    {
        var paths = new DataPaths();
        var connections = new SqliteConnectionFactory(paths);
        var backup = new SqliteBackupService(paths, connections);
        var initializer = new SqliteStorageInitializer(paths, connections, backup);
        var workspace = new SqliteWorkspaceStore(connections);
        var settings = new JsonSettingsStore(paths.RootDirectory);

        return new LocalBackend(
            paths,
            initializer,
            workspace,
            settings,
            backup,
            new CategoryService(workspace),
            new DesktopItemService(workspace, paths.ManagedStorageDirectory),
            new LayoutService(workspace));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Initializer.InitializeAsync(cancellationToken);
}
