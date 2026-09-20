using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskZone.Core.Models;

namespace DeskZone.App;

internal sealed record SmartGroupDefinition(string Key, string Name, int OrderIndex);

internal static class SmartGroupingService
{
    public static IReadOnlyList<SmartGroupDefinition> Definitions { get; } =
        new[]
        {
            new SmartGroupDefinition(SystemCategoryKeys.RecentlyOpened, "最近文件", 0),
            new SmartGroupDefinition("smart-applications", "应用入口", 1),
            new SmartGroupDefinition("smart-documents", "文档资料", 2),
            new SmartGroupDefinition("smart-images", "图片文件", 3),
            new SmartGroupDefinition("smart-archives", "压缩安装", 4),
            new SmartGroupDefinition("smart-directories", "文件目录", 5),
            new SmartGroupDefinition("smart-other", "其他文件", 6)
        };

    public static string Classify(DesktopItem item)
    {
        var path = item.ActivePath;
        var targetPath = item.ItemType == DesktopItemType.Shortcut
            ? TryResolveShortcutTarget(path)
            : null;
        var classificationPath = string.IsNullOrWhiteSpace(targetPath) ? path : targetPath;

        if (item.ItemType == DesktopItemType.Folder || Directory.Exists(classificationPath))
        {
            return "smart-directories";
        }

        var extension = Path.GetExtension(classificationPath).ToLowerInvariant();
        if (IsArchiveOrInstaller(extension, classificationPath))
        {
            return "smart-archives";
        }

        if (IsApplication(extension) ||
            item.ItemType == DesktopItemType.Shortcut && string.IsNullOrWhiteSpace(targetPath))
        {
            return "smart-applications";
        }

        if (DocumentExtensions.Contains(extension))
        {
            return "smart-documents";
        }

        if (ImageExtensions.Contains(extension))
        {
            return "smart-images";
        }

        // Audio and video deliberately fall through to the universal fallback
        // instead of creating a profession-specific media category.
        return "smart-other";
    }

    public static bool IsMissing(DesktopItem item) =>
        !File.Exists(item.ActivePath) && !Directory.Exists(item.ActivePath);

    private static bool IsArchiveOrInstaller(string extension, string path)
    {
        if (ArchiveExtensions.Contains(extension))
        {
            return true;
        }

        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("install", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("patch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplication(string extension) =>
        ApplicationExtensions.Contains(extension);

    private static string? TryResolveShortcutTarget(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { path });

            var target = shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(target.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or MissingMethodException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static readonly HashSet<string> ApplicationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appref-ms", ".bat", ".cmd", ".com", ".exe", ".ps1", ".vbs"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".appx", ".appxbundle", ".bz2", ".cab", ".dmg", ".gz", ".img", ".iso",
        ".msi", ".msix", ".pkg", ".rar", ".tar", ".wim", ".xz", ".zip"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docm", ".docx", ".markdown", ".md", ".odp", ".ods", ".odt",
        ".pdf", ".ppt", ".pptm", ".pptx", ".rtf", ".tex", ".tsv", ".txt", ".xls", ".xlsm",
        ".xlsx", ".xltx"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ai", ".avif", ".bmp", ".fig", ".gif", ".heic", ".ico", ".jpeg", ".jpg", ".png",
        ".psd", ".svg", ".tif", ".tiff", ".webp"
    };
}
