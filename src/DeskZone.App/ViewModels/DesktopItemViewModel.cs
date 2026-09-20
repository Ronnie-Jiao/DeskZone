using DeskZone.Core.Models;
using System.Windows.Media;

namespace DeskZone.App.ViewModels;

public sealed class DesktopItemViewModel
{
    public DesktopItemViewModel(DesktopItem model, bool isRecentItem = false, bool canReorder = true)
    {
        Model = model;
        IsRecentItem = isRecentItem;
        CanReorder = canReorder && !isRecentItem;
        IconImage = WindowsShellIconProvider.GetIcon(model.ActivePath);
    }

    public DesktopItem Model { get; }
    public Guid Id => Model.Id;
    public string Name => Model.DisplayName;
    public string Path => Model.ActivePath;
    public bool IsMissing => Model.IsMissing;
    public bool IsRecentItem { get; }
    public bool CanReorder { get; }
    public ImageSource? IconImage { get; }

    public string IconGlyph => IconImage is not null
        ? string.Empty
        : Model.ItemType switch
    {
        DesktopItemType.Folder => "📁",
        DesktopItemType.Shortcut => "↗",
        _ => "📄"
    };
}
