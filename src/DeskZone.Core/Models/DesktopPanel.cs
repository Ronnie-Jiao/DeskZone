namespace DeskZone.Core.Models;

public sealed record DesktopPanel(
    Guid Id,
    string? MonitorId,
    double DpiX,
    double DpiY,
    double LeftDip,
    double TopDip,
    double WidthDip,
    double HeightDip,
    double Opacity,
    bool IsCollapsed,
    bool IsLocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
