using DeskZone.Core.Services;

namespace DeskZone.Storage;

public sealed class FileSystemChangeMonitor : IFileSystemChangeMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler<FileSystemChangeEventArgs>? Changed;

    public void UpdatePaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var directory = FindExistingWatchDirectory(path);
            if (directory is not null)
            {
                directories.Add(directory);
            }
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var staleDirectory in _watchers.Keys.Where(path => !directories.Contains(path)).ToArray())
            {
                _watchers[staleDirectory].Dispose();
                _watchers.Remove(staleDirectory);
            }

            foreach (var directory in directories.Where(path => !_watchers.ContainsKey(path)))
            {
                _watchers[directory] = CreateWatcher(directory);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            Changed = null;
        }
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Publish(new[] { e.FullPath });

    private void OnRenamed(object sender, RenamedEventArgs e) =>
        Publish(new[] { e.OldFullPath, e.FullPath });

    private void OnError(object sender, ErrorEventArgs e) =>
        Publish(Array.Empty<string>());

    private void Publish(IEnumerable<string> paths)
    {
        EventHandler<FileSystemChangeEventArgs>? handler;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            handler = Changed;
        }

        try
        {
            handler?.Invoke(
                this,
                new FileSystemChangeEventArgs(
                    paths
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
        }
        catch
        {
            // A subscriber must not terminate the FileSystemWatcher callback thread.
        }
    }

    private static string? FindExistingWatchDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var candidate = Directory.Exists(fullPath)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath))
            : Path.GetDirectoryName(fullPath);
        candidate ??= Path.GetPathRoot(fullPath);

        while (!string.IsNullOrWhiteSpace(candidate))
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            var parent = Directory.GetParent(candidate)?.FullName;
            if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            candidate = parent;
        }

        return null;
    }
}
