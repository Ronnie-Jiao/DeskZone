using DeskZone.Core.Models;

namespace DeskZone.Core.Services;

public interface ILayoutService
{
    Task<DesktopPanel> GetPanelAsync(Guid panelId, CancellationToken cancellationToken = default);
    Task SavePanelAsync(DesktopPanel panel, CancellationToken cancellationToken = default);
}
