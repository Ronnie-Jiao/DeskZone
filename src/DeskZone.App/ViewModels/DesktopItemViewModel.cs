using DeskZone.Core.Models;
using System.Windows.Media;

namespace DeskZone.App.ViewModels;

public sealed class DesktopItemViewModel : BindableBase
{
    private DesktopItem _model;
    private bool _isSelected;
    private bool _showFileExtensions;

    public DesktopItemViewModel(
        DesktopItem model,
        bool isRecentItem = false,
        bool canReorder = true,
        bool showFileExtensions = false)
    {
        _model = model;
        IsRecentItem = isRecentItem;
        CanReorder = canReorder && !isRecentItem;
        _showFileExtensions = showFileExtensions;
        IconImage = WindowsShellIconProvider.GetIcon(model.ActivePath);
    }

    public DesktopItem Model => _model;
    public Guid Id => Model.Id;
    public string Name => GetDisplayName();
    public string PersistedName => Model.DisplayName;
    public string Path => Model.ActivePath;
    public bool IsMissing => Model.IsMissing;
    public bool IsRecentItem { get; }
    public bool CanReorder { get; }
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetField(ref _isSelected, value);
    }
    public ImageSource? IconImage { get; private set; }

    public void SetSelected(bool isSelected) => IsSelected = isSelected;

    public void SetShowFileExtensions(bool showFileExtensions)
    {
        if (_showFileExtensions == showFileExtensions)
        {
            return;
        }

        _showFileExtensions = showFileExtensions;
        RaisePropertyChanged(nameof(Name));
    }

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

    private string GetDisplayName()
    {
        if (Model.ItemType == DesktopItemType.Folder)
        {
            return Model.DisplayName;
        }

        var extension = System.IO.Path.GetExtension(Model.ActivePath);
        if (string.IsNullOrEmpty(extension))
        {
            return Model.DisplayName;
        }

        var hasExtension = Model.DisplayName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        if (_showFileExtensions)
        {
            return hasExtension ? Model.DisplayName : Model.DisplayName + extension;
        }

        return hasExtension
            ? Model.DisplayName[..^extension.Length]
            : Model.DisplayName;
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
