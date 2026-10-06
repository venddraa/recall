using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Recall.Core.Folders;

namespace Recall.Desktop;

public partial class IndexedFoldersWindow : Window
{
    private FolderConfigurationStore? _store;
    private readonly ObservableCollection<string> _folders = new();
    private bool _busy;

    public IndexedFoldersWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("FoldersList")!.ItemsSource = _folders;
        Closing += (_, e) => e.Cancel = _busy;
        RefreshControls();
    }

    public IndexedFoldersWindow(FolderConfigurationStore store, IReadOnlyList<string> folders) : this()
    {
        _store = store;
        foreach (var folder in folders)
        {
            _folders.Add(folder);
        }

        RefreshControls();
    }

    public IReadOnlyList<string> Folders => _folders.ToArray();

    private async void AddFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var store = _store;
        if (_busy || store is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (!StorageProvider.CanPickFolder)
            {
                ShowMessage("A folder picker is unavailable in this desktop session.", isError: true);
                return;
            }

            var selection = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder for Recall",
                AllowMultiple = false
            });
            if (selection.Count == 0)
            {
                return;
            }

            string? localPath;
            using (var selectedFolder = selection[0])
            {
                localPath = selectedFolder.TryGetLocalPath();
            }

            if (localPath is null)
            {
                ShowMessage("Choose a local folder that Recall can access.", isError: true);
                return;
            }

            var path = FolderPath.Normalize(localPath);
            if (_folders.Contains(path, StringComparer.Ordinal))
            {
                this.FindControl<ListBox>("FoldersList")!.SelectedItem = path;
                ShowMessage("This folder is already selected.");
                return;
            }

            if (!await Task.Run(() => Directory.Exists(path)))
            {
                ShowMessage("The folder is unavailable or cannot be accessed.", isError: true);
                return;
            }

            var updatedFolders = _folders.Append(path).ToArray();
            ShowMessage("Saving folder selection...");
            await Task.Run(() => store.SaveAsync(updatedFolders));
            _folders.Add(path);
            this.FindControl<ListBox>("FoldersList")!.SelectedItem = path;
            ShowMessage("Folder added. No files have been indexed yet.");
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not add an indexed folder: {0}", exception);
            ShowMessage("Could not add the folder. The saved selection has not changed.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var store = _store;
        if (_busy || store is null || this.FindControl<ListBox>("FoldersList")!.SelectedItem is not string path)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var updatedFolders = _folders.Where(folder => !StringComparer.Ordinal.Equals(folder, path)).ToArray();
            ShowMessage("Saving folder selection...");
            await Task.Run(() => store.SaveAsync(updatedFolders));
            _folders.Remove(path);
            ShowMessage("Folder removed from Recall. Its files were left untouched.");
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not remove an indexed folder: {0}", exception);
            ShowMessage("Could not remove the folder. The saved selection has not changed.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FoldersList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshControls();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshControls();
    }

    private void RefreshControls()
    {
        this.FindControl<Button>("AddFolderButton")!.IsEnabled = !_busy && _store is not null;
        this.FindControl<Button>("RemoveFolderButton")!.IsEnabled = !_busy && _store is not null
            && this.FindControl<ListBox>("FoldersList")!.SelectedItem is string;
        this.FindControl<Button>("CloseButton")!.IsEnabled = !_busy;
        this.FindControl<TextBlock>("EmptyFoldersText")!.IsVisible = _folders.Count == 0;
    }

    private void ShowMessage(string text, bool isError = false)
    {
        var message = this.FindControl<TextBlock>("MessageText")!;
        message.Text = text;
        message.Foreground = isError ? Brushes.DarkRed : Brushes.Black;
        message.IsVisible = true;
    }
}
