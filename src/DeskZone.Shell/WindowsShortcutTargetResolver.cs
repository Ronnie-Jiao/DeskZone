using System.Reflection;
using System.Runtime.InteropServices;
using DeskZone.Core.Services;

namespace DeskZone.Shell;

public sealed class WindowsShortcutTargetResolver : IShortcutTargetResolver
{
    public bool TryResolveTarget(string shortcutPath, out string targetPath)
    {
        targetPath = string.Empty;
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Path.GetExtension(shortcutPath), ".lnk", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(shortcutPath))
        {
            return false;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });

            var rawTarget = shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
            if (string.IsNullOrWhiteSpace(rawTarget))
            {
                return false;
            }

            var expandedTarget = Environment.ExpandEnvironmentVariables(rawTarget.Trim().Trim('"'));
            if (expandedTarget.Contains('%') || !Path.IsPathFullyQualified(expandedTarget))
            {
                return false;
            }

            targetPath = Path.GetFullPath(expandedTarget);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or MissingMethodException or
                                   TargetInvocationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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
}
