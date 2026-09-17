using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskZone.App;

internal static class WindowsShellIconProvider
{
    private const int ShellIconSize = 96;

    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfScaleUp = 0x00000100;

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    public static ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!File.Exists(path) && !Directory.Exists(path)))
        {
            return null;
        }

        return TryGetShellItemIcon(path) ?? TryGetSystemIcon(path);
    }

    private static ImageSource? TryGetShellItemIcon(string path)
    {
        IShellItemImageFactory? shellItem = null;
        var imageFactoryId = typeof(IShellItemImageFactory).GUID;

        try
        {
            var createResult = SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref imageFactoryId,
                out shellItem);
            if (createResult < 0 || shellItem is null)
            {
                return null;
            }

            var imageResult = shellItem.GetImage(
                new NativeSize(ShellIconSize, ShellIconSize),
                SiigbfBiggerSizeOk | SiigbfIconOnly | SiigbfScaleUp,
                out var bitmapHandle);
            if (imageResult < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DeleteObject(bitmapHandle);
            }
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
    }

    private static ImageSource? TryGetSystemIcon(string path)
    {
        var result = SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon);

        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory shellItem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public readonly int Width;
        public readonly int Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
