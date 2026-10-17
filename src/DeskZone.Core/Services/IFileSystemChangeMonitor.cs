namespace DeskZone.Core.Services;

public sealed class FileSystemChangeEventArgs : EventArgs
{
    public FileSystemChangeEventArgs(
        IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string>? deletedPaths = null)
    {
        Paths = paths;
        DeletedPaths = deletedPaths ?? Array.Empty<string>();
    }

    public IReadOnlyCollection<string> Paths { get; }
    public IReadOnlyCollection<string> DeletedPaths { get; }
}

public interface IFileSystemChangeMonitor : IDisposable
{
    event EventHandler<FileSystemChangeEventArgs>? Changed;

    void UpdatePaths(IEnumerable<string> paths);
}
