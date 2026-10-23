using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Recall.Core.Storage;

namespace Recall.Desktop;

public partial class MainWindow
{
    private async void OpenLogFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = this.FindControl<MenuItem>("OpenLogFolderMenuItem")!;
        if (!menu.IsEnabled)
            return;
        menu.IsEnabled = false;
        var message = "Could not open the local log folder.";
        try
        {
            var path = RecallDataPath.GetLogFilePath(Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var exists = await Task.Run(() => File.Exists(path));
            if (!IsVisible)
                return;
            message = exists
                ? await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(path)!))
                    ? "Local log folder opened" : "Could not open the log folder. Check your file manager."
                : "Local log file unavailable. Check application data directory permissions.";
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not open the local log folder: {0}", exception);
        }
        finally
        {
            if (IsVisible)
            {
                menu.IsEnabled = true;
                if (_scanCancellation is null)
                    this.FindControl<TextBlock>("IndexStatusText")!.Text = message;
            }
        }
    }
}
