using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private const int SearchDebounceMilliseconds = 300;
    private const int SearchPageSize = 100;
    private const int MaximumTextFileBytes = 5 * 1024 * 1024;
    private const int MaximumDocumentFileBytes = 20 * 1024 * 1024;
    private const int MaximumDocumentTextCharacters = 5 * 1024 * 1024;
    private FolderConfigurationStore? _folderStore;
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private CancellationTokenSource? _scanCancellation;
    private FileMetadataStore? _metadataStore;
    private bool _databaseReady;
    private long _storedFileCount;
    private long _textFileCount;
    private CancellationTokenSource? _searchCancellation;
    private string? _displayedQuery;
    private int _searchPage;
    private bool _hasNextSearchPage;
    private bool _copyingPath;
    private bool _openingFile;
    private bool _openingFolder;
    private CompletedScanReport? _lastCompletedScan;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, MainWindow_OnShortcutKeyDown, RoutingStrategies.Tunnel);
        Opened += MainWindow_OnOpened;
        Closed += async (_, _) =>
        {
            _lifetime.Cancel();
            _scanCancellation?.Cancel();
            CancelSearch();
            CloseResultContextMenu();
            await StopMonitoringAsync();
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
            _folders = await Task.Run(() => _folderStore.LoadAsync(_lifetime.Token));
            await LoadSettingsAsync();
            UpdateFolderStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
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
            await _metadataStore.InitializeAsync(_lifetime.Token);
            _storedFileCount = await _metadataStore.GetFileCountAsync(_lifetime.Token);
            _textFileCount = await _metadataStore.GetTextFileCountAsync(_lifetime.Token);
            _databaseReady = true;
            await RestartMonitoringAsync();
            if (IsVisible)
            {
                UpdateFolderStatus();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
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
        finally
        {
            if (IsVisible)
            {
                this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = true;
                this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = _settingsStore is not null;
            }
        }
        // Reuse manual scanning, including cancellation, incremental text extraction,
        // watcher coordination, and rollback. Existing settings keep this off by default.
        if (IsVisible && _databaseReady && _settings.ScanOnStartup && _folders.Count > 0)
            await ScanFoldersAsync(_folders);
    }

    private async void IndexedFolders_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_folderStore is null || _scanCancellation is not null || _editingSettings)
        {
            return;
        }

        this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = false;
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = false;
        this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = false;
        string? reindexFolder = null;
        try
        {
            // Stop before selection changes so removed folders cannot continue being indexed.
            await StopMonitoringAsync();
            if (!IsVisible)
                return;
            var dialog = new IndexedFoldersWindow(_folderStore, _folders, canReindex: _databaseReady);
            await dialog.ShowDialog(this);
            var folders = dialog.Folders;
            if (!_folders.SequenceEqual(folders, StringComparer.Ordinal))
            {
                _lastCompletedScan = null;
                this.FindControl<MenuItem>("LastCompletedScanMenuItem")!.IsEnabled = false;
            }
            _folders = folders;
            reindexFolder = dialog.RequestedReindexFolder;
            await RestartMonitoringAsync();
            if (IsVisible)
            {
                UpdateFolderStatus();
                ResetSearchView("Folder settings updated", "Choose Index → Scan Folders to reconcile existing files and changes made while folder settings were open.");
            }
        }
        finally
        {
            if (IsVisible)
            {
                this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = true;
                this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = _settingsStore is not null;
            }
        }
        if (reindexFolder is not null && IsVisible)
            await ScanFoldersAsync([reindexFolder], forceTextExtraction: true);
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
        if (hasFolders)
            UpdateMonitoringStatus();
        ResetSearchView(hasFolders
            ? (_storedFileCount > 0 ? "Ready to search" : "No metadata saved yet")
            : "No folders selected", hasFolders
            ? (_storedFileCount > 0
                ? "Enter words you remember; results appear when you pause typing.\nOnly your currently selected folders are searched.\nUse Scan Folders to catch changes made while Recall was closed."
                : "Choose Index → Scan Folders to index text files (up to 5 MiB), text-layer PDFs, DOCX documents and IPYNB notebooks (up to 20 MiB).")
            : "Choose Index → Indexed Folders to add a folder.");
        UpdateSearchButtons();
    }

    private async void ScanFolders_OnClick(object? sender, RoutedEventArgs e) => await ScanFoldersAsync(_folders);

    private async Task ScanFoldersAsync(IReadOnlyList<string> scanFolders, bool forceTextExtraction = false)
    {
        var metadataStore = _metadataStore;
        var roots = scanFolders.ToArray();
        if (_scanCancellation is not null || _editingSettings || roots.Length == 0 || !_databaseReady || metadataStore is null
            || roots.Any(root => !_folders.Contains(root, StringComparer.Ordinal)))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var operation = forceTextExtraction ? "Reindex" : "Scan";
        _scanCancellation = cancellation;
        ResetSearchView(forceTextExtraction ? "Reindexing selected folder..." : "Scanning selected folders...",
            "You can choose Index → Cancel Scan.");
        SetScanningControls(true);
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
            forceTextExtraction
                ? $"{roots[0]}\nAll supported file contents will be reread. Choose Index → Cancel Scan to stop."
                : "You can choose Index → Cancel Scan.";
        this.FindControl<TextBlock>("IndexStatusText")!.Text = $"{operation}: Starting";

        var savingMetadata = false;
        var scanSaved = false;
        var progress = new Progress<ScanProgress>(value =>
        {
            // Ignore updates queued by a completed, cancelled, or previous scan.
            if (!ReferenceEquals(_scanCancellation, cancellation)
                || cancellation.IsCancellationRequested || savingMetadata || !IsVisible)
            {
                return;
            }

            this.FindControl<TextBlock>("IndexStatusText")!.Text = $"{operation}: {value.FileCount:N0} files";
        });

        try
        {
            using var automaticPause = await PauseAutomaticUpdatesAsync(cancellation.Token);
            var result = await new FileScanner().ScanAsync(roots, progress, cancellation.Token,
                excludedFolders: IndexExclusions());
            savingMetadata = true;
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Extracting and saving file text...";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Choose Index → Cancel Scan to stop without saving this {operation.ToLowerInvariant()}.";
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
            var saved = await metadataStore.SynchronizeScanAsync(result, roots,
                new FileTextExtractor(MaximumTextFileBytes, MaximumDocumentFileBytes, MaximumDocumentTextCharacters),
                textProgress, cancellation.Token, forceTextExtraction);
            _storedFileCount = saved.StoredFiles;
            _textFileCount = saved.StoredTextFiles;
            scanSaved = true;
            var summary = (forceTextExtraction
                    ? $"Text reread: {saved.ExtractedFiles:N0} files.\n"
                    : $"Text ready: {saved.ExtractedFiles:N0} files (including unchanged files).\n")
                + FormatSkippedContent(saved)
                + $"\nStale index entries removed: {saved.RemovedFiles:N0}; cleanup skipped for {saved.CleanupSkippedFolders:N0} folders with scan errors."
                + $"\nScan: {result.Failures.Count:N0} unavailable entries; {result.SkippedLinks:N0} symbolic links skipped.";
            _lastCompletedScan = new(DateTime.UtcNow, operation, roots.ToArray(), result.Files.Count,
                summary, result.Failures.Take(CompletedScanReport.MaximumScannerDetails).ToArray(),
                saved.SkippedFileDetails ?? []);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("StoredFileCountText")!.Text = $"Stored files: {_storedFileCount:N0}";
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = $"{operation} saved: {result.Files.Count:N0} files";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    summary + "\nEnter words you remember; results appear when you pause typing."
                    + "\nChoose Index → Last Completed Scan to review the details.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Text files: {_textFileCount:N0} (complete)";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Trace.TraceInformation("File {0} cancelled.", operation.ToLowerInvariant());
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = $"{operation} cancelled";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Metadata and text from this {operation.ToLowerInvariant()} were not saved. "
                    + (forceTextExtraction
                        ? "Choose Index → Indexed Folders → Reindex Folder to start again."
                        : "Choose Index → Scan Folders to start again.");
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"{operation}: Cancelled";
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            Trace.TraceWarning("The metadata database is temporarily locked: {0}", exception.Message);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "The metadata database is busy";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Metadata from this {operation.ToLowerInvariant()} was not saved. Try again after other database operations finish.";
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
                    : $"Could not complete the {operation.ToLowerInvariant()}";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"Metadata from this {operation.ToLowerInvariant()} was not saved. Check the selected folders and database, then try again.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"{operation}: Failed";
            }
        }
        finally
        {
            _scanCancellation = null;
            if (IsVisible)
            {
                SetScanningControls(false);
                if (scanSaved || _monitor?.FailureReason is not null || _monitorProblem is not null)
                    UpdateMonitoringStatus();
            }
        }
    }

    private static string FormatSkippedContent(TextIndexResult result)
    {
        var skipped = result.SkippedFiles;
        var unsupported = skipped.GetValueOrDefault(TextExtractionStatus.Unsupported);
        var tooLarge = skipped.GetValueOrDefault(TextExtractionStatus.TooLarge);
        var noText = skipped.GetValueOrDefault(TextExtractionStatus.NoSearchableText);
        var failed = skipped.Where(pair => pair.Key is not (TextExtractionStatus.Unsupported or TextExtractionStatus.TooLarge or TextExtractionStatus.NoSearchableText))
            .Sum(pair => pair.Value);
        return $"Content skipped: {unsupported:N0} unsupported; {tooLarge:N0} over size limits; {noText:N0} documents without text; {failed:N0} unreadable, changed, or invalid.";
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
        this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = !scanning && _settingsStore is not null;
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = !scanning && _folders.Count > 0 && _databaseReady;
        this.FindControl<MenuItem>("CancelScanMenuItem")!.IsEnabled = scanning;
        this.FindControl<MenuItem>("LastCompletedScanMenuItem")!.IsEnabled = !scanning && _lastCompletedScan is not null;
        this.FindControl<TextBox>("SearchBox")!.IsEnabled = !scanning;
        UpdateSearchButtons();
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void About_OnClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    private async void LastCompletedScan_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null && _lastCompletedScan is { } report)
            await new ScanReportWindow(report).ShowDialog(this);
    }

    private async void SearchHelp_OnClick(object? sender, RoutedEventArgs e) => await ShowSearchHelpAsync();

    private Task ShowSearchHelpAsync()
    {
        CloseResultContextMenu();
        return new SearchHelpWindow().ShowDialog(this);
    }

    private async void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = FocusSearchResults();
        }
    }

    private async void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateSearchButtons();
        // TextChanged can be raised while the XAML is still loading.
        if (!_databaseReady || _scanCancellation is not null)
            return;
        if (_folders.Count == 0)
        {
            UpdateFolderStatus();
            return;
        }
        await RunSearchAsync(debounce: true);
    }

    private async void Find_OnClick(object? sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void PreviousPage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_searchPage > 0 && _searchCancellation is null && _scanCancellation is null)
            await RunSearchAsync(page: _searchPage - 1);
    }

    private async void NextPage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_hasNextSearchPage && _searchCancellation is null && _scanCancellation is null)
            await RunSearchAsync(page: _searchPage + 1);
    }

    private void ClearSearch_OnClick(object? sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        if (_scanCancellation is not null)
            return;
        // Cancel immediately, including a pending debounce, before TextChanged is delivered.
        CancelSearch();
        var searchBox = this.FindControl<TextBox>("SearchBox")!;
        searchBox.Clear();
        searchBox.Focus();
    }

    private async Task RunSearchAsync(bool debounce = false, bool refresh = false, int page = 0)
    {
        var store = _metadataStore;
        var query = this.FindControl<TextBox>("SearchBox")!.Text ?? "";
        if (!_databaseReady || store is null || _scanCancellation is not null || _folders.Count == 0 || !IsVisible)
            return;
        if (string.IsNullOrWhiteSpace(query))
        {
            ResetSearchView("Ready to search", "Enter words you remember; results appear when you pause typing.");
            return;
        }
        if (SearchQuery.BuildFtsExpression(query) is null)
        {
            ResetSearchView("Enter searchable words", "Type a word, number, or part of a sentence.");
            return;
        }

        // A watcher may interrupt a page request; retry that page rather than jumping to page 1.
        var requestedPage = refresh ? _searchPage : page;
        var keepResults = refresh && string.Equals(query, _displayedQuery, StringComparison.Ordinal);
        if (keepResults)
        {
            CloseResultContextMenu();
            CancelSearch();
        }
        else
        {
            ResetSearchView(debounce ? "Waiting for typing..." : "Searching...",
                debounce ? "Results appear when you pause. Find or Enter searches immediately."
                    : "Searching saved filenames and file contents.");
        }
        using var cancellation = new CancellationTokenSource();
        _searchPage = requestedPage;
        _searchCancellation = cancellation;
        UpdateSearchPagination();
        try
        {
            // The delay and database query share cancellation: typing, clearing, scanning,
            // changing folders, or closing must also cancel a search that has not started yet.
            if (debounce)
                await Task.Delay(SearchDebounceMilliseconds, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_searchCancellation, cancellation) || !IsVisible)
                return;
            this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Searching...";
            this.FindControl<TextBlock>("EmptyStateDescription")!.Text = "Searching saved filenames and file contents.";
            // Fetch one extra row to detect a next page without counting all matching documents.
            var offset = checked(requestedPage * SearchPageSize);
            var results = await store.SearchFoldersAsync(query, _folders, SearchPageSize + 1,
                cancellation.Token, offset: offset);
            if (!ReferenceEquals(_searchCancellation, cancellation) || cancellation.IsCancellationRequested || !IsVisible)
                return;
            var rows = results.Take(SearchPageSize).Select(result => new SearchResultRow(result)).ToArray();
            var list = this.FindControl<ListBox>("ResultsList")!;
            // Read the current selection after the query: the user may have navigated
            // to a different file while this background refresh was running.
            var selectedPath = keepResults ? (list.SelectedItem as SearchResultRow)?.FullPath : null;
            var restoreFocus = keepResults && IsActive && list.IsKeyboardFocusWithin;
            list.ItemsSource = rows;
            _displayedQuery = query;
            _hasNextSearchPage = results.Count > SearchPageSize;
            this.FindControl<TextBlock>("ResultCountText")!.Text = rows.Length == 0
                ? "0 results on this page"
                : $"{offset + 1:N0}–{(long)offset + rows.Length:N0} shown"
                    + (_hasNextSearchPage ? " (more available)" : "");
            this.FindControl<StackPanel>("EmptyStatePanel")!.IsVisible = rows.Length == 0;
            if (rows.Length == 0)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = requestedPage > 0
                    ? "No results on this page" : "No matching files";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text = requestedPage > 0
                    ? "The index may have changed. Use Previous, or Find to return to page 1."
                    : "Try different words, or choose Index → Scan Folders if your files have changed.";
            }
            if (keepResults)
            {
                // Do not silently select another file if the chosen file disappeared
                // or no longer appears among the matching results.
                list.SelectedIndex = Array.FindIndex(rows,
                    row => string.Equals(row.FullPath, selectedPath, StringComparison.Ordinal));
                if (list.SelectedIndex >= 0)
                    list.ScrollIntoView(list.SelectedIndex);
                if (restoreFocus)
                {
                    if (list.SelectedIndex >= 0)
                        FocusSearchResults();
                    else
                        list.Focus();
                }
            }
            else if (rows.Length > 0)
                list.SelectedIndex = 0;
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
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = keepResults
                    ? "Could not refresh search results" : "Could not search the index";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "Try again. If the problem persists, restart Recall and check the local database.";
                if (keepResults)
                    this.FindControl<TextBlock>("IndexStatusText")!.Text = "Results could not be refreshed. Press Find to retry.";
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                UpdateSearchPagination();
            }
        }
    }

    private void ResultsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFileActionMenus();
        UpdateContentPreview((sender as ListBox)?.SelectedItem as SearchResultRow);
    }

    private void UpdateContentPreview(SearchResultRow? row)
    {
        var preview = this.FindControl<SelectableTextBlock>("ContentPreview");
        if (preview is null)
            return;
        preview.SelectionStart = preview.SelectionEnd = 0;
        preview.Inlines!.Clear();
        preview.Text = null;
        this.FindControl<ScrollViewer>("ContentPreviewScroll")!.Offset = default;
        if (row is null)
        {
            preview.Text = "Select a search result to see its matching text.";
            return;
        }
        preview.Inlines.Add(new Run(row.FullPath + "\n\n"));
        foreach (var part in row.SnippetParts)
        {
            // Add document content as text, never as XAML or HTML markup.
            var run = new Run(part.Text);
            if (part.IsMatch)
            {
                run.Background = new SolidColorBrush(Color.Parse("#FFF2A8"));
                run.Foreground = Brushes.Black;
                run.FontWeight = FontWeight.Bold;
            }
            preview.Inlines.Add(run);
        }
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
    }

    private void ResetSearchView(string title, string description)
    {
        CloseResultContextMenu();
        CancelSearch();
        _displayedQuery = null;
        _searchPage = 0;
        _hasNextSearchPage = false;
        UpdateSearchPagination();
        this.FindControl<ListBox>("ResultsList")!.ItemsSource = Array.Empty<SearchResultRow>();
        UpdateFileActionMenus();
        UpdateContentPreview(null);
        this.FindControl<TextBlock>("ResultCountText")!.Text = "0 results";
        this.FindControl<StackPanel>("EmptyStatePanel")!.IsVisible = true;
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = title;
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text = description;
    }

    private void UpdateSearchPagination()
    {
        var ready = _databaseReady && _scanCancellation is null && _searchCancellation is null
            && _folders.Count > 0 && IsVisible;
        this.FindControl<Button>("PreviousPageButton")!.IsEnabled = ready && _searchPage > 0;
        this.FindControl<Button>("NextPageButton")!.IsEnabled = ready && _hasNextSearchPage;
        this.FindControl<TextBlock>("SearchPageText")!.Text = $"Page {_searchPage + 1:N0}";
    }

    private void UpdateSearchButtons()
    {
        // Controls can still be missing when TextChanged fires during XAML loading.
        var query = this.FindControl<TextBox>("SearchBox")?.Text;
        if (this.FindControl<Button>("FindButton") is { } find)
            find.IsEnabled = _databaseReady && _scanCancellation is null
                && _folders.Count > 0 && !string.IsNullOrWhiteSpace(query);
        if (this.FindControl<Button>("ClearSearchButton") is { } clear)
            clear.IsEnabled = _scanCancellation is null && !string.IsNullOrEmpty(query);
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
        var folderMenu = this.FindControl<MenuItem>("OpenFolderMenuItem");
        if (folderMenu is not null)
            folderMenu.IsEnabled = !_openingFolder && hasSelection;
    }

    private async void CopyPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("ResultsList")!.SelectedItem is SearchResultRow row)
            await CopyPathAsync(row);
    }

    private async Task CopyPathAsync(SearchResultRow row)
    {
        if (_copyingPath)
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

    private async void OpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("ResultsList")!.SelectedItem is SearchResultRow row)
            await OpenFolderAsync(row);
    }

    private async Task OpenFolderAsync(SearchResultRow row)
    {
        if (_openingFolder)
            return;
        _openingFolder = true;
        UpdateFileActionMenus();
        try
        {
            var directory = new FileInfo(row.FullPath).Directory;
            var exists = await Task.Run(() => directory?.Exists == true);
            if (!IsVisible)
                return;
            if (!exists)
            {
                if (_scanCancellation is null)
                    this.FindControl<TextBlock>("IndexStatusText")!.Text = "Folder unavailable. Check its location.";
                return;
            }
            var launched = await Launcher.LaunchDirectoryInfoAsync(directory!);
            if (IsVisible && _scanCancellation is null)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = launched
                    ? "Folder sent to its default file manager"
                    : "Could not open folder. Check your file manager.";
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not open the containing folder: {0}", exception);
            if (IsVisible && _scanCancellation is null)
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Could not open folder. Check its location and permissions.";
        }
        finally
        {
            _openingFolder = false;
            if (IsVisible)
                UpdateFileActionMenus();
        }
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
