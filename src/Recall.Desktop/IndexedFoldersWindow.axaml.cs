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
    private bool _canIndex;

    public IndexedFoldersWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("FoldersList")!.ItemsSource = _folders;
        Closing += (_, e) => e.Cancel = _busy;
        RefreshControls();
    }

    public IndexedFoldersWindow(FolderConfigurationStore store, IReadOnlyList<string> folders,
        bool canIndex = false) : this()
    {
        _store = store;
        _canIndex = canIndex;
        foreach (var folder in folders)
        {
            _folders.Add(folder);
        }

        RefreshControls();
    }

    public IReadOnlyList<string> Folders => _folders.ToArray();
    public string? RequestedScanFolder { get; private set; }
    public bool ForceTextExtraction { get; private set; }

    private void ScanFolder_OnClick(object? sender, RoutedEventArgs e) => RequestScan(forceTextExtraction: false);

    private void ReindexFolder_OnClick(object? sender, RoutedEventArgs e) => RequestScan(forceTextExtraction: true);

    private void RequestScan(bool forceTextExtraction)
    {
        if (_busy || !_canIndex || this.FindControl<ListBox>("FoldersList")!.SelectedItem is not string folder)
            return;
        RequestedScanFolder = folder;
        ForceTextExtraction = forceTextExtraction;
        Close();
    }

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
            ShowMessage("Folder added. Choose Scan Folder to index it now.");
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
        var canIndexSelection = !_busy && _canIndex
            && this.FindControl<ListBox>("FoldersList")!.SelectedItem is string;
        this.FindControl<Button>("ScanFolderButton")!.IsEnabled = canIndexSelection;
        this.FindControl<Button>("ReindexFolderButton")!.IsEnabled = canIndexSelection;
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
