using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using DeskZone.App.ViewModels;
using DeskZone.Core.Models;
using DeskZone.Core.Services;
using DeskZone.Storage;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskZone.App;

public partial class MainWindow : Window
{
    private static readonly Guid PanelId = Guid.Parse("8577142a-f767-41df-b3a2-5b07410f9181");
    private const double StablePanelOpacity = 1.0;
    private const string InternalItemDataFormat = "DeskZone.ItemId";
    private const string InternalItemCategoryDataFormat = "DeskZone.ItemCategoryId";
    private const string InternalCategoryDataFormat = "DeskZone.CategoryId";
    private const string CloseToTraySettingKey = "close-to-tray";
    private const string StartWithWindowsSettingKey = "start-with-windows";
    private const string TransparentModeSettingKey = "transparent-mode";
    private const string WindowsStartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsStartupRegistryValueName = "DeskZone";
    private const int WmSize = 0x0005;
    private const int SizeMinimized = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ScreenPoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maxCount);

    private readonly LocalBackend _backend;
    private readonly IShellService _shell;
    private readonly WorkspaceViewModel _viewModel;
    private readonly IDesktopHostService _desktopHost;
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly DispatcherTimer _desktopHostTimer;
    private readonly DispatcherTimer _desktopRestoreTimer;
    private readonly DispatcherTimer _categoryClickTimer;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;
    private HwndSource? _windowSource;

    private bool _applyingLayout;
    private bool _allowClose;
    private bool _handlingWindowClose;
    private bool _isTitleBarDragging;
    private ScreenPoint _titleBarDragStartCursor;
    private WindowRect _titleBarDragStartWindow;
    private bool _desktopPinned;
    private bool _userRequestedMinimize;
    private bool _desktopRestorePending;
    private bool _isPanelCollapsed;
    private bool _closeToTray = true;
    private bool _startWithWindows;
    private bool _transparentMode = true;
    private double _expandedHeight = 650;
    private bool _applyingSettings;
    private bool _locked;
    private DesktopItemViewModel? _dragCandidate;
    private Point _dragStartPoint;
    private CategoryViewModel? _categoryDragCandidate;
    private Point _categoryDragStartPoint;
    private CategoryViewModel? _pendingCategoryToggle;
    private bool _categoryShiftClick;
    private DateTimeOffset _panelCreatedAt = DateTimeOffset.UtcNow;

    public MainWindow(
        LocalBackend backend,
        IShellService shell,
        IDesktopHostService desktopHost)
    {
        InitializeComponent();

        _backend = backend;
        _shell = shell;
        _desktopHost = desktopHost;
        _viewModel = new WorkspaceViewModel(backend, shell);
        DataContext = _viewModel;

        _layoutSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _layoutSaveTimer.Tick += async (_, _) =>
        {
            _layoutSaveTimer.Stop();
            await SaveLayoutAsync();
        };

        _desktopHostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _desktopHostTimer.Tick += (_, _) => EnsureDesktopAttachment();

        _desktopRestoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(240)
        };
        _desktopRestoreTimer.Tick += (_, _) => RestoreDesktopPinnedWindow();

        _categoryClickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime())
        };
        _categoryClickTimer.Tick += CategoryClickTimer_Tick;

        InitializeTrayIcon();
        Closed += (_, _) => DisposeTrayIcon();
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(MainWindowMessageHook);
    }

    private IntPtr MainWindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == App.ShowExistingMessageId)
        {
            ShowFromTray();
            handled = true;
        }
        else if (message == WmSize && wParam.ToInt64() == SizeMinimized)
        {
            ScheduleDesktopWindowRestore();
        }

        return IntPtr.Zero;
    }

    private void InitializeTrayIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        try
        {
            _trayIconImage = Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (_trayIconImage is null)
            {
                return;
            }

            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.Items.Add("打开 DeskZone", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add("退出 DeskZone", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "DeskZone",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        }
        catch
        {
            DisposeTrayIcon();
        }
    }

    private void ShowFromTray()
    {
        if (_handlingWindowClose || _allowClose)
        {
            return;
        }

        _userRequestedMinimize = false;
        _desktopRestoreTimer.Stop();
        _desktopRestorePending = false;
        EnsureWindowVisible();
        if (_desktopPinned)
        {
            EnsureDesktopAttachment();
            _desktopHostTimer.Start();
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!_desktopPinned || !_desktopHost.IsAttached(handle))
        {
            ShowInTaskbar = true;
            Activate();
        }
    }

    private async Task HideToTrayAsync()
    {
        _layoutSaveTimer.Stop();
        _desktopHostTimer.Stop();
        _desktopRestoreTimer.Stop();
        await SaveLayoutAsync();
        ShowInTaskbar = false;
        Hide();
    }

    private async void ExitFromTray()
    {
        if (_allowClose || _handlingWindowClose)
        {
            return;
        }

        _allowClose = true;
        _layoutSaveTimer.Stop();
        _desktopHostTimer.Stop();
        await SaveLayoutAsync();
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();

            var savedTransparentMode = await _backend.Settings.LoadAsync<bool?>(TransparentModeSettingKey);
            _transparentMode = savedTransparentMode ?? true;
            ApplyAppearance(_transparentMode);

            // DeskZone is always a desktop component. There is no alternate
            // floating-window mode, including when an older shortcut passes
            // the former --preview argument.
            _desktopPinned = true;
            // The desktop host controls the component layer. Do not enter the
            // global topmost band, even briefly during startup.
            Topmost = false;

            var savedCloseToTray = await _backend.Settings.LoadAsync<bool?>(CloseToTraySettingKey);
            _closeToTray = savedCloseToTray ?? true;
            await RestoreStartWithWindowsPreferenceAsync();

            var panel = await _backend.Layout.GetPanelAsync(PanelId);
            _panelCreatedAt = panel.CreatedAt;

            _applyingLayout = true;
            Width = Math.Max(MinWidth, panel.WidthDip);
            Height = Math.Max(430, panel.HeightDip);
            _expandedHeight = Height;
            // A translucent layered window exposes changing content behind the desktop panel
            // and looks like flickering/ghost frames. Keep the rounded surface opaque.
            Opacity = StablePanelOpacity;
            ApplyLockedState(panel.IsLocked, scheduleSave: false);
            ApplyPanelCollapsed(panel.IsCollapsed, restoreExpandedHeight: false);

            if (_desktopPinned)
            {
                Left = panel.LeftDip;
                Top = panel.TopDip;
                // Apply WPF's taskbar state before the native desktop host is
                // assigned. Changing this after hosting overwrites the native
                // owner window and sends the component behind Explorer again.
                ShowInTaskbar = false;
                EnsureDesktopAttachment();
                _desktopHostTimer.Start();
                // Give WPF one turn to finalize its native window state, then
                // establish the desktop layer once. This is deliberately not
                // a recurring raise, so screenshot overlays stay above us.
                await Task.Delay(650);
                RefreshDesktopAttachmentAfterLoad();
            }
            else
            {
                Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
                Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
                ShowInTaskbar = true;
            }
        }
        catch (Exception ex)
        {
            ShowError("加载工作区失败", ex);
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    private void Window_LayoutChanged(object? sender, EventArgs e)
    {
        ScheduleLayoutSave();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_closeToTray)
        {
            _allowClose = true;
            _layoutSaveTimer.Stop();
            _desktopHostTimer.Stop();
            _ = SaveLayoutAsync();
            return;
        }

        e.Cancel = true;
        if (_handlingWindowClose)
        {
            return;
        }

        _handlingWindowClose = true;
        try
        {
            await HideToTrayAsync();
        }
        finally
        {
            _handlingWindowClose = false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out _titleBarDragStartCursor) ||
            !GetWindowRect(handle, out _titleBarDragStartWindow))
        {
            return;
        }

        _isTitleBarDragging = true;
        Mouse.Capture(TitleBar, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTitleBarDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTitleBarDrag();
            return;
        }

        if (GetCursorPos(out var cursor))
        {
            var handle = new WindowInteropHelper(this).Handle;
            var x = _titleBarDragStartWindow.Left + cursor.X - _titleBarDragStartCursor.X;
            var y = _titleBarDragStartWindow.Top + cursor.Y - _titleBarDragStartCursor.Y;
            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpShowWindow);
        }

        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isTitleBarDragging)
        {
            return;
        }

        EndTitleBarDrag();
        e.Handled = true;
    }

    private void EndTitleBarDrag()
    {
        _isTitleBarDragging = false;
        if (Mouse.Captured == TitleBar)
        {
            Mouse.Capture(null);
        }

        ScheduleLayoutSave();
    }

    private async void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CreateCategoryAsync();
            CategoryScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            ShowError("创建分类失败", ex);
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(SearchTextBox.Text))
        {
            return;
        }

        SearchTextBox.Clear();
        e.Handled = true;
    }

    private void NewCategory_DragEnter(object sender, DragEventArgs e) =>
        UpdateNewCategoryDropVisual(e);

    private void NewCategory_DragOver(object sender, DragEventArgs e) =>
        UpdateNewCategoryDropVisual(e);

    private void NewCategory_DragLeave(object sender, DragEventArgs e)
    {
        ResetNewCategoryDropVisual();
    }

    private async void NewCategory_Drop(object sender, DragEventArgs e)
    {
        ResetNewCategoryDropVisual();

        if (!TryGetDroppedFolder(e, out var folderPath))
        {
            return;
        }

        try
        {
            await _viewModel.CreateCategoryFromFolderAsync(folderPath);
            CategoryScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            ShowError("从文件夹创建分类失败", ex);
        }
    }

    private async void ToggleCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryViewModel category)
        {
            return;
        }

        try
        {
            await HandleCategoryToggleAsync(category, IsShiftPressed());
        }
        catch (Exception ex)
        {
            ShowError("更新分类失败", ex);
        }
    }

    private static bool IsShiftPressed() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private async Task HandleCategoryToggleAsync(CategoryViewModel category, bool toggleAll)
    {
        if (toggleAll)
        {
            await _viewModel.ToggleAllCategoriesAsync();
            return;
        }

        await _viewModel.ToggleCategoryAsync(category);
    }

    private void CategoryHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _categoryShiftClick = false;

        if (sender is not FrameworkElement header || header.Tag is not CategoryViewModel category)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var textBox = FindAncestor<TextBox>(source);
        if (FindAncestor<Button>(source) is not null || textBox is not null && !textBox.IsReadOnly)
        {
            return;
        }

        _categoryDragCandidate = category;
        _categoryDragStartPoint = e.GetPosition(this);
        _categoryShiftClick = IsShiftPressed();
    }

    private async void CategoryHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var shiftClick = _categoryShiftClick || IsShiftPressed();
        _categoryShiftClick = false;
        var category = _categoryDragCandidate;
        _categoryDragCandidate = null;

        if (category is null)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var textBox = FindAncestor<TextBox>(source);
        if (FindAncestor<Button>(source) is not null || textBox is not null && !textBox.IsReadOnly)
        {
            return;
        }

        if (textBox is not null)
        {
            if (shiftClick)
            {
                CancelPendingCategoryToggle();
                await HandleCategoryToggleFromInputAsync(category, toggleAll: true);
            }
            else
            {
                ScheduleCategoryToggle(category);
            }

            return;
        }

        try
        {
            await HandleCategoryToggleAsync(category, shiftClick);
        }
        catch (Exception ex)
        {
            ShowError("更新分类失败", ex);
        }
    }

    private void CategoryHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_categoryDragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (_categoryDragCandidate.IsSystem)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _categoryDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _categoryDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var category = _categoryDragCandidate;
        _categoryDragCandidate = null;
        _categoryShiftClick = false;

        var data = new DataObject();
        data.SetData(InternalCategoryDataFormat, category.Id.ToString("D"));
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released while the drag loop is starting.
        }

        e.Handled = true;
    }

    private void CategoryName_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not CategoryViewModel category || !textBox.IsReadOnly)
        {
            return;
        }

        if (IsShiftPressed())
        {
            _categoryShiftClick = true;
            _categoryDragCandidate = category;
            _categoryDragStartPoint = e.GetPosition(this);
            e.Handled = true;
            return;
        }

        if (category.IsSystem)
        {
            if (e.ClickCount == 1)
            {
                _categoryDragCandidate = category;
                _categoryDragStartPoint = e.GetPosition(this);
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            BeginCategoryNameEdit(textBox);
        }
        else
        {
            _categoryDragCandidate = category;
            _categoryDragStartPoint = e.GetPosition(this);
        }

        e.Handled = true;
    }

    private void CategoryName_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not CategoryViewModel category || !textBox.IsReadOnly)
        {
            return;
        }

        var candidate = _categoryDragCandidate;
        var shiftClick = _categoryShiftClick || IsShiftPressed();
        _categoryDragCandidate = null;
        _categoryShiftClick = false;
        if (candidate is not null)
        {
            if (shiftClick)
            {
                CancelPendingCategoryToggle();
                _ = HandleCategoryToggleFromInputAsync(category, toggleAll: true);
            }
            else
            {
                ScheduleCategoryToggle(category);
            }
        }

        e.Handled = true;
    }

    private async Task HandleCategoryToggleFromInputAsync(CategoryViewModel category, bool toggleAll)
    {
        try
        {
            await HandleCategoryToggleAsync(category, toggleAll);
        }
        catch (Exception ex)
        {
            ShowError("更新分类失败", ex);
        }
    }

    private void BeginCategoryNameEdit(TextBox textBox)
    {
        if (textBox.Tag is CategoryViewModel { IsSystem: true })
        {
            return;
        }

        CancelPendingCategoryToggle();
        _categoryDragCandidate = null;
        textBox.IsReadOnly = false;
        textBox.Cursor = Cursors.IBeam;
        textBox.Focus();
        textBox.SelectAll();
    }

    private void ScheduleCategoryToggle(CategoryViewModel category)
    {
        _pendingCategoryToggle = category;
        _categoryClickTimer.Stop();
        _categoryClickTimer.Start();
    }

    private void CancelPendingCategoryToggle()
    {
        _categoryClickTimer.Stop();
        _pendingCategoryToggle = null;
    }

    private async void CategoryClickTimer_Tick(object? sender, EventArgs e)
    {
        _categoryClickTimer.Stop();
        var category = _pendingCategoryToggle;
        _pendingCategoryToggle = null;
        if (category is null)
        {
            return;
        }

        try
        {
            await _viewModel.ToggleCategoryAsync(category);
        }
        catch (Exception ex)
        {
            ShowError("更新分类失败", ex);
        }
    }

    private async void CategoryName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not CategoryViewModel category)
        {
            return;
        }

        if (textBox.IsReadOnly)
        {
            return;
        }

        try
        {
            await _viewModel.RenameCategoryAsync(category, textBox.Text);
        }
        catch (Exception ex)
        {
            textBox.Text = category.PersistedName;
            ShowError("重命名分类失败", ex);
        }
        finally
        {
            textBox.IsReadOnly = true;
            textBox.Cursor = Cursors.SizeAll;
        }
    }

    private void CategoryName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not CategoryViewModel category)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            textBox.Text = category.PersistedName;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private static CategoryViewModel? GetCategoryFromMenu(object sender)
    {
        return sender is System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu { PlacementTarget: FrameworkElement { Tag: CategoryViewModel category } } }
            ? category
            : null;
    }

    private async void CategoryMenuToggle_Click(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromMenu(sender);
        if (category is null)
        {
            return;
        }

        try
        {
            await _viewModel.ToggleCategoryAsync(category);
        }
        catch (Exception ex)
        {
            ShowError("更新分类失败", ex);
        }
    }

    private void CategoryMenuRename_Click(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromMenu(sender);
        if (category is null || category.IsSystem)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                var textBox = FindVisualDescendant<TextBox>(
                    CategoryScroll,
                    candidate => ReferenceEquals(candidate.Tag, category));
                if (textBox is not null)
                {
                    BeginCategoryNameEdit(textBox);
                }
            }));
    }

    private void CategoryMenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromMenu(sender);
        if (category is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(category.Name);
        }
        catch (Exception ex)
        {
            ShowError("复制分类名称失败", ex);
        }
    }

    private async void CategoryMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var category = GetCategoryFromMenu(sender);
        if (category is not null)
        {
            await DeleteCategoryAsync(category);
        }
    }

    private async Task DeleteCategoryAsync(CategoryViewModel category)
    {
        if (category.IsSystem)
        {
            return;
        }

        var moveItems = category.Items.Count > 0;
        var message = moveItems
            ? $"“{category.Name}”中有 {category.Items.Count} 个入口。\n\n删除分类后，这些入口会移到系统“未分类”，真实文件不会被移动或删除。继续吗？"
            : $"删除空分类“{category.Name}”？";

        if (MessageBox.Show(message, "删除分类", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.DeleteCategoryAsync(category, moveItems);
        }
        catch (Exception ex)
        {
            ShowError("删除分类失败", ex);
        }
    }

    private void Category_DragEnter(object sender, DragEventArgs e) =>
        UpdateCategoryDropVisual(sender, e);

    private void Category_DragOver(object sender, DragEventArgs e) =>
        UpdateCategoryDropVisual(sender, e);

    private static bool HasFileDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
        paths.Length > 0;

    private static bool TryGetDraggedCategoryId(DragEventArgs e, out Guid categoryId)
    {
        categoryId = Guid.Empty;
        return e.Data.GetDataPresent(InternalCategoryDataFormat) &&
               Guid.TryParse(e.Data.GetData(InternalCategoryDataFormat) as string, out categoryId);
    }

    private static bool TryGetDraggedItemId(DragEventArgs e, out Guid itemId)
    {
        itemId = Guid.Empty;
        return e.Data.GetDataPresent(InternalItemDataFormat) &&
               Guid.TryParse(e.Data.GetData(InternalItemDataFormat) as string, out itemId);
    }

    private static bool TryGetDraggedItemCategoryId(DragEventArgs e, out Guid categoryId)
    {
        categoryId = Guid.Empty;
        return e.Data.GetDataPresent(InternalItemCategoryDataFormat) &&
               Guid.TryParse(e.Data.GetData(InternalItemCategoryDataFormat) as string, out categoryId);
    }

    private void UpdateCategoryDropVisual(object sender, DragEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (border.Tag is CategoryViewModel { IsSystem: true })
        {
            e.Effects = DragDropEffects.None;
            ResetDropVisual(border);
            return;
        }

        if (TryGetDraggedCategoryId(e, out _) || HasFileDrop(e))
        {
            e.Effects = DragDropEffects.Move;
            border.BorderBrush = (Brush)FindResource("SkyBrush");
            border.BorderThickness = new Thickness(1);
            border.Background = new SolidColorBrush(Color.FromRgb(238, 248, 255));
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ResetDropVisual(border);
        }
    }

    private void Category_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropVisual(sender as Border);
    }

    private async void Category_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not CategoryViewModel category || category.IsSystem)
        {
            return;
        }

        ResetDropVisual(border);

        if (TryGetDraggedCategoryId(e, out var sourceCategoryId))
        {
            try
            {
                await _viewModel.MoveCategoryAsync(sourceCategoryId, category.Id);
            }
            catch (Exception ex)
            {
                ShowError("调整分类顺序失败", ex);
            }

            return;
        }

        if (e.Data.GetDataPresent(InternalItemDataFormat) &&
            Guid.TryParse(e.Data.GetData(InternalItemDataFormat) as string, out var itemId))
        {
        if (e.Data.GetDataPresent(InternalItemCategoryDataFormat) &&
                Guid.TryParse(e.Data.GetData(InternalItemCategoryDataFormat) as string, out var sourceItemCategoryId) &&
                sourceItemCategoryId == category.Id)
            {
                var sourceItem = category.Items.FirstOrDefault(item => item.Id == itemId);
                var sourceIndex = sourceItem is null ? -1 : category.Items.IndexOf(sourceItem);
                try
                {
                    await _viewModel.ReorderItemsAsync(category, sourceIndex, category.Items.Count - 1);
                }
                catch (Exception ex)
                {
                    ShowError("调整项目顺序失败", ex);
                }

                return;
            }

            try
            {
                await _viewModel.MoveItemToCategoryAsync(itemId, category);
            }
            catch (Exception ex)
            {
                ShowError("移动分类项目失败", ex);
            }

            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        try
        {
            await _viewModel.MovePathsIntoCategoryAsync(category, paths);
        }
        catch (Exception ex)
        {
            ShowError("移动文件失败", ex);
        }
    }

    private async void FileCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DesktopItemViewModel item)
        {
            return;
        }

        if (e.ClickCount == 1 && !item.IsRecentItem)
        {
            _dragCandidate = item;
            _dragStartPoint = e.GetPosition(this);
            return;
        }

        _dragCandidate = null;
        if (e.ClickCount != 2)
        {
            return;
        }

        try
        {
            await _viewModel.OpenItemAsync(item);
        }
        catch (Exception ex)
        {
            ShowError("打开文件失败", ex);
        }
    }

    private void FileCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
    }

    private void FileCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not DesktopItemViewModel item)
        {
            return;
        }

        var screenPoint = border.PointToScreen(e.GetPosition(border));
        var shown = _shell.ShowContextMenu(
            item.Path,
            new WindowInteropHelper(this).Handle,
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y));
        e.Handled = shown;
    }

    private void FileCard_DragEnter(object sender, DragEventArgs e) =>
        UpdateFileCardDropVisual(sender, e);

    private void FileCard_DragOver(object sender, DragEventArgs e) =>
        UpdateFileCardDropVisual(sender, e);

    private void FileCard_DragLeave(object sender, DragEventArgs e)
    {
        ResetFileCardDropVisual(sender as Border);
    }

    private void UpdateFileCardDropVisual(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not DesktopItemViewModel targetItem ||
            !TryGetDraggedItemId(e, out var sourceItemId) ||
            !TryGetDraggedItemCategoryId(e, out var sourceCategoryId) ||
            sourceCategoryId != targetItem.Model.CategoryId)
        {
            e.Effects = DragDropEffects.None;
            ResetFileCardDropVisual(sender as Border);
            return;
        }

        e.Effects = DragDropEffects.Move;
        border.BorderBrush = (Brush)FindResource("SkyBrush");
        border.BorderThickness = new Thickness(1);
        border.Background = new SolidColorBrush(Color.FromRgb(238, 248, 255));
        e.Handled = true;
    }

    private async void FileCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not DesktopItemViewModel targetItem ||
            !TryGetDraggedItemId(e, out var sourceItemId) ||
            !TryGetDraggedItemCategoryId(e, out var sourceCategoryId) ||
            sourceCategoryId != targetItem.Model.CategoryId)
        {
            ResetFileCardDropVisual(sender as Border);
            return;
        }

        ResetFileCardDropVisual(border);
        e.Handled = true;
        if (sourceItemId == targetItem.Id)
        {
            return;
        }

        var category = _viewModel.Categories.FirstOrDefault(itemCategory => itemCategory.Id == targetItem.Model.CategoryId);
        var sourceItem = category?.Items.FirstOrDefault(item => item.Id == sourceItemId);
        var targetIndex = category?.Items.IndexOf(targetItem) ?? -1;
        var sourceIndex = sourceItem is null || category is null ? -1 : category.Items.IndexOf(sourceItem);
        if (category is null || sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        try
        {
            await _viewModel.ReorderItemsAsync(category, sourceIndex, targetIndex);
        }
        catch (Exception ex)
        {
            ShowError("调整项目顺序失败", ex);
        }
    }

    private async void FileCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || _dragCandidate.IsRecentItem || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _dragCandidate;
        _dragCandidate = null;
        if (item.IsMissing || (!File.Exists(item.Path) && !Directory.Exists(item.Path)))
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { item.Path });
        data.SetData(InternalItemDataFormat, item.Id.ToString("D"));
        data.SetData(InternalItemCategoryDataFormat, item.Model.CategoryId.ToString("D"));

        try
        {
            var effect = DragDrop.DoDragDrop(
                (DependencyObject)sender,
                data,
                DragDropEffects.Move);

            if (effect == DragDropEffects.Move && IsDesktopDropTarget())
            {
                await _viewModel.MoveItemToDesktopAsync(item);
            }
        }
        catch (Exception ex)
        {
            ShowError("移动项目到桌面失败", ex);
        }

        e.Handled = true;
    }

    private static bool IsDesktopDropTarget()
    {
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var windowHandle = WindowFromPoint(point);
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(64);
        if (GetClassName(windowHandle, className, className.Capacity) == 0)
        {
            return false;
        }

        return className.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    private void Lock_Click(object sender, RoutedEventArgs e) =>
        ApplyLockedState(!_locked);

    private async void Transparency_Click(object sender, RoutedEventArgs e)
    {
        _transparentMode = !_transparentMode;
        ApplyAppearance(_transparentMode);

        try
        {
            await _backend.Settings.SaveAsync(TransparentModeSettingKey, _transparentMode);
        }
        catch (Exception ex)
        {
            ShowError("保存显示样式失败", ex);
        }
    }

    private void ApplyAppearance(bool transparent)
    {
        SetAppearanceBrush("ShellSurfaceBrush", transparent ? Colors.Transparent : Colors.White);
        SetAppearanceBrush("ShellBorderBrush", transparent
            ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0xD7, 0xE8, 0xF6));
        SetAppearanceBrush("TitlePrimaryBrush", transparent
            ? Colors.White
            : Color.FromRgb(0x0B, 0x24, 0x47));
        SetAppearanceBrush("TitleSecondaryBrush", transparent
            ? Colors.White
            : Color.FromRgb(0x58, 0x69, 0x81));
        SetAppearanceBrush("ActionBrush", transparent
            ? Colors.White
            : Color.FromRgb(0x12, 0x6F, 0xF7));
        SetAppearanceBrush("CategoryMetaBrush", transparent
            ? Colors.White
            : Color.FromRgb(0x58, 0x69, 0x81));
        SetAppearanceBrush("ContentPrimaryBrush", transparent
            ? Colors.White
            : Color.FromRgb(0x0B, 0x24, 0x47));
        SetAppearanceBrush("CategorySurfaceBrush", transparent
            ? Color.FromArgb(0x33, 0xFC, 0xFE, 0xFF)
            : Color.FromRgb(0xFC, 0xFE, 0xFF));
        SetAppearanceBrush("CategoryHeaderSurfaceBrush", transparent
            ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)
            : Colors.White);
        SetAppearanceBrush("CategoryBorderBrush", transparent
            ? Colors.Transparent
            : Color.FromRgb(0xE5, 0xEE, 0xF6));
        // File objects stay lightweight in both appearances: show only the
        // icon and label without an individual tile background or outline.
        SetAppearanceBrush("FileItemSurfaceBrush", Colors.Transparent);
        SetAppearanceBrush("FileItemBorderBrush", Colors.Transparent);
        SetAppearanceBrush("NewCategorySurfaceBrush", transparent
            ? Color.FromArgb(0x33, 0xFC, 0xFE, 0xFF)
            : Color.FromRgb(0xEE, 0xF8, 0xFF));
        SetAppearanceBrush("NewCategoryBorderBrush", transparent
            ? Colors.Transparent
            : Color.FromRgb(0x79, 0xB8, 0xF8));
        SetAppearanceBrush("SettingsDialogSurfaceBrush", transparent
            ? Color.FromArgb(0xB8, 0xFC, 0xFE, 0xFF)
            : Colors.White);
        SetAppearanceBrush("SettingsDialogBorderBrush", transparent
            ? Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0xD9, 0xE8, 0xF5));
        SetAppearanceBrush("SettingsItemSurfaceBrush", transparent
            ? Color.FromArgb(0x55, 0xFC, 0xFE, 0xFF)
            : Color.FromRgb(0xF7, 0xFB, 0xFF));
        SetAppearanceBrush("SettingsItemBorderBrush", transparent
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0xE1, 0xEC, 0xF6));
        SetAppearanceBrush("SettingsActionSurfaceBrush", transparent
            ? Color.FromArgb(0x33, 0xEE, 0xF8, 0xFF)
            : Color.FromRgb(0xEE, 0xF8, 0xFF));
        SetAppearanceBrush("SettingsActionBorderBrush", transparent
            ? Color.FromArgb(0x80, 0xA9, 0xD3, 0xFA)
            : Color.FromRgb(0xA9, 0xD3, 0xFA));
        SetAppearanceBrush("SettingsResetBorderBrush", transparent
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0xC9, 0xD9, 0xE8));

        Resources["CategoryBorderThickness"] = new Thickness(transparent ? 0 : 1);
        Resources["FileItemBorderThickness"] = new Thickness(0);
        Resources["NewCategoryBorderThickness"] = new Thickness(transparent ? 0 : 1);

        TransparencyButton.ToolTip = transparent ? "切换为白色显示" : "切换为透明显示";
        TransparencyButton.Foreground = (Brush)FindResource("TitleSecondaryBrush");
    }

    private void SetAppearanceBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        ApplyPanelCollapsed(!_isPanelCollapsed);
    }

    private void ApplyPanelCollapsed(bool collapsed, bool restoreExpandedHeight = true)
    {
        _isPanelCollapsed = collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;

        if (collapsed)
        {
            _expandedHeight = Math.Max(430, ActualHeight > 0 ? ActualHeight : Height);
            WorkspaceBody.Visibility = Visibility.Collapsed;
            MinHeight = 56;
            Height = 56;
            MinimizeButton.Content = "\uE70E";
            MinimizeButton.ToolTip = "展开组件";
            SetResizeHandlesEnabled(false);
        }
        else
        {
            WorkspaceBody.Visibility = Visibility.Visible;
            MinHeight = 430;
            if (restoreExpandedHeight)
            {
                Height = Math.Max(MinHeight, _expandedHeight);
            }

            MinimizeButton.Content = "\uE921";
            MinimizeButton.ToolTip = "折叠为标题栏";
            SetResizeHandlesEnabled(!_locked);
        }

        ScheduleLayoutSave();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Refresh the dialog resources when it is opened so the preview always
        // follows the current appearance mode, including after an external
        // settings reload or a long-lived desktop-component session.
        ApplyAppearance(_transparentMode);
        SettingsOverlay.Visibility = Visibility.Visible;
        SyncSettingsControls();
    }

    private void SettingsOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SettingsDialog is null || e.NewSize.Width <= 0)
        {
            return;
        }

        // Keep the dialog exactly centered inside the same content area as the
        // desktop component. The overlay has symmetric padding, so the dialog
        // width must match the available inner width after that padding.
        SettingsDialog.Width = Math.Max(0, e.NewSize.Width - SettingsOverlay.Padding.Left - SettingsOverlay.Padding.Right);
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsLockPosition_Click(object sender, RoutedEventArgs e)
    {
        if (!_applyingSettings && sender is System.Windows.Controls.CheckBox toggle)
        {
            ApplyLockedState(toggle.IsChecked == true);
            SyncSettingsControls();
        }
    }

    private async void SettingsCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || sender is not System.Windows.Controls.CheckBox toggle)
        {
            return;
        }

        _closeToTray = toggle.IsChecked == true;
        try
        {
            await _backend.Settings.SaveAsync(CloseToTraySettingKey, _closeToTray);
        }
        catch (Exception ex)
        {
            ShowError("保存关闭行为失败", ex);
        }

        SyncSettingsControls();
    }

    private async void SettingsStartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || sender is not System.Windows.Controls.CheckBox toggle)
        {
            return;
        }

        var requestedValue = toggle.IsChecked == true;
        var previousValue = _startWithWindows;
        try
        {
            SetWindowsStartupRegistration(requestedValue);
            await _backend.Settings.SaveAsync(StartWithWindowsSettingKey, requestedValue);
            _startWithWindows = requestedValue;
        }
        catch (Exception ex)
        {
            try
            {
                SetWindowsStartupRegistration(previousValue);
            }
            catch
            {
                // The original error is the useful one to show to the user.
            }

            _startWithWindows = previousValue;
            ShowError("设置开机自动启动失败", ex);
        }

        SyncSettingsControls();
    }

    private void SettingsOpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _backend.Paths.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("打开数据文件夹失败", ex);
        }
    }

    private async void SettingsExportData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "导出 DeskZone 数据",
            Filter = "DeskZone 数据文件 (*.deskzone.zip)|*.deskzone.zip|ZIP 文件 (*.zip)|*.zip",
            DefaultExt = ".deskzone.zip",
            AddExtension = true,
            FileName = $"DeskZone-数据-{DateTime.Now:yyyyMMdd-HHmmss}.deskzone.zip",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _backend.DataTransfer.ExportAsync(dialog.FileName);
            MessageBox.Show(
                this,
                "数据已导出。分类、快捷方式引用、布局和应用设置都已包含在数据文件中。",
                "导出完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("导出数据失败", ex);
        }
    }

    private async void SettingsImportData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "导入 DeskZone 数据",
            Filter = "DeskZone 数据文件 (*.deskzone.zip;*.zip)|*.deskzone.zip;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "导入会替换当前分类、快捷方式引用、布局和应用设置。导入前会自动备份当前数据，是否继续？",
            "确认导入数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _backend.DataTransfer.ImportAsync(dialog.FileName);
            await _backend.Initializer.InitializeAsync();
            await ApplyImportedStateAsync();

            SettingsOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(
                this,
                "数据已导入并生效。原有数据已在本地备份中保留。",
                "导入完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("导入数据失败，原有数据可能未被修改", ex);
        }
    }

    private async Task ApplyImportedStateAsync()
    {
        var savedTransparentMode = await _backend.Settings.LoadAsync<bool?>(TransparentModeSettingKey);
        _transparentMode = savedTransparentMode ?? _transparentMode;
        ApplyAppearance(_transparentMode);

        var savedCloseToTray = await _backend.Settings.LoadAsync<bool?>(CloseToTraySettingKey);
        _closeToTray = savedCloseToTray ?? _closeToTray;
        await RestoreStartWithWindowsPreferenceAsync();

        var panel = await _backend.Layout.GetPanelAsync(PanelId);
        if (panel is not null)
        {
            _applyingLayout = true;
            try
            {
                Width = Math.Max(MinWidth, panel.WidthDip);
                Height = Math.Max(430, panel.HeightDip);
                _expandedHeight = Height;
                Left = panel.LeftDip;
                Top = panel.TopDip;
                _panelCreatedAt = panel.CreatedAt;
                ApplyLockedState(panel.IsLocked, scheduleSave: false);
                ApplyPanelCollapsed(panel.IsCollapsed, restoreExpandedHeight: false);
            }
            finally
            {
                _applyingLayout = false;
            }
        }

        await _viewModel.ReloadFromStorageAsync();
        SyncSettingsControls();
        EnsureDesktopAttachment();
    }

    private void SettingsRestoreSize_Click(object sender, RoutedEventArgs e)
    {
        Width = Math.Max(MinWidth, 380);
        Height = Math.Max(MinHeight, 650);
        if (!_desktopPinned)
        {
            Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
        }

        ScheduleLayoutSave();
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_closeToTray)
        {
            await HideToTrayAsync();
        }
        else
        {
            ExitFromTray();
        }
    }

    private void SyncSettingsControls()
    {
        if (SettingsOverlay is null)
        {
            return;
        }

        _applyingSettings = true;
        try
        {
            SettingsLockPositionToggle.IsChecked = _locked;
            SettingsCloseToTrayToggle.IsChecked = _closeToTray;
            SettingsStartWithWindowsToggle.IsChecked = _startWithWindows;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private async Task RestoreStartWithWindowsPreferenceAsync()
    {
        var savedValue = await _backend.Settings.LoadAsync<bool?>(StartWithWindowsSettingKey);
        if (savedValue is null)
        {
            _startWithWindows = IsWindowsStartupRegistered();
            return;
        }

        try
        {
            SetWindowsStartupRegistration(savedValue.Value);
            _startWithWindows = savedValue.Value;
        }
        catch
        {
            // A stale or manually edited startup entry should not prevent DeskZone
            // from opening. Keep the setting UI aligned with the actual registry state.
            _startWithWindows = IsWindowsStartupRegistered();
        }
    }

    private static bool IsWindowsStartupRegistered()
    {
        using var startupKey = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistryPath, writable: false);
        return startupKey?.GetValue(WindowsStartupRegistryValueName) is string command &&
            !string.IsNullOrWhiteSpace(command);
    }

    private static void SetWindowsStartupRegistration(bool enabled)
    {
        if (!enabled)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistryPath, writable: true);
            existingKey?.DeleteValue(WindowsStartupRegistryValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidOperationException("无法确定 DeskZone 的可执行文件路径。");
        }

        using var startupKey = Registry.CurrentUser.CreateSubKey(WindowsStartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法访问 Windows 的开机启动配置。");
        startupKey.SetValue(
            WindowsStartupRegistryValueName,
            $"\"{executablePath}\"",
            RegistryValueKind.String);
    }

    private void ApplyLockedState(bool locked, bool scheduleSave = true)
    {
        _locked = locked;
        LockButton.Content = locked ? "\uE72E" : "\uE785";
        LockButton.ToolTip = locked ? "解锁位置" : "锁定位置";
        ApplyResizeMode();

        if (scheduleSave)
        {
            ScheduleLayoutSave();
        }
    }

    private void ApplyResizeMode()
    {
        var canResize = !_locked;
        ResizeMode = canResize ? ResizeMode.CanResize : ResizeMode.NoResize;
        SetResizeHandlesEnabled(canResize);
    }

    private void SetResizeHandlesEnabled(bool enabled)
    {
        var visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        foreach (var handle in new Thumb[]
                 {
                     ResizeTopThumb,
                     ResizeBottomThumb,
                     ResizeLeftThumb,
                     ResizeRightThumb,
                     ResizeTopLeftThumb,
                     ResizeTopRightThumb,
                     ResizeBottomLeftThumb,
                     ResizeThumb
                 })
        {
            handle.Visibility = visibility;
            handle.IsEnabled = enabled;
        }
    }

    private void ResizeTopThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: true, resizeRight: false, resizeBottom: false);

    private void ResizeBottomThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: false, resizeRight: false, resizeBottom: true);

    private void ResizeLeftThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: false, resizeRight: false, resizeBottom: false);

    private void ResizeRightThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: false, resizeRight: true, resizeBottom: false);

    private void ResizeTopLeftThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: true, resizeRight: false, resizeBottom: false);

    private void ResizeTopRightThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: true, resizeRight: true, resizeBottom: false);

    private void ResizeBottomLeftThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: true, resizeTop: false, resizeRight: false, resizeBottom: true);

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeWindow(e.HorizontalChange, e.VerticalChange, resizeLeft: false, resizeTop: false, resizeRight: true, resizeBottom: true);

    private void ResizeWindow(
        double horizontalChange,
        double verticalChange,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        if (_locked)
        {
            return;
        }

        var currentWidth = double.IsNaN(Width) ? ActualWidth : Width;
        var currentHeight = double.IsNaN(Height) ? ActualHeight : Height;

        if (resizeLeft)
        {
            var resizedWidth = Math.Max(MinWidth, currentWidth - horizontalChange);
            Left += currentWidth - resizedWidth;
            Width = resizedWidth;
        }
        else if (resizeRight)
        {
            Width = Math.Max(MinWidth, currentWidth + horizontalChange);
        }

        if (resizeTop)
        {
            var resizedHeight = Math.Max(MinHeight, currentHeight - verticalChange);
            Top += currentHeight - resizedHeight;
            Height = resizedHeight;
        }
        else if (resizeBottom)
        {
            Height = Math.Max(MinHeight, currentHeight + verticalChange);
        }
    }

    private void EnsureDesktopAttachment()
    {
        if (!_desktopPinned)
        {
            ShowInTaskbar = true;
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Topmost = false;
        // Keep the WPF window out of the global topmost band. The native host
        // owns its desktop-layer placement so ordinary applications can cover it.
        if (_desktopHost.IsAttached(handle))
        {
            return;
        }

        ShowInTaskbar = false;
        EnsureWindowVisible();
        _ = _desktopHost.TryAttach(handle);
    }

    private void RefreshDesktopAttachmentAfterLoad()
    {
        if (!_desktopPinned)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // WPF finalizes native styles after Loaded. Reapply the component
            // host once after that finalization, not on a periodic timer.
            _ = _desktopHost.TryDetach(handle);
        }

        EnsureDesktopAttachment();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _userRequestedMinimize = false;
            return;
        }

        ScheduleDesktopWindowRestore();
    }

    private void ScheduleDesktopWindowRestore()
    {
        if (!_desktopPinned || _userRequestedMinimize || _desktopRestorePending)
        {
            return;
        }

        _desktopRestorePending = true;
        _desktopRestoreTimer.Stop();
        _desktopRestoreTimer.Start();
    }

    private void RestoreDesktopPinnedWindow()
    {
        _desktopRestoreTimer.Stop();
        _desktopRestorePending = false;

        if (!_desktopPinned || _userRequestedMinimize)
        {
            return;
        }

        EnsureWindowVisible();
        var handle = new WindowInteropHelper(this).Handle;
        if (!_desktopHost.IsAttached(handle))
        {
            ShowInTaskbar = true;
        }
    }

    private void EnsureWindowVisible()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    private void ScheduleLayoutSave()
    {
        if (_applyingLayout || !IsLoaded)
        {
            return;
        }

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private async Task SaveLayoutAsync()
    {
        if (_applyingLayout || !IsLoaded)
        {
            return;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var panel = new DesktopPanel(
                PanelId,
                null,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                Left,
                Top,
                Width,
                _isPanelCollapsed ? _expandedHeight : Height,
                Opacity,
                _isPanelCollapsed,
                _locked,
                _panelCreatedAt,
                DateTimeOffset.UtcNow);

            await _backend.Layout.SavePanelAsync(panel);
        }
        catch
        {
            // Layout persistence must never make the desktop panel unusable.
        }
    }

    private void ResetDropVisual(Border? border)
    {
        if (border is null)
        {
            return;
        }

        border.BorderBrush = (Brush)FindResource("CategoryBorderBrush");
        border.BorderThickness = (Thickness)FindResource("CategoryBorderThickness");
        border.Background = (Brush)FindResource("CategorySurfaceBrush");
    }

    private void ResetFileCardDropVisual(Border? border)
    {
        if (border is null)
        {
            return;
        }

        border.BorderBrush = (Brush)FindResource("FileItemBorderBrush");
        border.BorderThickness = (Thickness)FindResource("FileItemBorderThickness");
        border.Background = (Brush)FindResource("FileItemSurfaceBrush");
    }

    private void UpdateNewCategoryDropVisual(DragEventArgs e)
    {
        if (TryGetDroppedFolder(e, out _))
        {
            e.Effects = DragDropEffects.Link;
            NewCategoryButton.BorderBrush = (Brush)FindResource("SkyBrush");
            NewCategoryButton.BorderThickness = new Thickness(1);
            NewCategoryButton.Background = new SolidColorBrush(Color.FromRgb(238, 248, 255));
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ResetNewCategoryDropVisual();
        }

        e.Handled = true;
    }

    private void ResetNewCategoryDropVisual()
    {
        NewCategoryButton.BorderBrush = (Brush)FindResource("NewCategoryBorderBrush");
        NewCategoryButton.BorderThickness = (Thickness)FindResource("NewCategoryBorderThickness");
        NewCategoryButton.Background = (Brush)FindResource("NewCategorySurfaceBrush");
    }

    private static bool TryGetDroppedFolder(DragEventArgs e, out string folderPath)
    {
        folderPath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length != 1)
        {
            return false;
        }

        var candidate = paths[0].Trim().Trim('"');
        if (!Directory.Exists(candidate))
        {
            return false;
        }

        folderPath = candidate;
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T value)
            {
                return value;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject? source, Func<T, bool> predicate)
        where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(source);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
