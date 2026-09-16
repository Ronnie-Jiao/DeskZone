namespace DeskZone.Core.Services;

public interface IDesktopHostService
{
    bool TryAttach(IntPtr windowHandle);
    bool IsAttached(IntPtr windowHandle);
}
