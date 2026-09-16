using System.Runtime.InteropServices;
using System.Text;
using DeskZone.Core.Services;

namespace DeskZone.Shell;

public sealed class WindowsDesktopHostService : IDesktopHostService
{
    private const uint SpawnWorkerMessage = 0x052C;
    private const uint SmtoAbortIfHung = 0x0002;

    private IntPtr _desktopHost;

    public bool TryAttach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (IsAttached(windowHandle))
        {
            return true;
        }

        var host = FindDesktopHost();
        if (host == IntPtr.Zero)
        {
            return false;
        }

        _ = SetParent(windowHandle, host);
        if (GetParent(windowHandle) != host)
        {
            return false;
        }

        _desktopHost = host;
        return true;
    }

    public bool IsAttached(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || _desktopHost == IntPtr.Zero)
        {
            return false;
        }

        if (!IsWindow(windowHandle) || !IsWindow(_desktopHost))
        {
            _desktopHost = IntPtr.Zero;
            return false;
        }

        return GetParent(windowHandle) == _desktopHost;
    }

    private static IntPtr FindDesktopHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            _ = SendMessageTimeout(
                progman,
                SpawnWorkerMessage,
                UIntPtr.Zero,
                IntPtr.Zero,
                SmtoAbortIfHung,
                1000,
                out _);
        }

        IntPtr shellViewHost = IntPtr.Zero;
        _ = EnumWindows((topLevel, _) =>
        {
            if (FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return true;
            }

            shellViewHost = topLevel;
            return false;
        }, IntPtr.Zero);

        if (shellViewHost != IntPtr.Zero)
        {
            var siblingWorker = FindWindowEx(IntPtr.Zero, shellViewHost, "WorkerW", null);
            if (siblingWorker != IntPtr.Zero)
            {
                return siblingWorker;
            }
        }

        IntPtr workerFallback = IntPtr.Zero;
        _ = EnumWindows((topLevel, _) =>
        {
            if (!string.Equals(GetWindowClassName(topLevel), "WorkerW", StringComparison.Ordinal))
            {
                return true;
            }

            if (FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                return true;
            }

            workerFallback = topLevel;
            return false;
        }, IntPtr.Zero);

        return workerFallback != IntPtr.Zero ? workerFallback : progman;
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var buffer = new StringBuilder(128);
        return GetClassName(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr hWndParent,
        IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}
