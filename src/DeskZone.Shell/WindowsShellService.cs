using System.Diagnostics;
using DeskZone.Core.Services;

namespace DeskZone.Shell;

public sealed class WindowsShellService : IShellService
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken = default)
    {
        var argument = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
