using DeskZone.Core.Contracts;
using DeskZone.Core.Exceptions;
using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public sealed class LayoutService : ILayoutService
{
    private readonly IWorkspaceStore _store;

    public LayoutService(IWorkspaceStore store)
    {
        _store = store;
    }

    public async Task<DesktopPanel> GetPanelAsync(Guid panelId, CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetPanelAsync(panelId, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        var now = DateTimeOffset.UtcNow;
        return new DesktopPanel(
            panelId,
            null,
            96,
            96,
            80,
            80,
            380,
            650,
            1.0,
            false,
            false,
            now,
            now);
    }

    public Task SavePanelAsync(DesktopPanel panel, CancellationToken cancellationToken = default)
    {
        if (panel.WidthDip < 280 || panel.HeightDip < 48)
        {
            throw new DeskZoneValidationException("面板尺寸小于允许的最小值。");
        }
        if (panel.Opacity is < 0.30 or > 1.0)
        {
            throw new DeskZoneValidationException("面板透明度必须在 0.30 到 1.00 之间。");
        }
        if (panel.DpiX <= 0 || panel.DpiY <= 0)
        {
            throw new DeskZoneValidationException("DPI 数据无效。");
        }

        return _store.UpsertPanelAsync(panel with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    }
}
