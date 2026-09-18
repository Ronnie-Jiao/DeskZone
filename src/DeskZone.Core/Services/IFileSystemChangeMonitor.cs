namespace DeskZone.Core.Services;

public sealed class FileSystemChangeEventArgs : EventArgs
{
    public FileSystemChangeEventArgs(IReadOnlyCollection<string> paths)
    {
        Paths = paths;
    }

    public IReadOnlyCollection<string> Paths { get; }
}

public interface IFileSystemChangeMonitor : IDisposable
{
    event EventHandler<FileSystemChangeEventArgs>? Changed;

    void UpdatePaths(IEnumerable<string> paths);
}
