using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskZone.Shell;
using DeskZone.Storage;
using MessageBox = System.Windows.MessageBox;

namespace DeskZone.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\DeskZone.SingleInstance";
    private const int ShowWindowRestoreCommand = 9;
    private const int ShowWindowShowCommand = 5;
    private static readonly uint ShowExistingMessage = RegisterWindowMessage("DeskZone.ShowExisting");
    private static Mutex? _singleInstanceMutex;

    public LocalBackend Backend { get; private set; } = null!;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

    public static uint ShowExistingMessageId => ShowExistingMessage;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        Backend?.Dispose();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var signaled = false;

        EnumWindows((windowHandle, _) =>
        {
            if (TrySignalWindow(windowHandle, currentProcessId))
            {
                signaled = true;
                return false;
            }

            EnumChildWindows(windowHandle, (childHandle, _) =>
            {
                if (TrySignalWindow(childHandle, currentProcessId))
                {
                    signaled = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return !signaled;
        }, IntPtr.Zero);
    }

    private static bool TrySignalWindow(IntPtr windowHandle, uint currentProcessId)
    {
        if (windowHandle == IntPtr.Zero ||
            GetWindowThreadProcessId(windowHandle, out var processId) == 0 ||
            processId == currentProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, "DeskZone", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        // A pinned taskbar shortcut starts a second process. Restore the existing
        // window immediately, then let its WPF hook repair the current mode/parent.
        _ = ShowWindow(windowHandle, ShowWindowRestoreCommand);
        _ = ShowWindow(windowHandle, ShowWindowShowCommand);
        var signaled = PostMessage(windowHandle, ShowExistingMessage, IntPtr.Zero, IntPtr.Zero);
        if (signaled && GetParent(windowHandle) == IntPtr.Zero)
        {
            _ = SetForegroundWindow(windowHandle);
        }

        return signaled;
    }
}
