using System.IO;
using System.Collections.ObjectModel;
using DeskZone.App;
using DeskZone.Core.Models;
using DeskZone.Core.Services;
using DeskZone.Storage;

namespace DeskZone.App.ViewModels;

public enum WorkspaceDisplayMode
{
    Custom,
    Smart
}

public sealed class WorkspaceViewModel : BindableBase
{
    /// <summary>“最近打开”系统分类的默认显示数量。</summary>
    public const int DefaultRecentItemsDisplayLimit = 15;

    public const int MinimumRecentItemsDisplayLimit = 1;
    public const int MaximumRecentItemsDisplayLimit = 30;

    private const string RecentItemsDisplayLimitSettingKey = "recent-items-display-limit";

    private readonly LocalBackend _backend;
    private readonly IShellService _shell;
    private readonly IShortcutTargetResolver? _shortcutTargetResolver;
    private string _statusMessage = "正在加载本地工作区…";
    private string _searchText = string.Empty;
    private bool _isBusy;
    private WorkspaceDisplayMode _displayMode = WorkspaceDisplayMode.Custom;
    private int _recentItemsDisplayLimit = DefaultRecentItemsDisplayLimit;

    public WorkspaceViewModel(
        LocalBackend backend,
        IShellService shell,
        IShortcutTargetResolver? shortcutTargetResolver = null)
    {
        _backend = backend;
        _shell = shell;
        _shortcutTargetResolver = shortcutTargetResolver;
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = new();
    public ObservableCollection<CategoryViewModel> SmartCategories { get; } = new();

    public ObservableCollection<CategoryViewModel> DisplayedCategories =>
        IsSmartMode ? SmartCategories : Categories;

    public WorkspaceDisplayMode DisplayMode
    {
        get => _displayMode;
        private set
        {
            if (!SetField(ref _displayMode, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(IsCustomMode));
            RaisePropertyChanged(nameof(IsSmartMode));
            RaisePropertyChanged(nameof(CanSmartImport));
            RaisePropertyChanged(nameof(DisplayedCategories));
        }
    }

    public bool IsCustomMode => DisplayMode == WorkspaceDisplayMode.Custom;
    public bool IsSmartMode => DisplayMode == WorkspaceDisplayMode.Smart;

    public int RecentItemsDisplayLimit
    {
        get => _recentItemsDisplayLimit;
        private set => SetField(ref _recentItemsDisplayLimit, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                RaisePropertyChanged(nameof(HasSearchText));
                ApplySearchFilter();
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanSwitchMode));
                RaisePropertyChanged(nameof(CanSmartImport));
            }
        }
    }

    public bool CanSwitchMode => !IsBusy;

    public bool CanSmartImport => IsCustomMode && !IsBusy;

    public async Task SetDisplayModeAsync(
        WorkspaceDisplayMode mode,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy || DisplayMode == mode)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (mode == WorkspaceDisplayMode.Smart)
            {
                await ReloadSmartGroupsAsync(cancellationToken);
            }

            DisplayMode = mode;
            ApplySearchFilter();
            UpdateFileSystemWatchPaths();
            StatusMessage = mode == WorkspaceDisplayMode.Smart
                ? "已切换到智能分组。"
                : "已切换到自定义分组。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var savedRecentItemsDisplayLimit = await _backend.Settings.LoadAsync<int?>(
                RecentItemsDisplayLimitSettingKey,
                cancellationToken);
            if (savedRecentItemsDisplayLimit.HasValue)
            {
                RecentItemsDisplayLimit = NormalizeRecentItemsDisplayLimit(savedRecentItemsDisplayLimit.Value);
            }

            await ReloadAsync(cancellationToken);
            var stalePaths = Categories
                .Where(category => !category.IsRecentlyOpened)
                .SelectMany(category => category.Items)
                .Where(item => IsPathDefinitelyMissing(item.Path) && IsPathRootAvailable(item.Path))
                .Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var removed = await RemoveMissingItemsCoreAsync(stalePaths, cancellationToken);
            removed += await RemoveMissingShortcutTargetsCoreAsync(cancellationToken);
            if (removed > 0)
            {
                await ReloadAsync(cancellationToken);
            }

            StatusMessage = Categories.Count == 0
                ? "还没有分类。点击“新建分类”开始整理。"
                : $"已加载 {Categories.Count} 个分类。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetRecentItemsDisplayLimitAsync(
        int displayLimit,
        CancellationToken cancellationToken = default)
    {
        if (displayLimit is < MinimumRecentItemsDisplayLimit or > MaximumRecentItemsDisplayLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayLimit),
                $"最近打开数量必须在 {MinimumRecentItemsDisplayLimit} 到 {MaximumRecentItemsDisplayLimit} 之间。");
        }

        if (RecentItemsDisplayLimit == displayLimit)
        {
            return;
        }

        await _backend.Settings.SaveAsync(
            RecentItemsDisplayLimitSettingKey,
            displayLimit,
            cancellationToken);
        RecentItemsDisplayLimit = displayLimit;

        foreach (var category in Categories.Concat(SmartCategories).Where(category => category.IsRecentlyOpened))
        {
            category.ReplaceItems(await GetRecentlyOpenedDesktopItemsAsync(category.Id, cancellationToken));
        }

        ApplySearchFilter();
    }

    public async Task<CategoryViewModel> CreateCategoryAsync(CancellationToken cancellationToken = default)
    {
        if (IsSmartMode)
        {
            throw new InvalidOperationException("智能分组不可新建分类，请先切换到自定义。");
        }

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

    public async Task<(int CandidateCount, MoveItemsResult? MoveResult)> CreateCategoryFromFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (IsSmartMode)
        {
            throw new InvalidOperationException("智能分组不可新建分类，请先切换到自定义。");
        }

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

            MoveItemsResult? result = null;
            if (childPaths.Length > 0)
            {
                result = await _backend.Items.MovePathsIntoCategoryAsync(category.Id, childPaths, cancellationToken);
                await ReloadAsync(cancellationToken);
            }

            StatusMessage = result is null
                ? $"已创建分类“{category.Name}”，但文件夹为空。"
                : result.Failures.Count == 0
                    ? $"已创建分类“{category.Name}”，并将 {result.Moved} 个项目收纳到本地数据目录。"
                    : $"已创建分类“{category.Name}”，已收纳 {result.Moved} 个项目，{result.Failures.Count} 个项目未移动并保留在源文件夹。";

            return (childPaths.Length, result);
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

    public async Task RenameItemAsync(
        DesktopItemViewModel item,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = displayName.Trim();
        if (string.Equals(trimmed, item.PersistedName, StringComparison.Ordinal))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await _backend.Items.RenameDisplayNameAsync(item.Model, trimmed, cancellationToken);
            await ReloadAsync(cancellationToken);
            StatusMessage = $"已将“{item.PersistedName}”的显示名称修改为“{updated.DisplayName}”。";
        }
        finally
        {
            IsBusy = false;
        }
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
            if (result.Accepted > 0 && !category.IsExpanded)
            {
                await _backend.Categories.SetCollapsedAsync(category.Id, false, cancellationToken);
                category.SetExpanded(true);
            }

            if (result.MovedFromOtherCategories > 0)
            {
                await ReloadAsync(cancellationToken);
            }
            else
            {
                await ReloadCategoryItemsAsync(category, cancellationToken);
                UpdateFileSystemWatchPaths();
            }
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
            if (result.Moved > 0 && !category.IsExpanded)
            {
                await _backend.Categories.SetCollapsedAsync(category.Id, false, cancellationToken);
                category.SetExpanded(true);
            }

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

    public DesktopItemViewModel? FindDuplicateProgramShortcut(CategoryViewModel category, string candidatePath)
    {
        if (category.IsSystem ||
            !string.Equals(Path.GetExtension(candidatePath), ".lnk", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidatePath))
        {
            return null;
        }

        var normalizedCandidatePath = NormalizePathForComparison(candidatePath);
        var candidateName = Path.GetFileName(candidatePath);
        var candidateTarget = _shortcutTargetResolver is not null &&
                              _shortcutTargetResolver.TryResolveTarget(candidatePath, out var resolvedCandidateTarget)
            ? NormalizePathForComparison(resolvedCandidateTarget)
            : null;

        foreach (var item in category.Items)
        {
            if (item.Model.ItemType != DesktopItemType.Shortcut)
            {
                continue;
            }

            var existingPath = NormalizePathForComparison(item.Path);
            if (normalizedCandidatePath is not null &&
                existingPath is not null &&
                string.Equals(normalizedCandidatePath, existingPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(item.Path), candidateName, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (candidateTarget is null ||
                _shortcutTargetResolver is null ||
                !_shortcutTargetResolver.TryResolveTarget(item.Path, out var existingTarget))
            {
                continue;
            }

            var normalizedExistingTarget = NormalizePathForComparison(existingTarget);
            if (normalizedExistingTarget is not null &&
                string.Equals(candidateTarget, normalizedExistingTarget, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    public async Task<bool> ReplaceProgramShortcutAsync(
        CategoryViewModel category,
        DesktopItemViewModel existingItem,
        string incomingPath,
        CancellationToken cancellationToken = default)
    {
        if (category.IsSystem)
        {
            throw new InvalidOperationException("系统分类中的程序入口不能被替换。");
        }

        IsBusy = true;
        try
        {
            var sourceRemoved = await _backend.Items.ReplaceShortcutWithPathAsync(
                existingItem.Id,
                incomingPath,
                cancellationToken);
            await ReloadAsync(cancellationToken);
            StatusMessage = sourceRemoved
                ? $"已替换“{existingItem.PersistedName}”。"
                : $"已替换“{existingItem.PersistedName}”，原快捷方式仍保留在原位置。";
            return sourceRemoved;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 智能导入：枚举桌面顶层项目，按名称与类型特征匹配到用户自定义分类，并剪切移动到对应分类。
    /// 匹配不上的项目保留在桌面。返回 null 表示当前还没有可用的自定义分类。
    /// </summary>
    public async Task<SmartImportResult?> SmartImportFromDesktopAsync(CancellationToken cancellationToken = default)
    {
        if (IsSmartMode)
        {
            throw new InvalidOperationException("智能导入只适用于自定义分类，请先切换到“自定义”。");
        }

        var targets = Categories
            .Where(category => !category.IsSystem)
            .Select((category, index) => new SmartImportTarget(category.Id, category.Name, index))
            .ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "还没有创建分类，请先新建分类再使用智能导入。";
            return null;
        }

        var plan = SmartImportService.BuildPlan(targets, SmartImportService.EnumerateDesktopPaths());
        if (plan.Assignments.Count == 0)
        {
            StatusMessage = plan.CandidateCount == 0
                ? "桌面上没有可以导入的项目。"
                : $"桌面上的 {plan.CandidateCount} 个项目都没有匹配到分类，已保留在桌面。";
            return new SmartImportResult(plan.CandidateCount, 0, 0, plan.Assignments, plan.UnmatchedPaths);
        }

        IsBusy = true;
        try
        {
            var moved = 0;
            var failed = 0;
            foreach (var assignment in plan.Assignments)
            {
                var result = await _backend.Items.MovePathsIntoCategoryAsync(
                    assignment.CategoryId,
                    assignment.Paths,
                    cancellationToken);
                moved += result.Moved;
                failed += result.Failures.Count;
            }

            await ReloadAsync(cancellationToken);
            StatusMessage = $"智能导入完成：已移动 {moved} 个项目到 {plan.Assignments.Count} 个分类。";
            return new SmartImportResult(plan.CandidateCount, moved, failed, plan.Assignments, plan.UnmatchedPaths);
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
        if (IsSmartMode)
        {
            IsBusy = true;
            try
            {
                await ReloadSmartGroupsAsync(cancellationToken);
                UpdateFileSystemWatchPaths();
                ApplySearchFilter();
                return 0;
            }
            finally
            {
                IsBusy = false;
            }
        }

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

    public async Task<int> RemoveMissingShortcutTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return 0;
        }

        IsBusy = true;
        try
        {
            var removed = await RemoveMissingShortcutTargetsCoreAsync(cancellationToken);
            if (removed > 0)
            {
                await ReloadAsync(cancellationToken);
                StatusMessage = $"已清理 {removed} 个目标已卸载的快捷方式及其最近打开记录。";
            }

            return removed;
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
        if (IsSmartMode)
        {
            await ReloadSmartGroupsAsync(cancellationToken);
        }
        else
        {
            await ReloadRecentlyOpenedItemsAsync(cancellationToken);
        }

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

    public async Task<int> RemoveDeletedItemsAsync(
        IReadOnlyCollection<string> deletedPaths,
        CancellationToken cancellationToken = default)
    {
        if (deletedPaths.Count == 0)
        {
            return 0;
        }

        IsBusy = true;
        try
        {
            var removed = await RemoveMissingItemsCoreAsync(deletedPaths, cancellationToken);
            if (removed > 0)
            {
                await ReloadAsync(cancellationToken);
                StatusMessage = $"已清理 {removed} 个已删除项目及其最近打开记录。";
            }

            return removed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<int> RemoveMissingItemsCoreAsync(
        IReadOnlyCollection<string> deletedPaths,
        CancellationToken cancellationToken)
    {
        if (deletedPaths.Count == 0)
        {
            return 0;
        }

        var normalizedDeletedPaths = deletedPaths
            .Select(NormalizePathForComparison)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedDeletedPaths.Length == 0)
        {
            return 0;
        }

        var itemIds = Categories
            .Where(category => !category.IsRecentlyOpened)
            .SelectMany(category => category.Items)
            .Select(item => item.Model)
            .Where(item => IsPathDefinitelyMissing(item.ActivePath) &&
                           normalizedDeletedPaths.Any(deletedPath => IsSameOrDescendantPath(item.ActivePath, deletedPath)))
            .Select(item => item.Id)
            .Distinct()
            .ToArray();

        return await _backend.Items.RemoveMissingItemsAsync(itemIds, cancellationToken);
    }

    private async Task<int> RemoveMissingShortcutTargetsCoreAsync(CancellationToken cancellationToken)
    {
        if (_shortcutTargetResolver is null)
        {
            return 0;
        }

        var missingShortcutIds = new List<Guid>();
        foreach (var item in Categories
                     .Where(category => !category.IsRecentlyOpened)
                     .SelectMany(category => category.Items)
                     .Select(item => item.Model)
                     .Where(item => item.ItemType == DesktopItemType.Shortcut))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shortcutTargetResolver.TryResolveTarget(item.ActivePath, out var targetPath) &&
                IsPathRootAvailable(targetPath) &&
                IsPathDefinitelyMissing(targetPath))
            {
                missingShortcutIds.Add(item.Id);
            }
        }

        return await _backend.Items.RemoveMissingItemsAsync(missingShortcutIds, cancellationToken);
    }

    private static bool IsSameOrDescendantPath(string itemPath, string deletedPath)
    {
        var normalizedItemPath = NormalizePathForComparison(itemPath);
        var normalizedDeletedPath = NormalizePathForComparison(deletedPath);
        if (normalizedItemPath is null || normalizedDeletedPath is null)
        {
            return false;
        }

        if (string.Equals(normalizedItemPath, normalizedDeletedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(normalizedDeletedPath, normalizedItemPath);
        return relativePath != "." &&
               !Path.IsPathRooted(relativePath) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsPathDefinitelyMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPathRootAvailable(string path)
    {
        var normalizedPath = NormalizePathForComparison(path);
        if (normalizedPath is null)
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(normalizedPath);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

        if (IsSmartMode)
        {
            await ReloadSmartGroupsAsync(cancellationToken);
        }

        ApplySearchFilter();
        UpdateFileSystemWatchPaths();
    }

    private async Task ReloadSmartGroupsAsync(CancellationToken cancellationToken)
    {
        var recentItems = await _backend.Items.ListRecentlyOpenedAsync(int.MaxValue, cancellationToken);
        var recentOrderByItemId = new Dictionary<Guid, int>();
        var recentOrderByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < recentItems.Count; index++)
        {
            var recentItem = recentItems[index];
            recentOrderByItemId.TryAdd(recentItem.ItemId, index);

            var normalizedPath = NormalizePathForComparison(recentItem.Path);
            if (normalizedPath is not null)
            {
                recentOrderByPath.TryAdd(normalizedPath, index);
            }
        }

        var sourceItems = Categories
            .Where(category => !category.IsRecentlyOpened)
            .SelectMany(category => category.Items)
            .Select(item => item.Model)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToArray();
        var classifiedItems = await Task.Run(
            () => sourceItems
                .Select(item => (
                    Item: item,
                    GroupKey: SmartGroupingService.Classify(item),
                    RecentOrder: GetRecentOrder(item, recentOrderByItemId, recentOrderByPath)))
                .ToArray(),
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var smartCategories = new List<CategoryViewModel>();
        foreach (var definition in SmartGroupingService.Definitions)
        {
            var categoryId = Guid.NewGuid();
            IReadOnlyList<DesktopItem> items;
            if (definition.Key == SystemCategoryKeys.RecentlyOpened)
            {
                items = await GetRecentlyOpenedDesktopItemsAsync(categoryId, cancellationToken);
            }
            else
            {
                var groupItems = classifiedItems
                    .Where(item => string.Equals(item.GroupKey, definition.Key, StringComparison.Ordinal))
                    .OrderBy(item => item.RecentOrder)
                    .ThenBy(item => item.Item.CustomOrder)
                    .ToArray();
                if (groupItems.Length == 0)
                {
                    continue;
                }

                items = groupItems
                    .Select((item, index) => new DesktopItem(
                        item.Item.Id,
                        categoryId,
                        item.Item.ItemMode,
                        item.Item.OriginalPath,
                        item.Item.ManagedPath,
                        item.Item.DisplayName,
                        item.Item.ItemType,
                        index,
                        SmartGroupingService.IsMissing(item.Item),
                        item.Item.CreatedAt,
                        item.Item.UpdatedAt))
                    .ToArray();
            }

            var category = new Category(
                categoryId,
                definition.Name,
                definition.OrderIndex,
                "grid",
                "#126FF7",
                false,
                definition.Key == SystemCategoryKeys.RecentlyOpened ? "recent" : "smart",
                definition.Key,
                now,
                now);
            smartCategories.Add(new CategoryViewModel(category, items));
        }

        SmartCategories.Clear();
        foreach (var category in smartCategories)
        {
            SmartCategories.Add(category);
        }
    }

    private async Task ReloadRecentlyOpenedItemsAsync(CancellationToken cancellationToken)
    {
        var category = DisplayedCategories.FirstOrDefault(candidate => candidate.IsRecentlyOpened);
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
        var recentItems = await _backend.Items.ListRecentlyOpenedAsync(_recentItemsDisplayLimit, cancellationToken);
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

    private static int NormalizeRecentItemsDisplayLimit(int displayLimit)
    {
        return Math.Clamp(
            displayLimit,
            MinimumRecentItemsDisplayLimit,
            MaximumRecentItemsDisplayLimit);
    }

    private static int GetRecentOrder(
        DesktopItem item,
        IReadOnlyDictionary<Guid, int> recentOrderByItemId,
        IReadOnlyDictionary<string, int> recentOrderByPath)
    {
        if (recentOrderByItemId.TryGetValue(item.Id, out var order))
        {
            return order;
        }

        var normalizedPath = NormalizePathForComparison(item.ActivePath);
        return normalizedPath is not null && recentOrderByPath.TryGetValue(normalizedPath, out order)
            ? order
            : int.MaxValue;
    }

    private static string? NormalizePathForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmedPath = path.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmedPath;
        }
    }

    private async Task ReloadCategoryItemsAsync(CategoryViewModel category, CancellationToken cancellationToken)
    {
        var items = await _backend.Items.ListByCategoryAsync(category.Id, cancellationToken);
        category.ReplaceItems(items);
        ApplySearchFilter();
    }

    private void UpdateFileSystemWatchPaths()
    {
        _backend.FileSystemChanges.UpdatePaths(
            Categories
                .Where(category => !category.IsRecentlyOpened)
                .SelectMany(category => category.Items)
                .Select(item => item.Path));
    }

    private void ApplySearchFilter()
    {
        foreach (var category in DisplayedCategories)
        {
            category.ApplySearch(_searchText);
        }
    }
}
