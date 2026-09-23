using System.Runtime.InteropServices;
using DeskZone.Core.Services;

namespace DeskZone.Shell;

/// <summary>
/// Keeps DeskZone as a lightweight desktop component by making its HWND a real
/// child of the desktop WorkerW surface. It therefore stays visible on the
/// desktop without occupying the global topmost window band.
/// </summary>
public sealed class WindowsDesktopHostService : IDesktopHostService
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const uint SwpNoZOrder = 0x0004;
    private const int ShowWindowNoActivateCommand = 4;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;
    private const uint ProgmanWorkerMessage = 0x052C;
    private const uint SendMessageTimeoutAbortIfHung = 0x0002;

    private IntPtr _attachedWindow;
    private IntPtr _desktopOwner;
    private IntPtr _previousParent;
    private long _previousWindowStyle;
    private long _previousExtendedStyle;

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

        var desktopOwner = FindDesktopShellWindow();
        if (desktopOwner == IntPtr.Zero || !GetWindowRect(windowHandle, out var windowRect))
        {
            return false;
        }

        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var panelWidth = windowRect.Right - windowRect.Left;
        var panelHeight = windowRect.Bottom - windowRect.Top;
        var left = Math.Clamp(windowRect.Left, 0, Math.Max(0, screenWidth - panelWidth));
        var top = Math.Clamp(windowRect.Top, 0, Math.Max(0, screenHeight - panelHeight));
        var hostPosition = new Point { X = left, Y = top };
        if (!ScreenToClient(desktopOwner, ref hostPosition))
        {
            return false;
        }

        var previousParent = GetParent(windowHandle);
        var previousWindowStyle = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        var previousExtendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        var componentWindowStyle = (previousWindowStyle | WsChild) & ~WsPopup;
        var componentExtendedStyle = previousExtendedStyle | WsExToolWindow;

        _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(componentWindowStyle));
        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(componentExtendedStyle));
        _ = SetParent(windowHandle, desktopOwner);
        if (GetParent(windowHandle) != desktopOwner)
        {
            RestoreWindow(windowHandle, previousParent, previousWindowStyle, previousExtendedStyle, windowRect);
            return false;
        }

        // Keep the component in the desktop layer, below ordinary applications
        // while remaining available when Win+D returns to the desktop.
        if (!SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                hostPosition.X,
                hostPosition.Y,
                0,
                0,
                SetWindowPosNoSize |
                SetWindowPosNoActivate |
                SetWindowPosFrameChanged))
        {
            RestoreWindow(windowHandle, previousParent, previousWindowStyle, previousExtendedStyle, windowRect);
            return false;
        }

        // A real child of WorkerW is not included in Win+D's top-level window
        // hiding pass. Keep it below normal application windows without using
        // the global topmost band.
        if (!SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                hostPosition.X,
                hostPosition.Y,
                0,
                0,
                SetWindowPosNoSize |
                SetWindowPosNoActivate |
                SwpNoZOrder |
                SetWindowPosShowWindow))
        {
            RestoreWindow(windowHandle, previousParent, previousWindowStyle, previousExtendedStyle, windowRect);
            return false;
        }

        _ = ShowWindow(windowHandle, ShowWindowNoActivateCommand);
        _attachedWindow = windowHandle;
        _desktopOwner = desktopOwner;
        _previousParent = previousParent;
        _previousWindowStyle = previousWindowStyle;
        _previousExtendedStyle = previousExtendedStyle;
        return true;
    }

    public bool TryDetach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (!IsAttached(windowHandle))
        {
            ClearAttachment();
            return true;
        }

        if (!GetWindowRect(windowHandle, out var screenRect))
        {
            return false;
        }

        RestoreWindow(windowHandle, _previousParent, _previousWindowStyle, _previousExtendedStyle, screenRect);
        ClearAttachment();
        return true;
    }

    public bool IsAttached(IntPtr windowHandle)
    {
        if (_attachedWindow == IntPtr.Zero || windowHandle != _attachedWindow ||
            _desktopOwner == IntPtr.Zero || !IsWindow(windowHandle) || !IsWindow(_desktopOwner) ||
            !IsWindowVisible(windowHandle))
        {
            if (_attachedWindow == windowHandle || !IsWindow(_attachedWindow))
            {
                ClearAttachment();
            }

            return false;
        }

        if (GetParent(windowHandle) == _desktopOwner)
        {
            if (!GetWindowRect(windowHandle, out var rect))
            {
                ClearAttachment();
                return false;
            }

            var hostPosition = new Point { X = rect.Left, Y = rect.Top };
            if (!ScreenToClient(_desktopOwner, ref hostPosition) ||
                !SetWindowPos(
                    windowHandle,
                    IntPtr.Zero,
                    hostPosition.X,
                    hostPosition.Y,
                    0,
                    0,
                    SetWindowPosNoSize |
                    SetWindowPosNoActivate |
                    SwpNoZOrder |
                    SetWindowPosShowWindow))
            {
                ClearAttachment();
                return false;
            }

            _ = ShowWindow(windowHandle, ShowWindowNoActivateCommand);
            return true;
        }

        ClearAttachment();
        return false;
    }

    private static IntPtr FindDesktopShellWindow()
    {
        var progman = FindWindow("Progman", "Program Manager");
        if (progman != IntPtr.Zero)
        {
            _ = SendMessageTimeout(
                progman,
                ProgmanWorkerMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                SendMessageTimeoutAbortIfHung,
                1000,
                out _);
        }

        var desktopShellWindow = IntPtr.Zero;
        _ = EnumWindows((topLevelWindow, _) =>
        {
            var desktopView = FindWindowEx(topLevelWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView == IntPtr.Zero)
            {
                return true;
            }

            var workerWindow = FindWindowEx(IntPtr.Zero, topLevelWindow, "WorkerW", null);
            if (workerWindow != IntPtr.Zero && IsUsableDesktopHost(workerWindow))
            {
                desktopShellWindow = workerWindow;
                return false;
            }

            if (desktopShellWindow == IntPtr.Zero && IsUsableDesktopHost(topLevelWindow))
            {
                desktopShellWindow = topLevelWindow;
            }

            return true;
        }, IntPtr.Zero);

        return desktopShellWindow;
    }

    private static bool IsUsableDesktopHost(IntPtr windowHandle)
    {
        if (!IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        return screenWidth > 0 && screenHeight > 0 &&
               width >= screenWidth / 2 && height >= screenHeight / 2;
    }

    private static void RestoreWindow(
        IntPtr windowHandle,
        IntPtr previousParent,
        long previousWindowStyle,
        long previousExtendedStyle,
        Rect screenRect)
    {
        _ = SetParent(windowHandle, previousParent);
        _ = SetWindowLongPtr(windowHandle, GwlStyle, new IntPtr(previousWindowStyle));
        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(previousExtendedStyle));
        _ = SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            screenRect.Left,
            screenRect.Top,
            0,
            0,
            SetWindowPosNoSize |
            SetWindowPosNoActivate |
            SwpNoZOrder |
            SetWindowPosFrameChanged |
            SetWindowPosShowWindow);
    }

    private void ClearAttachment()
    {
        _attachedWindow = IntPtr.Zero;
        _desktopOwner = IntPtr.Zero;
        _previousParent = IntPtr.Zero;
        _previousWindowStyle = 0;
        _previousExtendedStyle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentWindow, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr childWindow, IntPtr newParentWindow);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
}
