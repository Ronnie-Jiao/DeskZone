using System.IO;
using System.Text;

namespace DeskZone.App;

/// <summary>一个可供智能导入匹配的目标分类。</summary>
public sealed record SmartImportTarget(Guid CategoryId, string CategoryName, int OrderIndex);

/// <summary>匹配到某个分类的一组桌面项目。</summary>
public sealed record SmartImportAssignment(Guid CategoryId, string CategoryName, IReadOnlyList<string> Paths);

/// <summary>导入前形成的匹配计划：哪些项目去哪个分类，哪些没有匹配上。</summary>
public sealed record SmartImportPlan(
    IReadOnlyList<SmartImportAssignment> Assignments,
    IReadOnlyList<string> UnmatchedPaths,
    int CandidateCount);

/// <summary>智能导入的执行结果，供界面展示。</summary>
public sealed record SmartImportResult(
    int CandidateCount,
    int MovedCount,
    int FailedCount,
    IReadOnlyList<SmartImportAssignment> Assignments,
    IReadOnlyList<string> UnmatchedPaths);

/// <summary>
/// 智能导入：把桌面上的文件/文件夹/快捷方式按“文件名称 + 类型特征”匹配到用户自定义的分类名上。
/// 分类名由用户自由命名，因此这里不做固定词典匹配，而是同时比较名称字面重合度与类型同义词。
/// </summary>
internal static class SmartImportService
{
    // 类型同义词用于把“文件是什么”映射到用户可能起的中文分类名上。
    // 刻意不收录“文件”这类过于宽泛的词，避免把文档误分到“文件目录”之类的分类。
    private static readonly string[] ApplicationSynonyms =
        { "应用", "软件", "程序", "工具", "快捷方式", "入口", "app", "tool" };

    private static readonly string[] ArchiveSynonyms =
        { "压缩", "安装", "归档", "备份", "archive" };

    private static readonly string[] DocumentSynonyms =
        { "文档", "资料", "表格", "报表", "笔记", "文档资料", "ppt", "word", "excel", "pdf", "document" };

    private static readonly string[] ImageSynonyms =
        { "图片", "图像", "照片", "壁纸", "设计", "素材", "image", "photo", "picture" };

    private static readonly string[] MediaSynonyms =
        { "视频", "音乐", "音频", "影视", "电影", "媒体", "歌曲", "video", "music", "audio" };

    private static readonly string[] FolderSynonyms =
        { "文件夹", "目录", "分类", "folder", "directory" };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".aiff", ".ape", ".avi", ".flac", ".flv", ".m4a", ".m4v", ".mid", ".mkv",
        ".mov", ".mp3", ".mp4", ".mpeg", ".mpg", ".ogg", ".rmvb", ".wav", ".webm", ".wma", ".wmv"
    };

    /// <summary>列出桌面顶层中可以被导入的项目（跳过隐藏/系统文件）。</summary>
    public static IReadOnlyList<string> EnumerateDesktopPaths()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            return Array.Empty<string>();
        }

        return new DirectoryInfo(desktop)
            .EnumerateFileSystemInfos()
            .Where(info => !IsHiddenOrSystem(info))
            .Where(info => !string.Equals(info.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => info.FullName)
            .ToArray();
    }

    /// <summary>为桌面上的一批项目生成匹配计划；匹配不上的项目会原样保留在桌面。</summary>
    public static SmartImportPlan BuildPlan(IReadOnlyList<SmartImportTarget> targets, IReadOnlyList<string> paths)
    {
        var buckets = new Dictionary<Guid, List<string>>();
        var unmatched = new List<string>();

        foreach (var path in paths ?? Array.Empty<string>())
        {
            var target = MatchTarget(path, targets);
            if (target is null)
            {
                unmatched.Add(path);
                continue;
            }

            if (!buckets.TryGetValue(target.CategoryId, out var bucket))
            {
                bucket = new List<string>();
                buckets[target.CategoryId] = bucket;
            }

            bucket.Add(path);
        }

        var assignments = targets
            .Where(target => buckets.ContainsKey(target.CategoryId))
            .Select(target => new SmartImportAssignment(target.CategoryId, target.CategoryName, buckets[target.CategoryId]))
            .ToArray();

        return new SmartImportPlan(assignments, unmatched, (paths ?? Array.Empty<string>()).Count);
    }

    private static SmartImportTarget? MatchTarget(string path, IReadOnlyList<SmartImportTarget> targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return null;
        }

        ItemSignals signals;
        try
        {
            signals = Analyze(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var synonyms = SynonymsFor(ResolveTypeKey(signals));
        if (synonyms.Length == 0)
        {
            // 类型无法归纳（例如普通文本说明文件）时不猜测归属，保留在桌面。
            return null;
        }

        var haystack = Compact(signals.Name);
        if (!string.IsNullOrWhiteSpace(signals.TargetName))
        {
            haystack += "\u0000" + Compact(signals.TargetName);
        }

        var tokens = Tokenize(signals.Name);
        tokens.AddRange(Tokenize(signals.TargetName));

        SmartImportTarget? best = null;
        var bestScore = 0;
        // 按用户定义的顺序遍历，分数相同时保留靠前的分类。
        foreach (var target in targets.OrderBy(target => target.OrderIndex))
        {
            var score = ScoreCategory(target.CategoryName, haystack, tokens, synonyms);
            if (score > bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        return best;
    }

    private static int ScoreCategory(
        string categoryName,
        string haystack,
        IReadOnlyList<string> tokens,
        string[] synonyms)
    {
        var categoryCompact = Compact(categoryName);
        if (categoryCompact.Length < 2)
        {
            return 0;
        }

        var score = 0;

        // 1) 分类名与文件（或快捷方式目标）名称整体互相包含，信号最强。
        if (categoryCompact.Length >= 2 && haystack.Contains(categoryCompact, StringComparison.CurrentCultureIgnoreCase))
        {
            score += 12 + categoryCompact.Length * 2;
        }

        // 2) 文件名称分词出现在分类名中，例如“微信开发者工具”里的“工具”。
        foreach (var token in tokens)
        {
            if (token.Length >= 2 && categoryCompact.Contains(token, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 7 + token.Length;
            }
        }

        // 3) 分类名分词出现在文件名称中，例如分类“Clash”对文件“Clash Verge.lnk”。
        foreach (var token in Tokenize(categoryName))
        {
            if (token.Length >= 2 && haystack.Contains(token, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 7 + token.Length;
            }
        }

        // 4) 类型同义词，例如“.zip → 压缩”对分类“压缩安装”。
        foreach (var synonym in synonyms)
        {
            if (categoryCompact.Contains(synonym, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 4;
            }
        }

        return score;
    }

    private static ItemSignals Analyze(string path)
    {
        if (Directory.Exists(path))
        {
            return new ItemSignals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                null,
                string.Empty,
                path,
                null,
                true);
        }

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var targetPath = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
            ? SmartGroupingService.TryResolveShortcutTarget(path)
            : null;

        var targetIsFolder = !string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath);
        var targetName = string.IsNullOrWhiteSpace(targetPath)
            ? null
            : targetIsFolder
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(targetPath))
                : Path.GetFileNameWithoutExtension(targetPath);

        return new ItemSignals(
            Path.GetFileNameWithoutExtension(fileName),
            targetName,
            targetIsFolder ? string.Empty : extension,
            path,
            targetPath,
            targetIsFolder);
    }

    private static string ResolveTypeKey(ItemSignals signals)
    {
        if (signals.IsFolder)
        {
            return "folder";
        }

        var extension = signals.Extension.ToLowerInvariant();
        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "application";
        }

        if (SmartGroupingService.IsArchiveOrInstaller(extension, signals.TargetPath ?? signals.SourcePath))
        {
            return "archive";
        }

        if (SmartGroupingService.ApplicationExtensions.Contains(extension))
        {
            return "application";
        }

        if (SmartGroupingService.DocumentExtensions.Contains(extension))
        {
            return "document";
        }

        if (SmartGroupingService.ImageExtensions.Contains(extension))
        {
            return "image";
        }

        return MediaExtensions.Contains(extension) ? "media" : "other";
    }

    private static string[] SynonymsFor(string typeKey) => typeKey switch
    {
        "folder" => FolderSynonyms,
        "application" => ApplicationSynonyms,
        "archive" => ArchiveSynonyms,
        "document" => DocumentSynonyms,
        "image" => ImageSynonyms,
        "media" => MediaSynonyms,
        _ => Array.Empty<string>()
    };

    private static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var buffer = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer.Append(character);
            }
            else if (buffer.Length > 0)
            {
                tokens.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            tokens.Add(buffer.ToString());
        }

        return tokens;
    }

    /// <summary>去掉空白与常见分隔符，便于比较“Agent 工具”这类带空格的分类名。</summary>
    private static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || character is '-' or '_' or '.' or '·' or '－')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsHiddenOrSystem(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        return (attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
               (attributes & FileAttributes.System) == FileAttributes.System;
    }

    private sealed record ItemSignals(
        string Name,
        string? TargetName,
        string Extension,
        string SourcePath,
        string? TargetPath,
        bool IsFolder);
}
