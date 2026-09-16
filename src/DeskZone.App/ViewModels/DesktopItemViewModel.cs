using DeskZone.Core.Models;

namespace DeskZone.App.ViewModels;

public sealed class DesktopItemViewModel
{
    public DesktopItemViewModel(DesktopItem model)
    {
        Model = model;
    }

    public DesktopItem Model { get; }
    public Guid Id => Model.Id;
    public string Name => Model.DisplayName;
    public string Path => Model.ActivePath;
    public bool IsMissing => Model.IsMissing;

    public string IconGlyph => Model.ItemType switch
    {
        DesktopItemType.Folder => "📁",
        DesktopItemType.Shortcut => "↗",
        _ => "📄"
    };
}
