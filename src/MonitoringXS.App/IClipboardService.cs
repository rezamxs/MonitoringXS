using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;

namespace MonitoringXS.App;

public interface IClipboardService
{
    ValueTask<bool> CopyTextAsync(string text, CancellationToken cancellationToken);
}

internal sealed class WindowsClipboardService : IClipboardService
{
    public ValueTask<bool> CopyTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DataPackage content = new();
            content.SetText(text);
            Clipboard.SetContent(content);
            Clipboard.Flush();
            return ValueTask.FromResult(true);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or COMException)
        {
            return ValueTask.FromResult(false);
        }
    }
}
