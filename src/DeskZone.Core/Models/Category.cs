namespace DeskZone.Core.Models;

public static class SystemCategoryKeys
{
    public const string Unclassified = "unclassified";
}

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
}
