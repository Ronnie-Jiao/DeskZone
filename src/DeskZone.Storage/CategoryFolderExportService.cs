using DeskZone.Core.Contracts;
using DeskZone.Core.Models;
using DeskZone.Core.Services;

namespace DeskZone.Storage;

public sealed class CategoryFolderExportService : ICategoryFolderExportService
{
    private const string ExportDirectoryPrefix = "DeskZone-分类导出-";
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly IWorkspaceStore _workspace;

    public CategoryFolderExportService(IWorkspaceStore workspace)
    {
        _workspace = workspace;
    }

    public async Task<CategoryFolderExportResult> ExportAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("请选择导出位置。", nameof(destinationDirectory));
        }

        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(destination))
        {
            throw new DirectoryNotFoundException("选择的导出位置不存在。请重新选择一个文件夹。");
        }

        var categories = await _workspace.GetCategoriesAsync(includeSystem: false, cancellationToken);
        if (categories.Count == 0)
        {
            throw new InvalidOperationException("当前没有可导出的自定义分类。");
        }

        var categoryExports = new List<(Category Category, string DirectoryName, IReadOnlyList<DesktopItem> Items)>();
        var usedCategoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = GetSafeDirectoryName(category.Name);
            if (usedCategoryNames.TryGetValue(directoryName, out var existingCategoryName))
            {
                throw new InvalidOperationException(
                    $"分类“{category.Name}”与“{existingCategoryName}”生成了相同的文件夹名称。请先调整分类名称后再导出。");
            }

            usedCategoryNames.Add(directoryName, category.Name);
            var items = await _workspace.GetItemsByCategoryAsync(category.Id, cancellationToken);
            foreach (var item in items)
            {
                var sourcePath = GetExistingItemPath(item, category);
                if (Directory.Exists(sourcePath) && IsSameOrDescendant(destination, sourcePath))
                {
                    throw new InvalidOperationException(
                        $"导出位置位于分类“{category.Name}”中的文件夹“{item.DisplayName}”内。请改选该文件夹之外的位置，避免把导出结果写回源文件夹。");
                }
            }

            categoryExports.Add((category, directoryName, items));
        }

        var outputDirectory = GetUniqueOutputDirectory(destination);
        var stagingDirectory = Path.Combine(destination, $".deskzone-export-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);

        var exportedItemCount = 0;
        try
        {
            foreach (var (category, directoryName, items) in categoryExports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var categoryDirectory = Path.Combine(stagingDirectory, directoryName);
                Directory.CreateDirectory(categoryDirectory);

                var usedItemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = GetExistingItemPath(item, category);
                    var isDirectory = Directory.Exists(sourcePath);
                    ThrowIfReparsePoint(sourcePath);

                    var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
                    if (string.IsNullOrWhiteSpace(sourceName))
                    {
                        sourceName = new DirectoryInfo(sourcePath).Name;
                    }

                    var itemName = GetUniqueItemName(sourceName, isDirectory, usedItemNames);
                    var targetPath = Path.Combine(categoryDirectory, itemName);
                    if (isDirectory)
                    {
                        await CopyDirectoryAsync(sourcePath, targetPath, cancellationToken);
                    }
                    else
                    {
                        await CopyFileAsync(sourcePath, targetPath, cancellationToken);
                    }

                    exportedItemCount++;
                }
            }

            Directory.Move(stagingDirectory, outputDirectory);
            return new CategoryFolderExportResult(outputDirectory, categoryExports.Count, exportedItemCount);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static string GetExistingItemPath(DesktopItem item, Category category)
    {
        if (string.IsNullOrWhiteSpace(item.ActivePath))
        {
            throw new InvalidOperationException(
                $"分类“{category.Name}”中的“{item.DisplayName}”没有有效路径，无法导出。");
        }

        var path = Path.GetFullPath(item.ActivePath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException(
                $"分类“{category.Name}”中的“{item.DisplayName}”已不存在，无法完成导出。请先刷新或移除该项目。",
                path);
        }

        return path;
    }

    private static string GetSafeDirectoryName(string categoryName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = categoryName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();
        var safeName = new string(characters).TrimEnd('.', ' ');
        if (safeName.Length == 0)
        {
            safeName = "_";
        }

        var deviceName = safeName.Split('.', 2)[0].TrimEnd(' ', '.');
        return ReservedWindowsNames.Contains(deviceName) ? $"_{safeName}" : safeName;
    }

    private static string GetUniqueItemName(
        string sourceName,
        bool isDirectory,
        ISet<string> usedNames)
    {
        var candidate = sourceName;
        for (var suffix = 2; !usedNames.Add(candidate); suffix++)
        {
            candidate = isDirectory
                ? $"{sourceName} ({suffix})"
                : $"{Path.GetFileNameWithoutExtension(sourceName)} ({suffix}){Path.GetExtension(sourceName)}";
        }

        return candidate;
    }

    private static string GetUniqueOutputDirectory(string destination)
    {
        var name = $"{ExportDirectoryPrefix}{DateTime.Now:yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(destination, name);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(destination, $"{name} ({suffix})");
        }

        return candidate;
    }

    private static bool IsSameOrDescendant(string path, string directory)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directoryPrefix = Path.EndsInDirectorySeparator(normalizedDirectory)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfReparsePoint(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"文件夹包含符号链接或联接点，无法安全导出：“{entry}”。");
            }

            var destination = Path.Combine(destinationDirectory, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                await CopyDirectoryAsync(entry, destination, cancellationToken);
            }
            else
            {
                await CopyFileAsync(entry, destination, cancellationToken);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"无法安全导出符号链接或联接点：“{path}”。");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
