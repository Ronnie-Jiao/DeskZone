using System.Collections.ObjectModel;
using DeskZone.Core.Models;

namespace DeskZone.App.ViewModels;

public sealed class CategoryViewModel : BindableBase
{
    private Category _model;
    private string _name;
    private string _searchText = string.Empty;
    private bool _isExpanded;
    private bool _isSearchActive;

    public CategoryViewModel(Category model, IEnumerable<DesktopItem> items)
    {
        _model = model;
        _name = model.Name;
        _isExpanded = !model.IsCollapsed;
        ReplaceItems(items);
    }

    public Guid Id => _model.Id;
    public string? Color => _model.Color;
    public bool IsSystem => _model.IsSystem;
    public bool IsRecentlyOpened => string.Equals(_model.SystemKey, SystemCategoryKeys.RecentlyOpened, StringComparison.Ordinal);
    public ObservableCollection<DesktopItemViewModel> Items { get; } = new();
    public ObservableCollection<DesktopItemViewModel> VisibleItems { get; } = new();

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
                RaisePropertyChanged(nameof(IsExpandedForDisplay));
            }
        }
    }

    public string ToggleGlyph => IsExpandedForDisplay ? "▾" : "▸";

    // "最近打开"只是其他分类项目的快捷汇总，搜索时不重复展示它。
    public bool IsVisible => !_isSearchActive || (!IsRecentlyOpened && VisibleItems.Count > 0);

    public bool IsExpandedForDisplay => IsExpanded || (_isSearchActive && VisibleItems.Count > 0);

    public int VisibleItemCount => _isSearchActive ? VisibleItems.Count : Items.Count;

    public string PersistedName => _model.Name;

    public void Apply(Category model)
    {
        _model = model;
        Name = model.Name;
        IsExpanded = !model.IsCollapsed;
        RaisePropertyChanged(nameof(Color));
        RaisePropertyChanged(nameof(IsSystem));
        RaisePropertyChanged(nameof(IsRecentlyOpened));
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
            Items.Add(new DesktopItemViewModel(
                item,
                isRecentItem: IsRecentlyOpened,
                canReorder: !IsSystem));
        }

        ApplySearch(string.Empty);
    }

    public void ApplySearch(string searchText)
    {
        _searchText = searchText ?? string.Empty;
        _isSearchActive = !string.IsNullOrWhiteSpace(searchText);
        var query = _searchText.Trim();

        VisibleItems.Clear();
        foreach (var item in Items)
        {
            if (!_isSearchActive ||
                item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                VisibleItems.Add(item);
            }
        }

        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(IsExpandedForDisplay));
        RaisePropertyChanged(nameof(VisibleItemCount));
    }

    public void MoveItem(int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= Items.Count || targetIndex < 0 || targetIndex >= Items.Count || sourceIndex == targetIndex)
        {
            return;
        }

        Items.Move(sourceIndex, targetIndex);
        // The UI is bound to VisibleItems rather than Items. Reapply the
        // current filter immediately so a successful drag is visible without
        // requiring a later open/search/reload action.
        ApplySearch(_searchText);
    }

    public void RestoreItems(IEnumerable<DesktopItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }
}
