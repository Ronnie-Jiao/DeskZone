using System.Windows;
using DeskZone.Shell;
using DeskZone.Storage;

namespace DeskZone.App;

public partial class App : Application
{
    public LocalBackend Backend { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Backend = LocalBackend.CreateDefault();
            await Backend.InitializeAsync();

            var window = new MainWindow(
                Backend,
                new WindowsShellService(),
                new WindowsDesktopHostService());
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"DeskZone 本地数据初始化失败。\n\n{ex.Message}\n\n应用尚未执行任何文件整理操作。",
                "DeskZone 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
