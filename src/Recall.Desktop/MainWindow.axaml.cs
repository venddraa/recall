using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Microsoft.Data.Sqlite;
using Recall.Core.Extraction;
using Recall.Core.Folders;
using Recall.Core.Scanning;
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

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += MainWindow_OnOpened;
        Closed += (_, _) => _scanCancellation?.Cancel();
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
        this.FindControl<TextBlock>("EmptyStateTitle")!.Text = hasFolders
            ? (_storedFileCount > 0 ? "File metadata is saved" : "No metadata saved yet")
            : "No folders selected";
        this.FindControl<TextBlock>("EmptyStateDescription")!.Text = hasFolders
            ? (_storedFileCount > 0
                ? $"{_storedFileCount:N0} files stored; {_textFileCount:N0} with extracted text. Content search is not available yet.\nChoose Index → Scan Folders to extract plain-text files (maximum 5 MiB each)."
                : "Choose Index → Scan Folders to save metadata and extract plain-text files (maximum 5 MiB each).")
            : "Choose Index → Indexed Folders to add a folder.";
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
                    "You can cancel before the database transaction completes.";
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
                    + "\nContent search is not available yet.";
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
