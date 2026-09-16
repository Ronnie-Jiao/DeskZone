using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskZone.App.ViewModels;
using DeskZone.Core.Models;
using DeskZone.Core.Services;
using DeskZone.Storage;

namespace DeskZone.App;

public partial class MainWindow : Window
{
    private static readonly Guid PanelId = Guid.Parse("8577142a-f767-41df-b3a2-5b07410f9181");

    private readonly LocalBackend _backend;
    private readonly WorkspaceViewModel _viewModel;
    private readonly DispatcherTimer _layoutSaveTimer;

    private bool _applyingLayout;
    private bool _allowClose;
    private bool _locked;
    private bool _collapsed;
    private double _expandedHeight = 650;
    private DateTimeOffset _panelCreatedAt = DateTimeOffset.UtcNow;

    public MainWindow(LocalBackend backend, IShellService shell)
    {
        InitializeComponent();

        _backend = backend;
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();

            var panel = await _backend.Layout.GetPanelAsync(PanelId);
            _panelCreatedAt = panel.CreatedAt;

            _applyingLayout = true;
            Left = panel.LeftDip;
            Top = panel.TopDip;
            Width = Math.Max(MinWidth, panel.WidthDip);
            Height = Math.Max(430, panel.HeightDip);
            Opacity = Math.Clamp(panel.Opacity, 0.30, 1.0);
            ApplyLockedState(panel.IsLocked, scheduleSave: false);
            ApplyCollapsedState(panel.IsCollapsed, scheduleSave: false);
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

        e.Cancel = true;
        _layoutSaveTimer.Stop();

        try
        {
            await SaveLayoutAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
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

        if (e.ClickCount == 2)
        {
            ApplyCollapsedState(!_collapsed);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released between the event and DragMove on a busy desktop.
        }
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

    private async void ToggleCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryViewModel category)
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

        try
        {
            await _viewModel.RenameCategoryAsync(category, textBox.Text);
        }
        catch (Exception ex)
        {
            textBox.Text = category.PersistedName;
            ShowError("重命名分类失败", ex);
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

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryViewModel category)
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

    private void Category_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Link;
            border.BorderBrush = (Brush)FindResource("SkyBrush");
            border.Background = new SolidColorBrush(Color.FromRgb(238, 248, 255));
            e.Handled = true;
        }
    }

    private void Category_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropVisual(sender as Border);
    }

    private async void Category_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not CategoryViewModel category)
        {
            return;
        }

        ResetDropVisual(border);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        try
        {
            await _viewModel.AddReferencesAsync(category, paths);
        }
        catch (Exception ex)
        {
            ShowError("添加文件引用失败", ex);
        }
    }

    private async void FileCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.Tag is not DesktopItemViewModel item)
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

    private void Lock_Click(object sender, RoutedEventArgs e) =>
        ApplyLockedState(!_locked);

    private void Collapse_Click(object sender, RoutedEventArgs e) =>
        ApplyCollapsedState(!_collapsed);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"DeskZone 当前为纯本地模式。\n\n数据目录：\n{_backend.Paths.RootDirectory}\n\n真实文件默认不会因为拖入分类而被移动。",
            "DeskZone 设置",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void ApplyLockedState(bool locked, bool scheduleSave = true)
    {
        _locked = locked;
        LockButton.Content = locked ? "🔒" : "🔓";
        LockButton.ToolTip = locked ? "解锁位置" : "锁定位置";
        ApplyResizeMode();

        if (scheduleSave)
        {
            ScheduleLayoutSave();
        }
    }

    private void ApplyCollapsedState(bool collapsed, bool scheduleSave = true)
    {
        if (_collapsed == collapsed && WorkspaceBody.Visibility == (collapsed ? Visibility.Collapsed : Visibility.Visible))
        {
            return;
        }

        if (collapsed)
        {
            _expandedHeight = Math.Max(430, Height);
            MinHeight = 48;
            WorkspaceBody.Visibility = Visibility.Collapsed;
            Height = 48;
        }
        else
        {
            WorkspaceBody.Visibility = Visibility.Visible;
            MinHeight = 430;
            Height = Math.Max(430, _expandedHeight);
        }

        _collapsed = collapsed;
        CollapseButton.Content = collapsed ? "▾" : "—";
        CollapseButton.ToolTip = collapsed ? "展开面板" : "折叠面板";
        ApplyResizeMode();

        if (scheduleSave)
        {
            ScheduleLayoutSave();
        }
    }

    private void ApplyResizeMode()
    {
        ResizeMode = _locked || _collapsed
            ? ResizeMode.NoResize
            : ResizeMode.CanResizeWithGrip;
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
            var height = _collapsed ? Math.Max(430, _expandedHeight) : Height;
            var panel = new DesktopPanel(
                PanelId,
                null,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                Left,
                Top,
                Width,
                height,
                Opacity,
                _collapsed,
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

    private static void ResetDropVisual(Border? border)
    {
        if (border is null)
        {
            return;
        }

        border.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 238, 246));
        border.Background = new SolidColorBrush(Color.FromRgb(252, 254, 255));
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

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
