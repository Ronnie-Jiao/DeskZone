using System.Runtime.InteropServices;

namespace DeskZone.Shell;

/// <summary>
/// Opens the real Windows Explorer context menu for a file-system item.
/// The menu is provided and executed by Shell32, so registered shell
/// extensions and file-type-specific verbs are preserved.
/// </summary>
internal static class WindowsShellContextMenuService
{
    private const uint ContextMenuFirstCommand = 1;
    private const uint ContextMenuLastCommand = 0x7FFF;
    private const uint ContextMenuFlags = 0x0004; // CMF_EXPLORE
    private const uint TrackPopupMenuRightButton = 0x0002;
    private const uint TrackPopupMenuNoNotify = 0x0080;
    private const uint TrackPopupMenuReturnCommand = 0x0100;
    private const uint InvokeCommandUnicode = 0x00004000; // CMIC_MASK_UNICODE
    private const uint InvokeCommandPoint = 0x20000000; // CMIC_MASK_PTINVOKE
    private const int ShowNormal = 1;
    private const uint WindowStylePopup = 0x80000000;
    private const uint WindowExStyleToolWindow = 0x00000080;
    private const int ShowWindowCommand = 5;
    private const uint WindowMessageNull = 0;
    private static readonly Guid ShellFolderInterfaceGuid = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuInterfaceId = new("000214e4-0000-0000-c000-000000000046");

    public static bool TryShow(string path, IntPtr ownerWindowHandle, int screenX, int screenY)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!File.Exists(path) && !Directory.Exists(path)))
        {
            return false;
        }

        IntPtr itemIdList = IntPtr.Zero;
        IntPtr menuHandle = IntPtr.Zero;
        IntPtr popupOwnerHandle = IntPtr.Zero;
        IContextMenu? contextMenu = null;
        IShellFolder? parentFolder = null;

        try
        {
            popupOwnerHandle = CreatePopupOwner(screenX, screenY);
            var menuOwnerHandle = popupOwnerHandle == IntPtr.Zero
                ? ownerWindowHandle
                : popupOwnerHandle;

            var parseResult = SHParseDisplayName(path, IntPtr.Zero, out itemIdList, 0, out _);
            if (parseResult < 0 || itemIdList == IntPtr.Zero)
            {
                return false;
            }

            // SHBindToParent returns the parent folder, not IContextMenu.
            // The context-menu object must then be requested from that folder
            // for the final child PIDL.
            var shellFolderInterfaceId = ShellFolderInterfaceGuid;
            var bindResult = SHBindToParent(
                itemIdList,
                ref shellFolderInterfaceId,
                out parentFolder,
                out var childIdList);
            if (bindResult < 0 || parentFolder is null || childIdList == IntPtr.Zero)
            {
                return false;
            }

            var contextMenuInterfaceId = ContextMenuInterfaceId;
            var getContextMenuResult = parentFolder.GetUIObjectOf(
                menuOwnerHandle,
                1,
                ref childIdList,
                ref contextMenuInterfaceId,
                IntPtr.Zero,
                out contextMenu);
            if (getContextMenuResult < 0 || contextMenu is null)
            {
                return false;
            }

            menuHandle = CreatePopupMenu();
            if (menuHandle == IntPtr.Zero)
            {
                return false;
            }

            var queryResult = contextMenu.QueryContextMenu(
                menuHandle,
                0,
                ContextMenuFirstCommand,
                ContextMenuLastCommand,
                ContextMenuFlags);
            if (queryResult < 0)
            {
                return false;
            }

            _ = ShowWindow(menuOwnerHandle, ShowWindowCommand);
            _ = SetForegroundWindow(menuOwnerHandle);
            var selectedCommand = TrackPopupMenuEx(
                menuHandle,
                TrackPopupMenuReturnCommand | TrackPopupMenuRightButton | TrackPopupMenuNoNotify,
                screenX,
                screenY,
                menuOwnerHandle,
                IntPtr.Zero);

            if (selectedCommand < ContextMenuFirstCommand || selectedCommand > ContextMenuLastCommand)
            {
                return false;
            }

            var commandOffset = selectedCommand - ContextMenuFirstCommand;
            var invokeInfo = new CommandInvocationInfo
            {
                Size = (uint)Marshal.SizeOf<CommandInvocationInfo>(),
                Mask = InvokeCommandUnicode | InvokeCommandPoint,
                WindowHandle = menuOwnerHandle,
                Verb = new IntPtr(commandOffset),
                VerbUnicode = new IntPtr(commandOffset),
                ShowCommand = ShowNormal,
                InvokePoint = new ScreenPoint { X = screenX, Y = screenY }
            };

            var invokeResult = contextMenu.InvokeCommand(ref invokeInfo);
            return invokeResult >= 0;
        }
        finally
        {
            var messageOwnerHandle = popupOwnerHandle == IntPtr.Zero
                ? ownerWindowHandle
                : popupOwnerHandle;
            _ = PostMessage(messageOwnerHandle, WindowMessageNull, IntPtr.Zero, IntPtr.Zero);
            if (menuHandle != IntPtr.Zero)
            {
                _ = DestroyMenu(menuHandle);
            }

            if (itemIdList != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(itemIdList);
            }

            if (contextMenu is not null && Marshal.IsComObject(contextMenu))
            {
                Marshal.FinalReleaseComObject(contextMenu);
            }

            if (parentFolder is not null && Marshal.IsComObject(parentFolder))
            {
                Marshal.FinalReleaseComObject(parentFolder);
            }

            if (popupOwnerHandle != IntPtr.Zero)
            {
                _ = DestroyWindow(popupOwnerHandle);
            }
        }
    }

    private static IntPtr CreatePopupOwner(int screenX, int screenY) =>
        CreateWindowEx(
            WindowExStyleToolWindow,
            "STATIC",
            "DeskZone Shell Context Menu Owner",
            WindowStylePopup,
            screenX,
            screenY,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr itemIdList,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder parentFolder,
        out IntPtr childIdList);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menuHandle);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menuHandle,
        uint flags,
        int x,
        int y,
        IntPtr ownerWindowHandle,
        IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindowHandle,
        IntPtr menuHandle,
        IntPtr instanceHandle,
        IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwndOwner,
            IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            out uint characterCount,
            out IntPtr itemIdList,
            ref uint attributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwndOwner, uint flags, out IntPtr enumIdList);

        [PreserveSig]
        int BindToObject(
            IntPtr itemIdList,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int BindToStorage(
            IntPtr itemIdList,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int CompareIds(IntPtr sortParameters, IntPtr firstItemIdList, IntPtr secondItemIdList);

        [PreserveSig]
        int CreateViewObject(
            IntPtr hwndOwner,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int GetAttributesOf(uint itemCount, ref IntPtr itemIdLists, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint itemCount,
            ref IntPtr itemIdLists,
            ref Guid interfaceId,
            IntPtr reserved,
            [MarshalAs(UnmanagedType.Interface)] out IContextMenu contextMenu);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr itemIdList, uint flags, IntPtr name);

        [PreserveSig]
        int SetNameOf(
            IntPtr hwndOwner,
            IntPtr itemIdList,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags,
            out IntPtr newItemIdList);
    }

    [ComImport]
    [Guid("000214e4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(
            IntPtr menuHandle,
            uint indexMenu,
            uint idCommandFirst,
            uint idCommandLast,
            uint flags);

        [PreserveSig]
        int InvokeCommand(ref CommandInvocationInfo commandInfo);

        [PreserveSig]
        int GetCommandString(
            IntPtr commandId,
            uint flags,
            IntPtr reserved,
            IntPtr commandString,
            uint commandStringLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CommandInvocationInfo
    {
        public uint Size;
        public uint Mask;
        public IntPtr WindowHandle;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int ShowCommand;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbUnicode;
        public IntPtr ParametersUnicode;
        public IntPtr DirectoryUnicode;
        public IntPtr TitleUnicode;
        public ScreenPoint InvokePoint;
    }
}
