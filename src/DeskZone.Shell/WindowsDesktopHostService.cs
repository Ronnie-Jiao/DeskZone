using System.Runtime.InteropServices;
using DeskZone.Core.Services;

namespace DeskZone.Shell;

/// <summary>
/// Keeps DeskZone as a lightweight desktop component. It remains a top-level
/// surface so Explorer cannot paint over it, is owned by the desktop shell,
/// and is placed directly above the desktop but below ordinary app windows.
/// </summary>
public sealed class WindowsDesktopHostService : IDesktopHostService
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x00000080L;
    private const int ShowWindowNoActivateCommand = 4;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr HwndNoTopMost = new(-2);

    private IntPtr _attachedWindow;
    private IntPtr _desktopOwner;
    private IntPtr _previousOwner;
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

        var previousOwner = GetParent(windowHandle);
        var previousExtendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        var componentExtendedStyle = previousExtendedStyle | WsExToolWindow;

        _ = SetWindowLongPtr(windowHandle, GwlExStyle, new IntPtr(componentExtendedStyle));
        _ = SetWindowLongPtr(windowHandle, GwlHwndParent, desktopOwner);

        // Keep the component in the desktop layer, below ordinary applications
        // while remaining available when Win+D returns to the desktop.
        if (!SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                left,
                top,
                0,
                0,
                SetWindowPosNoSize |
                SetWindowPosNoActivate |
                SetWindowPosFrameChanged))
        {
            RestoreTopLevelWindow(windowHandle, previousOwner, previousExtendedStyle, windowRect);
            return false;
        }

        // FrameChanged can reset z-order on this Explorer build, so apply the
        // component layer in a separate call after style recalculation.
        if (!SetWindowPos(
                windowHandle,
                HwndNoTopMost,
                left,
                top,
                0,
                0,
                SetWindowPosNoSize |
                SetWindowPosNoActivate |
                SetWindowPosShowWindow))
        {
            RestoreTopLevelWindow(windowHandle, previousOwner, previousExtendedStyle, windowRect);
            return false;
        }

        _ = ShowWindow(windowHandle, ShowWindowNoActivateCommand);
        _attachedWindow = windowHandle;
        _desktopOwner = desktopOwner;
        _previousOwner = previousOwner;
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

        RestoreTopLevelWindow(windowHandle, _previousOwner, _previousExtendedStyle, screenRect);
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

        if (GetWindow(windowHandle, GwOwner) == _desktopOwner)
        {
            if (!GetWindowRect(windowHandle, out var rect) ||
                !SetWindowPos(
                    windowHandle,
                    HwndNoTopMost,
                    rect.Left,
                    rect.Top,
                    0,
                    0,
                    SetWindowPosNoSize |
                    SetWindowPosNoActivate |
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
        var desktopShellWindow = IntPtr.Zero;
        _ = EnumWindows((topLevelWindow, _) =>
        {
            var desktopView = FindWindowEx(topLevelWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView == IntPtr.Zero || !IsUsableDesktopHost(topLevelWindow))
            {
                return true;
            }

            desktopShellWindow = topLevelWindow;
            return false;
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

    private static void RestoreTopLevelWindow(IntPtr windowHandle, IntPtr previousOwner, long previousExtendedStyle, Rect screenRect)
    {
        _ = SetWindowLongPtr(windowHandle, GwlHwndParent, previousOwner);
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
            SetWindowPosFrameChanged |
            SetWindowPosShowWindow);
    }

    private void ClearAttachment()
    {
        _attachedWindow = IntPtr.Zero;
        _desktopOwner = IntPtr.Zero;
        _previousOwner = IntPtr.Zero;
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

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentWindow, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

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
