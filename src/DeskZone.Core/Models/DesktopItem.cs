namespace DeskZone.Core.Models;

public enum DesktopItemType
{
    File,
    Folder,
    Shortcut
}

public enum DesktopItemMode
{
    Reference,
    Managed
}

public sealed record DesktopItem(
    Guid Id,
    Guid CategoryId,
    DesktopItemMode ItemMode,
    string OriginalPath,
    string? ManagedPath,
    string DisplayName,
    DesktopItemType ItemType,
    int CustomOrder,
    bool IsMissing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string ActivePath => ItemMode == DesktopItemMode.Managed && !string.IsNullOrWhiteSpace(ManagedPath)
        ? ManagedPath
        : OriginalPath;
}
