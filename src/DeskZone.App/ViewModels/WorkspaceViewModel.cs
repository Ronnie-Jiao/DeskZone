using System.Collections.ObjectModel;
using DeskZone.Core.Models;
using DeskZone.Core.Services;
using DeskZone.Storage;

namespace DeskZone.App.ViewModels;

public sealed class WorkspaceViewModel : BindableBase
{
    private readonly LocalBackend _backend;
    private readonly IShellService _shell;
    private string _statusMessage = "正在加载本地工作区…";
    private bool _isBusy;

    public WorkspaceViewModel(LocalBackend backend, IShellService shell)
    {
        _backend = backend;
        _shell = shell;
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await ReloadAsync(cancellationToken);
            StatusMessage = Categories.Count == 0
                ? "还没有分类。点击“新建分类”开始整理。"
                : $"已加载 {Categories.Count} 个分类。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<CategoryViewModel> CreateCategoryAsync(CancellationToken cancellationToken = default)
    {
        var category = await _backend.Categories.CreateAsync("新分类", "#126FF7", cancellationToken);
        var viewModel = new CategoryViewModel(category, Array.Empty<DesktopItem>());
        Categories.Add(viewModel);
        StatusMessage = "已创建“新分类”，可以直接修改名称。";
        return viewModel;
    }

    public async Task RenameCategoryAsync(CategoryViewModel category, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (string.Equals(trimmed, category.PersistedName, StringComparison.Ordinal))
        {
            category.Name = category.PersistedName;
            return;
        }

        var updated = await _backend.Categories.RenameAsync(category.Id, trimmed, cancellationToken);
        category.Apply(updated);
        StatusMessage = $"分类已重命名为“{updated.Name}”。";
    }

    public async Task ToggleCategoryAsync(CategoryViewModel category, CancellationToken cancellationToken = default)
    {
        var expanded = !category.IsExpanded;
        await _backend.Categories.SetCollapsedAsync(category.Id, !expanded, cancellationToken);
        category.SetExpanded(expanded);
    }

    public async Task DeleteCategoryAsync(CategoryViewModel category, bool moveItemsToUnclassified, CancellationToken cancellationToken = default)
    {
        var mode = moveItemsToUnclassified
            ? CategoryDeleteMode.MoveItemsToUnclassified
            : CategoryDeleteMode.RejectIfNotEmpty;

        await _backend.Categories.DeleteAsync(category.Id, mode, cancellationToken: cancellationToken);
        Categories.Remove(category);
        StatusMessage = moveItemsToUnclassified
            ? $"已删除“{category.Name}”，其中的入口已移到“未分类”。"
            : $"已删除空分类“{category.Name}”。";
    }

    public async Task AddReferencesAsync(CategoryViewModel category, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await _backend.Items.AddReferencesAsync(category.Id, paths, cancellationToken);
            await ReloadCategoryItemsAsync(category, cancellationToken);
            StatusMessage = result.Failures.Count == 0
                ? $"已接收 {result.Accepted} 个入口（仅建立本地引用，不移动真实文件）。"
                : $"已接收 {result.Accepted} 个入口，{result.Failures.Count} 个路径未加入。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenItemAsync(DesktopItemViewModel item, CancellationToken cancellationToken = default)
    {
        if (item.IsMissing)
        {
            StatusMessage = $"“{item.Name}”已不在原位置。";
            return;
        }

        await _shell.OpenAsync(item.Path, cancellationToken);
        StatusMessage = $"已交给 Windows 打开“{item.Name}”。";
    }

    public async Task RevealItemAsync(DesktopItemViewModel item, CancellationToken cancellationToken = default)
    {
        if (item.IsMissing)
        {
            StatusMessage = $"“{item.Name}”已不在原位置。";
            return;
        }

        await _shell.RevealInExplorerAsync(item.Path, cancellationToken);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var categories = await _backend.Categories.ListAsync(cancellationToken);
        Categories.Clear();

        foreach (var category in categories)
        {
            var items = await _backend.Items.ListByCategoryAsync(category.Id, cancellationToken);
            Categories.Add(new CategoryViewModel(category, items));
        }
    }

    private async Task ReloadCategoryItemsAsync(CategoryViewModel category, CancellationToken cancellationToken)
    {
        var items = await _backend.Items.ListByCategoryAsync(category.Id, cancellationToken);
        category.ReplaceItems(items);
    }
}
