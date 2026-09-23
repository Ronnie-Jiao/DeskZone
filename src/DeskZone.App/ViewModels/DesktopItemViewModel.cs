using DeskZone.Core.Models;
using System.Windows.Media;

namespace DeskZone.App.ViewModels;

public sealed class DesktopItemViewModel : BindableBase
{
    private DesktopItem _model;

    public DesktopItemViewModel(DesktopItem model, bool isRecentItem = false, bool canReorder = true)
    {
        _model = model;
        IsRecentItem = isRecentItem;
        CanReorder = canReorder && !isRecentItem;
        IconImage = WindowsShellIconProvider.GetIcon(model.ActivePath);
    }

    public DesktopItem Model => _model;
    public Guid Id => Model.Id;
    public string Name => Model.DisplayName;
    public string PersistedName => Model.DisplayName;
    public string Path => Model.ActivePath;
    public bool IsMissing => Model.IsMissing;
    public bool IsRecentItem { get; }
    public bool CanReorder { get; }
    public ImageSource? IconImage { get; private set; }

    public void Apply(DesktopItem model)
    {
        _model = model;
        IconImage = WindowsShellIconProvider.GetIcon(model.ActivePath);
        RaisePropertyChanged(nameof(Model));
        RaisePropertyChanged(nameof(Id));
        RaisePropertyChanged(nameof(Name));
        RaisePropertyChanged(nameof(PersistedName));
        RaisePropertyChanged(nameof(Path));
        RaisePropertyChanged(nameof(IsMissing));
        RaisePropertyChanged(nameof(IconImage));
        RaisePropertyChanged(nameof(IconGlyph));
    }

    public string IconGlyph => IconImage is not null
        ? string.Empty
        : Model.ItemType switch
    {
        DesktopItemType.Folder => "📁",
        DesktopItemType.Shortcut => "↗",
        _ => "📄"
    };
}
