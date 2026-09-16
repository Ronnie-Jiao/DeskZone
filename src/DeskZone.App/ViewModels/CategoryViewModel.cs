using System.Collections.ObjectModel;
using DeskZone.Core.Models;

namespace DeskZone.App.ViewModels;

public sealed class CategoryViewModel : BindableBase
{
    private Category _model;
    private string _name;
    private bool _isExpanded;

    public CategoryViewModel(Category model, IEnumerable<DesktopItem> items)
    {
        _model = model;
        _name = model.Name;
        _isExpanded = !model.IsCollapsed;
        ReplaceItems(items);
    }

    public Guid Id => _model.Id;
    public string? Color => _model.Color;
    public ObservableCollection<DesktopItemViewModel> Items { get; } = new();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetField(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(ToggleGlyph));
            }
        }
    }

    public string ToggleGlyph => IsExpanded ? "▾" : "▸";

    public string PersistedName => _model.Name;

    public void Apply(Category model)
    {
        _model = model;
        Name = model.Name;
        IsExpanded = !model.IsCollapsed;
        RaisePropertyChanged(nameof(Color));
    }

    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
    }

    public void ReplaceItems(IEnumerable<DesktopItem> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new DesktopItemViewModel(item));
        }
    }
}
