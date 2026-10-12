using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Folders;
using Recall.Core.Scanning;
using Recall.Core.Search;
using Recall.Core.Storage;

namespace Recall.Desktop;

public partial class MainWindow : Window
{
    private const int MaximumTextFileBytes = 5 * 1024 * 1024;
    private FolderConfigurationStore? _folderStore;
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private CancellationTokenSource? _scanCancellation;
    private FileMetadataStore? _metadataStore;
    private bool _databaseReady;
    private long _storedFileCount;
    private long _textFileCount;
    private CancellationTokenSource? _searchCancellation;
    private bool _copyingPath;
    private bool _openingFile;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += MainWindow_OnOpened;
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();
            CancelSearch();
        };
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
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        this.FindControl<TextBlock>("IndexStatusText")!.Text = "Database: Loading";
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Loading saved file metadata...";
        try
        {
            var databasePath = RecallDataPath.GetDatabasePath(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _metadataStore = new FileMetadataStore(databasePath);
            await _metadataStore.InitializeAsync();
            _storedFileCount = await _metadataStore.GetFileCountAsync();
            _textFileCount = await _metadataStore.GetTextFileCountAsync();
            _databaseReady = true;
            if (IsVisible)
            {
                UpdateFolderStatus();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not open the metadata database: {0}", exception);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Could not open the metadata database";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Check {_metadataStore?.DatabasePath ?? "your application data directory"} and restart Recall. Your folder selection is still available.";
                this.FindControl<TextBlock>("StoredFileCountText")!.Text = "Stored files: Unavailable";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Database: Error";
            }
        }
    }

    private async void IndexedFolders_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_folderStore is null || _scanCancellation is not null)
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
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = hasFolders && _databaseReady;
        this.FindControl<TextBlock>("FolderCountText")!.Text = $"Folders: {_folders.Count}";
        if (!_databaseReady)
        {
            return;
        }

        this.FindControl<TextBlock>("StoredFileCountText")!.Text = $"Stored files: {_storedFileCount:N0}";
        this.FindControl<TextBlock>("IndexStatusText")!.Text = hasFolders
            ? $"Text files: {_textFileCount:N0}"
            : "Index: Not configured";
        ResetSearchView(hasFolders
            ? (_storedFileCount > 0 ? "Ready to search" : "No metadata saved yet")
            : "No folders selected", hasFolders
            ? (_storedFileCount > 0
                ? "Enter words you remember, then click Find or press Enter.\nOnly your currently selected folders are searched."
                : "Choose Index → Scan Folders to save metadata and extract plain-text files (maximum 5 MiB each).")
            : "Choose Index → Indexed Folders to add a folder.");
        UpdateFindButton();
    }

    private async void ScanFolders_OnClick(object? sender, RoutedEventArgs e)
    {
        var metadataStore = _metadataStore;
        if (_scanCancellation is not null || _folders.Count == 0 || !_databaseReady || metadataStore is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        ResetSearchView("Scanning selected folders...", "You can choose Index → Cancel Scan.");
        SetScanningControls(true);
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Scanning selected folders...";
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
            "You can keep using Recall or choose Index → Cancel Scan.";
        this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Starting";

        var savingMetadata = false;
        var progress = new Progress<ScanProgress>(value =>
        {
            // Ignore updates queued by a completed, cancelled, or previous scan.
            if (!ReferenceEquals(_scanCancellation, cancellation)
                || cancellation.IsCancellationRequested || savingMetadata || !IsVisible)
            {
                return;
            }

            this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Scan: {value.FileCount:N0} files";
        });

        try
        {
            var result = await new FileScanner().ScanAsync(_folders, progress, cancellation.Token);
            savingMetadata = true;
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Extracting and saving plain-text files...";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Choose Index → Cancel Scan to stop without saving this scan.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Text: 0 / {result.Files.Count:N0} files";
            }

            var textProgress = new Progress<TextIndexProgress>(value =>
            {
                if (ReferenceEquals(_scanCancellation, cancellation)
                    && !cancellation.IsCancellationRequested && IsVisible)
                {
                    this.FindControl<TextBlock>("IndexStatusText")!.Text =
                        $"Text: {value.ProcessedFiles:N0} / {result.Files.Count:N0} files";
                }
            });
            var saved = await metadataStore.IndexScanAsync(result.Files,
                new PlainTextExtractor(MaximumTextFileBytes), textProgress, cancellation.Token);
            _storedFileCount = saved.StoredFiles;
            _textFileCount = saved.StoredTextFiles;
            if (IsVisible)
            {
                this.FindControl<TextBlock>("StoredFileCountText")!.Text = $"Stored files: {_storedFileCount:N0}";
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = $"Scan saved: {result.Files.Count:N0} files";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Text ready: {saved.ExtractedFiles:N0} files (including unchanged files).\n"
                    + FormatSkippedContent(saved)
                    + $"\nScan: {result.Failures.Count:N0} unavailable entries; {result.SkippedLinks:N0} symbolic links skipped."
                    + "\nEnter words you remember, then click Find or press Enter.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Text files: {_textFileCount:N0} (complete)";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Trace.TraceInformation("File scan cancelled.");
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Scan cancelled";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Metadata and text from this scan were not saved. Choose Index → Scan Folders to start again.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Cancelled";
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            Trace.TraceWarning("The metadata database is temporarily locked: {0}", exception.Message);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "The metadata database is busy";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Metadata from this scan was not saved. Try scanning again after other database operations finish.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Database: Busy";
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("File scan failed: {0}", exception);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = savingMetadata
                    ? "Could not save file metadata"
                    : "Could not complete the scan";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Metadata from this scan was not saved. Check the selected folders and database, then try again.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Failed";
            }
        }
        finally
        {
            _scanCancellation = null;
            if (IsVisible)
            {
                SetScanningControls(false);
            }
        }
    }

    private static string FormatSkippedContent(TextIndexResult result)
    {
        var skipped = result.SkippedFiles;
        var unsupported = skipped.GetValueOrDefault(TextExtractionStatus.Unsupported);
        var tooLarge = skipped.GetValueOrDefault(TextExtractionStatus.TooLarge);
        var failed = skipped.Where(pair => pair.Key is not (TextExtractionStatus.Unsupported or TextExtractionStatus.TooLarge))
            .Sum(pair => pair.Value);
        return $"Content skipped: {unsupported:N0} unsupported; {tooLarge:N0} over 5 MiB; {failed:N0} unreadable, changed, binary, or linked.";
    }

    private void CancelScan_OnClick(object? sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        this.FindControl<MenuItem>("CancelScanMenuItem")!.IsEnabled = false;
        this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Cancelling";
    }

    private void SetScanningControls(bool scanning)
    {
        this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = !scanning;
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = !scanning && _folders.Count > 0 && _databaseReady;
        this.FindControl<MenuItem>("CancelScanMenuItem")!.IsEnabled = scanning;
        this.FindControl<TextBox>("SearchBox")!.IsEnabled = !scanning;
        UpdateFindButton();
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void About_OnClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    private async void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox searchBox)
        {
            searchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        // TextChanged can be raised while the XAML is still loading.
        if (!_databaseReady || _scanCancellation is not null)
            return;
        if (_folders.Count == 0)
        {
            UpdateFolderStatus();
            return;
        }
        ResetSearchView("Ready to search", "Click Find or press Enter to search your selected folders.");
        UpdateFindButton();
    }

    private async void Find_OnClick(object? sender, RoutedEventArgs e) => await RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var store = _metadataStore;
        var query = this.FindControl<TextBox>("SearchBox")!.Text ?? "";
        if (!_databaseReady || store is null || _scanCancellation is not null || _folders.Count == 0)
            return;
        if (SearchQuery.BuildFtsExpression(query) is null)
        {
            ResetSearchView("Enter searchable words", "Type a word, number, or part of a sentence.");
            return;
        }

        ResetSearchView("Searching...", "Searching saved filenames and file contents.");
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            // Fetch one extra row so the status can distinguish an exact count from a capped result.
            var results = await store.SearchFoldersAsync(query, _folders, 101, cancellation.Token);
            if (!ReferenceEquals(_searchCancellation, cancellation) || cancellation.IsCancellationRequested || !IsVisible)
                return;
            var rows = results.Take(100).Select(result => new SearchResultRow(result)).ToArray();
            this.FindControl<ListBox>("ResultsList")!.ItemsSource = rows;
            this.FindControl<TextBlock>("ResultCountText")!.Text = results.Count > 100
                ? "100 results shown (more available)"
                : $"{rows.Length:N0} results";
            this.FindControl<StackPanel>("EmptyStatePanel")!.IsVisible = rows.Length == 0;
            if (rows.Length == 0)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "No matching files";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Try different words, or choose Index → Scan Folders if your files have changed.";
            }
            else
            {
                this.FindControl<ListBox>("ResultsList")!.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer query, folder change, scan, or closing window superseded this search.
        }
        catch (Exception exception)
        {
            Trace.TraceError("File search failed: {0}", exception);
            if (ReferenceEquals(_searchCancellation, cancellation) && IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Could not search the index";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Try again. If the problem persists, restart Recall and check the local database.";
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
                _searchCancellation = null;
        }
    }

    private void ResultsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFileActionMenus();
        var preview = this.FindControl<TextBox>("ContentPreview");
        if (preview is null)
            return;
        preview.Text =
            (sender as ListBox)?.SelectedItem is SearchResultRow row
                ? row.FullPath + "\n\n" + row.Snippet
                : "Select a search result to see its matching text.";
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
    }

    private void ResetSearchView(string title, string description)
    {
        CancelSearch();
        this.FindControl<ListBox>("ResultsList")!.ItemsSource = Array.Empty<SearchResultRow>();
        UpdateFileActionMenus();
        this.FindControl<TextBox>("ContentPreview")!.Text = "Select a search result to see its matching text.";
        this.FindControl<TextBlock>("ResultCountText")!.Text = "0 results";
        this.FindControl<StackPanel>("EmptyStatePanel")!.IsVisible = true;
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = title;
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text = description;
    }

    private void UpdateFindButton()
    {
        this.FindControl<Button>("FindButton")!.IsEnabled = _databaseReady && _scanCancellation is null
            && _folders.Count > 0 && !string.IsNullOrWhiteSpace(this.FindControl<TextBox>("SearchBox")!.Text);
    }

    private void UpdateFileActionMenus()
    {
        var hasSelection = this.FindControl<ListBox>("ResultsList")?.SelectedItem is SearchResultRow;
        var menu = this.FindControl<MenuItem>("CopyPathMenuItem");
        if (menu is not null)
            menu.IsEnabled = !_copyingPath && hasSelection;
        var openMenu = this.FindControl<MenuItem>("OpenFileMenuItem");
        if (openMenu is not null)
            openMenu.IsEnabled = !_openingFile && hasSelection;
    }

    private async void CopyPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_copyingPath || this.FindControl<ListBox>("ResultsList")!.SelectedItem is not SearchResultRow row)
            return;
        _copyingPath = true;
        UpdateFileActionMenus();
        try
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(row.FullPath);
            if (IsVisible)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Path copied";
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not copy the file path: {0}", exception);
            if (IsVisible)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Could not copy path. Try again.";
        }
        finally
        {
            _copyingPath = false;
            if (IsVisible)
                UpdateFileActionMenus();
        }
    }

    private async void OpenFile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("ResultsList")!.SelectedItem is SearchResultRow row)
            await OpenFileAsync(row);
    }

    private async void ResultsList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = e.Source as ListBoxItem ?? (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not SearchResultRow row)
            return;
        e.Handled = true;
        await OpenFileAsync(row);
    }

    private async Task OpenFileAsync(SearchResultRow row)
    {
        if (_openingFile)
            return;
        _openingFile = true;
        UpdateFileActionMenus();
        try
        {
            var file = new FileInfo(row.FullPath);
            var exists = await Task.Run(() => file.Exists);
            if (!IsVisible)
                return;
            if (!exists)
            {
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "File unavailable. Check its location.";
                return;
            }
            var launched = await Launcher.LaunchFileInfoAsync(file);
            if (IsVisible && _scanCancellation is null)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = launched
                    ? "File sent to its default application"
                    : "Could not open file. Check its default application.";
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not open the selected file: {0}", exception);
            if (IsVisible && _scanCancellation is null)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Could not open file. Check its location and permissions.";
        }
        finally
        {
            _openingFile = false;
            if (IsVisible)
                UpdateFileActionMenus();
        }
    }
}
