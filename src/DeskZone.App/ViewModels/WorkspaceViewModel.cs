using System.IO;
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
    private string _searchText = string.Empty;
    private bool _isBusy;

    public WorkspaceViewModel(LocalBackend backend, IShellService shell)
    {
        _backend = backend;
        _shell = shell;
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ApplySearchFilter();
            }
        }
    }

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
        ApplySearchFilter();
        StatusMessage = "已创建“新分类”，可以直接修改名称。";
        return viewModel;
    }

    public async Task ReloadFromStorageAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await ReloadAsync(cancellationToken);
            StatusMessage = Categories.Count == 0
                ? "数据已导入，但还没有分类。"
                : $"数据已导入，已加载 {Categories.Count} 个分类。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<CategoryViewModel> CreateCategoryFromFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(folderPath.Trim().Trim('"'));
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"拖入的文件夹不存在或已无法访问：{normalizedPath}");
        }

        var directory = new DirectoryInfo(normalizedPath);
        var childPaths = directory
            .EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.FullName)
            .ToArray();

        IsBusy = true;
        try
        {
            var category = await _backend.Categories.CreateAsync(directory.Name, "#126FF7", cancellationToken);
            var viewModel = new CategoryViewModel(category, Array.Empty<DesktopItem>());
            Categories.Add(viewModel);
            ApplySearchFilter();

            AddReferencesResult? result = null;
            if (childPaths.Length > 0)
            {
                result = await _backend.Items.AddReferencesAsync(category.Id, childPaths, cancellationToken);
                await ReloadCategoryItemsAsync(viewModel, cancellationToken);
            }

            StatusMessage = result is null
                ? $"已创建分类“{category.Name}”，但文件夹为空。"
                : result.Failures.Count == 0
                    ? $"已根据“{category.Name}”创建分类，并加入 {result.Accepted} 个入口。"
                    : $"已创建分类“{category.Name}”，加入 {result.Accepted} 个入口，{result.Failures.Count} 个项目未加入。";

            return viewModel;
        }
        finally
        {
            IsBusy = false;
        }
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
        if (category.IsSystem)
        {
            category.SetExpanded(expanded);
            return;
        }

        await _backend.Categories.SetCollapsedAsync(category.Id, !expanded, cancellationToken);
        category.SetExpanded(expanded);
    }

    public async Task ToggleAllCategoriesAsync(CancellationToken cancellationToken = default)
    {
        if (Categories.Count == 0)
        {
            return;
        }

        var expand = Categories.Any(category => !category.IsExpanded);
        IsBusy = true;
        try
        {
            foreach (var category in Categories)
            {
                if (category.IsSystem)
                {
                    category.SetExpanded(expand);
                    continue;
                }

                if (category.IsExpanded == expand)
                {
                    continue;
                }

                await _backend.Categories.SetCollapsedAsync(category.Id, !expand, cancellationToken);
                category.SetExpanded(expand);
            }

            StatusMessage = expand ? "已展开全部分类。" : "已折叠全部分类。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task MoveCategoryAsync(
        Guid sourceCategoryId,
        Guid targetCategoryId,
        CancellationToken cancellationToken = default)
    {
        var source = Categories.FirstOrDefault(category => category.Id == sourceCategoryId);
        var target = Categories.FirstOrDefault(category => category.Id == targetCategoryId);
        if (source is null || target is null || source == target || source.IsSystem || target.IsSystem)
        {
            return;
        }

        var originalOrder = Categories.ToArray();
        var sourceIndex = Categories.IndexOf(source);
        Categories.RemoveAt(sourceIndex);
        Categories.Insert(Categories.IndexOf(target), source);

        IsBusy = true;
        try
        {
            await _backend.Categories.ReorderAsync(
                Categories.Where(category => !category.IsSystem).Select(category => category.Id).ToArray(),
                cancellationToken);
            StatusMessage = $"已将分类“{source.Name}”移动到“{target.Name}”之前。";
        }
        catch
        {
            Categories.Clear();
            foreach (var category in originalOrder)
            {
                Categories.Add(category);
            }

            throw;
        }
        finally
        {
            IsBusy = false;
        }
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

    public async Task<MoveItemsResult> MovePathsIntoCategoryAsync(
        CategoryViewModel category,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await _backend.Items.MovePathsIntoCategoryAsync(category.Id, paths, cancellationToken);
            await ReloadAsync(cancellationToken);
            StatusMessage = result.Failures.Count == 0
                ? $"已移动 {result.Moved} 个项目到“{category.Name}”。"
                : $"已移动 {result.Moved} 个项目到“{category.Name}”，{result.Failures.Count} 个项目未移动。";
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task MoveItemToCategoryAsync(
        Guid itemId,
        CategoryViewModel targetCategory,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _backend.Items.MoveToCategoryAsync(new[] { itemId }, targetCategory.Id, cancellationToken);
            await ReloadAsync(cancellationToken);
            StatusMessage = $"已将项目移动到“{targetCategory.Name}”。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReorderItemsAsync(
        CategoryViewModel category,
        int sourceIndex,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        if (category.IsSystem || sourceIndex < 0 || sourceIndex >= category.Items.Count ||
            targetIndex < 0 || targetIndex >= category.Items.Count ||
            sourceIndex == targetIndex)
        {
            return;
        }

        var originalOrder = category.Items.ToArray();
        category.MoveItem(sourceIndex, targetIndex);

        IsBusy = true;
        try
        {
            await _backend.Items.ReorderAsync(
                category.Id,
                category.Items.Select(item => item.Id).ToArray(),
                cancellationToken);
            StatusMessage = "已调整项目顺序。";
        }
        catch
        {
            category.RestoreItems(originalOrder);
            category.ApplySearch(_searchText);
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task MoveItemToDesktopAsync(
        DesktopItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopDirectory))
        {
            desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        IsBusy = true;
        try
        {
            await _backend.Items.MoveItemToDirectoryAsync(item.Id, desktopDirectory, cancellationToken);
            await ReloadAsync(cancellationToken);
            StatusMessage = $"已将“{item.Name}”移动到桌面。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> RefreshMissingStatesAsync(CancellationToken cancellationToken = default)
    {
        if (Categories.Count == 0)
        {
            return 0;
        }

        IsBusy = true;
        try
        {
            var changed = 0;
            foreach (var category in Categories.Where(category => !category.IsRecentlyOpened))
            {
                changed += await _backend.Items.RefreshMissingStateAsync(category.Id, cancellationToken);
            }

            if (changed > 0)
            {
                await ReloadAsync(cancellationToken);
            }

            return changed;
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
        await _backend.Items.RecordOpenedAsync(item.Model, cancellationToken);
        await ReloadRecentlyOpenedItemsAsync(cancellationToken);
        ApplySearchFilter();
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
            var items = category.IsRecentlyOpened
                ? await GetRecentlyOpenedDesktopItemsAsync(category.Id, cancellationToken)
                : await _backend.Items.ListByCategoryAsync(category.Id, cancellationToken);
            Categories.Add(new CategoryViewModel(category, items));
        }

        ApplySearchFilter();
        _backend.FileSystemChanges.UpdatePaths(
            Categories
                .Where(category => !category.IsRecentlyOpened)
                .SelectMany(category => category.Items)
                .Select(item => item.Path));
    }

    private async Task ReloadRecentlyOpenedItemsAsync(CancellationToken cancellationToken)
    {
        var category = Categories.FirstOrDefault(candidate => candidate.IsRecentlyOpened);
        if (category is null)
        {
            return;
        }

        category.ReplaceItems(await GetRecentlyOpenedDesktopItemsAsync(category.Id, cancellationToken));
    }

    private async Task<IReadOnlyList<DesktopItem>> GetRecentlyOpenedDesktopItemsAsync(
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var recentItems = await _backend.Items.ListRecentlyOpenedAsync(10, cancellationToken);
        return recentItems
            .Select((item, index) => new DesktopItem(
                item.ItemId,
                categoryId,
                DesktopItemMode.Reference,
                item.Path,
                null,
                item.DisplayName,
                item.ItemType,
                index,
                !File.Exists(item.Path) && !Directory.Exists(item.Path),
                item.OpenedAt,
                item.OpenedAt))
            .ToArray();
    }

    private async Task ReloadCategoryItemsAsync(CategoryViewModel category, CancellationToken cancellationToken)
    {
        var items = await _backend.Items.ListByCategoryAsync(category.Id, cancellationToken);
        category.ReplaceItems(items);
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        foreach (var category in Categories)
        {
            category.ApplySearch(_searchText);
        }
    }
}
