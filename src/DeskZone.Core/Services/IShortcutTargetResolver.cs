namespace DeskZone.Core.Services;

/// <summary>
/// Resolves a shortcut only when it points to a concrete, fully-qualified
/// file-system path. Shell namespace and otherwise ambiguous targets are not
/// reported as paths.
/// </summary>
public interface IShortcutTargetResolver
{
    bool TryResolveTarget(string shortcutPath, out string targetPath);
}
