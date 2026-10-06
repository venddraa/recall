using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Recall.Core.Folders;

namespace Recall.Desktop;

public partial class MainWindow : Window
{
    private FolderConfigurationStore? _folderStore;
    private IReadOnlyList<string> _folders = Array.Empty<string>();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += MainWindow_OnOpened;
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        this.FindControl<TextBox>("SearchBox")?.Focus();
        try
        {
            var configPath = FolderPath.GetConfigurationFilePath(
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _folderStore = new FolderConfigurationStore(configPath);
            _folders = await Task.Run(() => _folderStore.LoadAsync());
            UpdateFolderStatus();
            this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = true;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not load folder configuration: {0}", exception);
            this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Could not load folder settings";
            this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                $"Check {_folderStore?.FilePath ?? "your configuration directory"} and restart Recall. The configuration has been left unchanged.";
            this.FindControl<TextBlock>("IndexStatusText")!.Text = "Index: Configuration error";
        }
    }

    private async void IndexedFolders_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_folderStore is null)
        {
            return;
        }

        var dialog = new IndexedFoldersWindow(_folderStore, _folders);
        await dialog.ShowDialog(this);
        _folders = dialog.Folders;
        UpdateFolderStatus();
    }

    private void UpdateFolderStatus()
    {
        var hasFolders = _folders.Count > 0;
        this.FindControl<TextBlock>("FolderCountText")!.Text = $"Folders: {_folders.Count}";
        this.FindControl<TextBlock>("IndexStatusText")!.Text = hasFolders
            ? "Index: Not indexed"
            : "Index: Not configured";
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = hasFolders
            ? "No indexed files yet"
            : "No folders selected";
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text = hasFolders
            ? "Your selected folders are saved. No files have been indexed."
            : "Choose Index → Indexed Folders to add a folder.";
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void About_OnClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    private void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox searchBox)
        {
            searchBox.Clear();
            e.Handled = true;
        }
    }
}
