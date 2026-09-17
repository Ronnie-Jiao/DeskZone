namespace DeskZone.Core.Models;

public static class SystemCategoryKeys
{
    public const string Unclassified = "unclassified";
    public const string RecentlyOpened = "recently-opened";
}

public sealed record RecentOpenedItem(
    Guid ItemId,
    string Path,
    string DisplayName,
    DesktopItemType ItemType,
    DateTimeOffset OpenedAt);

public sealed record Category(
    Guid Id,
    string Name,
    int OrderIndex,
    string LayoutType,
    string? Color,
    bool IsCollapsed,
    string SortMode,
    string? SystemKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsSystem => !string.IsNullOrWhiteSpace(SystemKey);
    public bool IsRecentlyOpened => string.Equals(SystemKey, SystemCategoryKeys.RecentlyOpened, StringComparison.Ordinal);
}
