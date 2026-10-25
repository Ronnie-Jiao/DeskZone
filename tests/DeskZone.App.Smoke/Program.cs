using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskZone.App;
using DeskZone.App.ViewModels;
using DeskZone.Core.Models;
using DeskZone.Core.Services;
using DeskZone.Shell;
using DeskZone.Storage;
using DeskZone.Storage.Backup;
using DeskZone.Storage.SQLite;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _exitCode;
    private static bool _shortcutCleanupOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        _shortcutCleanupOnly = args.Contains("--shortcut-cleanup-only", StringComparer.OrdinalIgnoreCase);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var (key, color) in new[]
        {
            ("BrandBrush", "#126FF7"), ("SkyBrush", "#1DA4F4"), ("CyanBrush", "#31CDEE"),
            ("TextBrush", "#0B2447"), ("MutedBrush", "#586981")
        })
            application.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await VerifyAsync(); }
            catch (Exception error) { Console.Error.WriteLine(error); _exitCode = 1; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task VerifyAsync()
    {
        VerifyCardRowsFillFromLeft();

        var testRoot = Path.Combine(Path.GetTempPath(), "DeskZoneHeaderDropSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var shortcutTargetResolver = new WindowsShortcutTargetResolver();
        var backend = CreateBackend(
            new DataPaths(Path.Combine(testRoot, "data"), Path.Combine(testRoot, "managed")),
            shortcutTargetResolver);
        MainWindow? window = null;
        HwndSource? renderHost = null;
        try
        {
            await backend.InitializeAsync();
            var targetModel = await backend.Categories.CreateAsync("脚本", "#126FF7");
            var sourceModel = await backend.Categories.CreateAsync("其他分类", "#126FF7");
            var cleanupModel = await backend.Categories.CreateAsync("删除清理", "#126FF7");
            await backend.Categories.SetCollapsedAsync(targetModel.Id, true);
            var batchPath = Path.Combine(testRoot, "桌面测试脚本.bat");
            var shortcutPath = Path.Combine(testRoot, "测试快捷方式.lnk");
            var existingPath = Path.Combine(testRoot, "已有入口.txt");
            var internalPath = Path.Combine(testRoot, "应用内入口.txt");
            var editPath = Path.Combine(testRoot, "编辑名称时拖入.txt");
            var shortcutTargetPath = Path.Combine(testRoot, "快捷方式目标.exe");
            var uninstalledTargetPath = Path.Combine(testRoot, "已卸载应用.exe");
            var staleShortcutPath = Path.Combine(testRoot, "已卸载应用.lnk");
            var startupMissingPath = Path.Combine(testRoot, "启动前已删除.txt");
            var deletedReferencePath = Path.Combine(testRoot, "运行中删除的引用.txt");
            var managedDeleteSourcePath = Path.Combine(testRoot, "待删除的托管文件.txt");
            File.WriteAllText(batchPath, "@echo off\r\necho smoke-test\r\n");
            File.WriteAllText(existingPath, "existing reference");
            File.WriteAllText(internalPath, "internal reference");
            File.WriteAllText(editPath, "edit-state drop");
            File.WriteAllText(shortcutTargetPath, "shortcut target");
            File.WriteAllText(uninstalledTargetPath, "application before uninstall");
            File.WriteAllText(startupMissingPath, "delete before workspace load");
            File.WriteAllText(deletedReferencePath, "delete while workspace is running");
            File.WriteAllText(managedDeleteSourcePath, "managed item delete");
            CreateShortcut(shortcutPath, shortcutTargetPath);
            CreateShortcut(staleShortcutPath, uninstalledTargetPath);
            await backend.Items.AddReferencesAsync(sourceModel.Id, new[] { existingPath, internalPath, shortcutPath });
            await backend.Items.AddReferencesAsync(cleanupModel.Id, new[] { startupMissingPath, deletedReferencePath, staleShortcutPath });
            var staleShortcut = (await backend.Items.ListByCategoryAsync(cleanupModel.Id))
                .Single(item => item.ActivePath == staleShortcutPath);
            await backend.Items.RecordOpenedAsync(staleShortcut);
            await backend.Items.MovePathsIntoCategoryAsync(cleanupModel.Id, new[] { managedDeleteSourcePath });
            File.Delete(startupMissingPath);
            File.Delete(uninstalledTargetPath);

            // Render the real MainWindow templates in memory. Never Show the
            // window or attach it to Explorer; all storage is in testRoot.
            window = new MainWindow(backend, new NoShell(), new NoDesktopHost(), shortcutTargetResolver);
            typeof(MainWindow).GetMethod("DisposeTrayIcon", PrivateInstance)!.Invoke(window, null);
            window.Resources["SkyBrush"] = new SolidColorBrush(Colors.DeepSkyBlue);
            var workspace = (WorkspaceViewModel)window.DataContext;
            await workspace.InitializeAsync();
            ((DispatcherTimer)typeof(MainWindow).GetField("_fileSystemRefreshTimer", PrivateInstance)!
                .GetValue(window)!).Start();
            Require((await backend.Items.ListByCategoryAsync(cleanupModel.Id)).All(item => item.ActivePath != startupMissingPath),
                "startup left a deleted local reference in the workspace");
            Require(await backend.WorkspaceStore.GetItemAsync(staleShortcut.Id) is null,
                "startup left a shortcut whose installed application target no longer exists");
            Require(File.Exists(staleShortcutPath),
                "startup cleanup deleted the user's physical shortcut file");
            var startupRecent = await backend.Items.ListRecentlyOpenedAsync(100);
            Require(startupRecent.All(item => item.ItemId != staleShortcut.Id),
                "startup cleanup left the stale shortcut in recently opened history");

            var cleanupCategory = workspace.Categories.Single(category => category.Id == cleanupModel.Id);
            var deletedReference = cleanupCategory.Items.Single(item => item.Path == deletedReferencePath);
            var deletedManagedItem = cleanupCategory.Items.Single(item => item.Model.ItemMode == DesktopItemMode.Managed);

            var liveTargetPath = Path.Combine(testRoot, "运行中卸载的应用.exe");
            var liveShortcutPath = Path.Combine(testRoot, "运行中卸载的应用.lnk");
            File.WriteAllText(liveTargetPath, "application before uninstall");
            CreateShortcut(liveShortcutPath, liveTargetPath);
            await workspace.AddReferencesAsync(cleanupCategory, new[] { liveShortcutPath });
            var liveShortcut = cleanupCategory.Items.Single(item => item.Path == liveShortcutPath);
            await backend.Items.RecordOpenedAsync(liveShortcut.Model);
            File.Delete(liveTargetPath);
            Require(await workspace.RemoveMissingShortcutTargetsAsync() == 1,
                "live cleanup did not remove a shortcut whose application target was uninstalled");
            Require(await backend.WorkspaceStore.GetItemAsync(liveShortcut.Id) is null,
                "live cleanup left the stale shortcut reference in the category");
            Require(File.Exists(liveShortcutPath),
                "live cleanup deleted the user's physical shortcut file");

            Require(await workspace.RemoveDeletedItemsAsync(new[] { deletedReferencePath }) == 0,
                "an existing path was removed as if it had been deleted");
            await backend.Items.RecordOpenedAsync(deletedReference.Model);
            await backend.Items.RecordOpenedAsync(deletedManagedItem.Model);
            File.Delete(deletedReference.Path);
            File.Delete(deletedManagedItem.Path);

            var deleteTimeout = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deleteTimeout &&
                   (await backend.WorkspaceStore.GetItemAsync(deletedReference.Id) is not null ||
                    await backend.WorkspaceStore.GetItemAsync(deletedManagedItem.Id) is not null))
            {
                await Task.Delay(50);
            }

            Require(await backend.WorkspaceStore.GetItemAsync(deletedReference.Id) is null,
                "external deletion left a reference record behind");
            Require(await backend.WorkspaceStore.GetItemAsync(deletedManagedItem.Id) is null,
                "external deletion left a managed item record behind");
            var recent = await backend.Items.ListRecentlyOpenedAsync(100);
            Require(recent.All(item => item.ItemId != deletedReference.Id &&
                                       item.ItemId != deletedManagedItem.Id &&
                                       item.ItemId != liveShortcut.Id),
                "deleted items or stale shortcuts remained in recently opened history");
            Console.WriteLine("PASS: startup and live cleanup remove deleted items and uninstalled-app shortcuts from DeskZone data while preserving the .lnk files.");
            if (_shortcutCleanupOnly)
            {
                return;
            }

            var root = (FrameworkElement)window.Content;
            window.Content = null;
            root.DataContext = workspace;
            root.Resources.MergedDictionaries.Add(window.Resources);
            renderHost = new HwndSource(new HwndSourceParameters("DeskZone header drop test")
            {
                WindowStyle = unchecked((int)0x80000000),
                PositionX = -32000, PositionY = -32000, Width = 700, Height = 1600
            });
            renderHost.RootVisual = root;
            Layout(root);

            var searchClearButton = (Button)typeof(MainWindow)
                .GetField("SearchClearButton", PrivateInstance)!
                .GetValue(window)!;
            workspace.SearchText = "gsfdg";
            Layout(root);
            Require(searchClearButton.Visibility == Visibility.Visible,
                "search clear button did not appear when search text was entered");
            Require(workspace.Categories.Any(category => !category.IsVisible),
                "search text did not filter the category list");
            searchClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, searchClearButton));
            Layout(root);
            Require(string.IsNullOrEmpty(workspace.SearchText) && !workspace.HasSearchText,
                "search clear button did not clear the bound search text");
            Require(searchClearButton.Visibility == Visibility.Collapsed &&
                    workspace.Categories.All(category => category.IsVisible),
                "clearing search did not hide the button and restore all categories");
            Console.WriteLine("PASS: search clear button appears with text and restores the unfiltered categories when clicked.");

            var searchInputWindow = new SearchInputWindow { DataContext = workspace };
            try
            {
                var overlayClearButton = (Button)typeof(SearchInputWindow)
                    .GetField("SearchClearButton", PrivateInstance)!
                    .GetValue(searchInputWindow)!;
                workspace.SearchText = "gsfdg";
                Require(overlayClearButton.Visibility == Visibility.Visible,
                    "search clear button did not appear in the active text-entry layer");
                overlayClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, overlayClearButton));
                Require(string.IsNullOrEmpty(workspace.SearchText) && !workspace.HasSearchText,
                    "active text-entry layer did not clear the bound search text");
            }
            finally
            {
                searchInputWindow.Close();
            }
            Console.WriteLine("PASS: the active text-entry layer also clears search from its right-side button.");

            var name = FindName(root, targetModel.Id);
            Require(name.IsReadOnly, "the category name should initially be read-only");
            var data = Files(batchPath, shortcutPath);
            VerifyHover(root, name, data, DragDropEffects.Move);
            await DropAsync(root, name, data, DragDropEffects.Move, workspace);
            var target = workspace.Categories.Single(category => category.Id == targetModel.Id);
            Require(target.IsExpanded && target.VisibleItemCount == 2, "collapsed category did not expand and display both entries");
            Require(target.Items.Any(item => item.Model.ItemType == DesktopItemType.Shortcut), "shortcut metadata was not preserved");
            Require(!File.Exists(batchPath) && !File.Exists(shortcutPath), "header drop did not remove the originals");
            Require(target.Items.All(item => item.Model.ItemMode == DesktopItemMode.Managed && File.Exists(item.Path)), "moved items are not present in category storage");
            Console.WriteLine("PASS: title drop moves file and shortcut into category storage and expands the category.");

            Layout(root);
            name = FindName(root, targetModel.Id);
            var grid = (Grid)VisualTreeHelper.GetParent(name);
            foreach (var child in grid.Children.OfType<FrameworkElement>())
                VerifyHover(root, child, data, DragDropEffects.Move);
            Console.WriteLine("PASS: title name, toggle arrow, item count and overflow button all accept file hover.");

            name.IsReadOnly = false;
            await DropAsync(root, name, Files(editPath), DragDropEffects.Move, workspace);
            Require(name.Text == "脚本", "file drop altered category name while editing");
            Layout(root);
            name = FindName(root, targetModel.Id);
            await DropAsync(root, name, Files(editPath), DragDropEffects.Copy, workspace);
            Require(workspace.Categories.Single(category => category.Id == targetModel.Id).Items.Count == 3, "repeated drop duplicated entries");
            Console.WriteLine("PASS: editing title remains intact, and failed repeated moves preserve the source.");

            Layout(root);
            name = FindName(root, targetModel.Id);
            await DropAsync(root, name, Files(existingPath), DragDropEffects.Move, workspace);
            target = workspace.Categories.Single(category => category.Id == targetModel.Id);
            var source = workspace.Categories.Single(category => category.Id == sourceModel.Id);
            Require(target.Items.Any(item => item.Model.ItemMode == DesktopItemMode.Managed && item.Model.OriginalPath == existingPath && File.Exists(item.Path)), "existing reference was not moved into target storage");
            Require(!File.Exists(existingPath), "existing reference source was not removed");
            Require(source.Items.All(item => item.Path != existingPath) && source.VisibleItemCount == 1, "old category retained stale entry/count");
            Console.WriteLine("PASS: existing reference is moved into target and both visible counts refresh.");

            Layout(root);
            var internalItem = source.Items.Single();
            var internalData = new DataObject();
            internalData.SetData("DeskZone.ItemId", internalItem.Id.ToString());
            internalData.SetData("DeskZone.ItemCategoryId", source.Id.ToString());
            await DropAsync(root, FindName(root, targetModel.Id), internalData, DragDropEffects.Move, workspace);
            Require(workspace.Categories.Single(category => category.Id == sourceModel.Id).VisibleItemCount == 0, "internal item drag was not preserved");
            Console.WriteLine("PASS: application item drag still moves category membership.");

            Layout(root);
            var system = workspace.Categories.First(category => category.IsSystem);
            VerifyHover(root, FindName(root, system.Id), data, DragDropEffects.None);
            Console.WriteLine("PASS: system category rejects file drop.");
        }
        finally
        {
            renderHost?.Dispose();
            if (window is not null)
            {
                typeof(MainWindow).GetField("_allowClose", PrivateInstance)!.SetValue(window, true);
                window.Close();
            }
            backend.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // This uniquely created directory contains only this test's files.
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void VerifyCardRowsFillFromLeft()
    {
        const double availableWidth = 450;
        var converter = new AdaptiveCardWidthConverter();
        var cardWidth = (double)converter.Convert(
            availableWidth,
            typeof(double),
            null,
            System.Globalization.CultureInfo.InvariantCulture);
        var wrapPanel = new WrapPanel { Width = availableWidth };
        for (var index = 0; index < 11; index++)
        {
            wrapPanel.Children.Add(new Border
            {
                Width = cardWidth,
                Height = 72,
                Margin = new Thickness(3)
            });
        }

        wrapPanel.Measure(new Size(availableWidth, 1000));
        wrapPanel.Arrange(new Rect(0, 0, availableWidth, 1000));
        var positions = wrapPanel.Children
            .OfType<Border>()
            .Select(card => card.TranslatePoint(new Point(0, 0), wrapPanel))
            .ToArray();

        Require(positions.Take(5).All(position => Math.Abs(position.Y - positions[0].Y) < 0.1),
            "the first row should fit five cards");
        Require(positions.Skip(5).Take(5).All(position => Math.Abs(position.Y - positions[5].Y) < 0.1),
            "the second row should fit the next five cards");
        Require(positions[10].Y > positions[5].Y,
            "the final card should wrap only after the preceding row is full");
        var firstRowX = positions.Take(5).Select(position => position.X).ToArray();
        Require(firstRowX.Zip(firstRowX.Skip(1), (left, right) => right > left).All(isIncreasing => isIncreasing),
            "cards should be laid out from left to right");
        Console.WriteLine("PASS: cards fill each row from the left and wrap only after the row is full.");
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(700, 1600));
        root.Arrange(new Rect(0, 0, 700, 1600));
        root.UpdateLayout();
    }

    private static TextBox FindName(DependencyObject root, Guid id) =>
        Descendants(root).OfType<TextBox>().First(text => text.Tag is CategoryViewModel category && category.Id == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static UIElement Hit(FrameworkElement root, FrameworkElement element)
    {
        var point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), root);
        var target = root.InputHitTest(point) as UIElement;
        // WPF's OLE target checks AllowDrop on the directly hit element. An
        // AllowDrop=False name field prevents any routed event reaching its parent.
        Require(target is not null && target.AllowDrop,
            $"directly hit header element disables AllowDrop: {target?.GetType().Name ?? "null"}, point={point}, visible={element.IsVisible}");
        return target!;
    }

    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent preview, RoutedEvent bubble)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(PrivateInstance).Single(info => info.GetParameters().Length == 5);
        var args = (DragEventArgs)constructor.Invoke(new object[]
        {
            data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, target, new Point(1, 1)
        });
        args.RoutedEvent = preview;
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = bubble;
            target.RaiseEvent(args);
        }
        return args;
    }

    private static void VerifyHover(FrameworkElement root, FrameworkElement element, IDataObject data, DragDropEffects effect)
    {
        var target = Hit(root, element);
        foreach (var events in new[]
        {
            (DragDrop.PreviewDragEnterEvent, DragDrop.DragEnterEvent),
            (DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent)
        })
        {
            var args = Raise(target, data, events.Item1, events.Item2);
            Require(args.Handled && args.Effects == effect, "header hover returned wrong effect or was intercepted by child control");
        }
        Raise(target, data, DragDrop.PreviewDragLeaveEvent, DragDrop.DragLeaveEvent);
    }

    private static async Task DropAsync(FrameworkElement root, FrameworkElement element, IDataObject data, DragDropEffects effect, WorkspaceViewModel workspace)
    {
        var args = Raise(Hit(root, element), data, DragDrop.PreviewDropEvent, DragDrop.DropEvent);
        Require(args.Handled && args.Effects == effect, "category release did not handle the drop with the correct effect");
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (workspace.IsBusy && DateTime.UtcNow < timeout) await Task.Delay(20);
        Require(!workspace.IsBusy, "category update timed out");
    }

    private static DataObject Files(params string[] paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return data;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CreateShortcut(string path, string target)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = ((dynamic)shell).CreateShortcut(path);
        try { shortcut.TargetPath = target; shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }

    private static LocalBackend CreateBackend(DataPaths paths, IShortcutTargetResolver shortcutTargetResolver)
    {
        var connections = new SqliteConnectionFactory(paths);
        var backup = new SqliteBackupService(paths, connections);
        var store = new SqliteWorkspaceStore(connections);
        return (LocalBackend)typeof(LocalBackend).GetConstructors(PrivateInstance).Single().Invoke(new object[]
        {
            paths, new SqliteStorageInitializer(paths, connections, backup), store,
            new JsonSettingsStore(paths.RootDirectory), backup,
            new DataTransferService(paths, connections, backup), new CategoryService(store),
            new DesktopItemService(store, paths.ManagedStorageDirectory, shortcutTargetResolver), new LayoutService(store), new FileSystemChangeMonitor()
        });
    }

    private sealed class NoDesktopHost : IDesktopHostService
    {
        public bool TryAttach(IntPtr handle) => false;
        public bool TryDetach(IntPtr handle) => true;
        public bool IsAttached(IntPtr handle) => false;
    }

    private sealed class NoShell : IShellService
    {
        public Task OpenAsync(string path, CancellationToken token = default) => Task.CompletedTask;
        public Task RevealInExplorerAsync(string path, CancellationToken token = default) => Task.CompletedTask;
        public Task<bool> DeleteToRecycleBinAsync(string path, IntPtr ownerWindowHandle, CancellationToken token = default) => Task.FromResult(false);
        public bool ShowContextMenu(string path, IntPtr handle, int x, int y) => false;
    }
}
