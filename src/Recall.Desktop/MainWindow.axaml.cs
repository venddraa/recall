using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Recall.Core.Folders;
using Recall.Core.Scanning;

namespace Recall.Desktop;

public partial class MainWindow : Window
{
    private FolderConfigurationStore? _folderStore;
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private CancellationTokenSource? _scanCancellation;

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
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = hasFolders;
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

    private async void ScanFolders_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null || _folders.Count == 0)
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

        var progress = new Progress<ScanProgress>(value =>
        {
            // Ignore updates queued by a completed, cancelled, or previous scan.
            if (!ReferenceEquals(_scanCancellation, cancellation)
                || cancellation.IsCancellationRequested || !IsVisible)
            {
                return;
            }

            this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Scan: {value.FileCount:N0} files";
        });

        try
        {
            var result = await new FileScanner().ScanAsync(_folders, progress, cancellation.Token);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = $"Scan completed: {result.Files.Count:N0} files";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    $"{result.Failures.Count:N0} unavailable entries; {result.SkippedLinks:N0} symbolic links skipped.\nNo file contents have been indexed yet.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = $"Scan: {result.Files.Count:N0} files (complete)";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Trace.TraceInformation("File scan cancelled.");
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Scan cancelled";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "No files were changed. Choose Index → Scan Folders to start again.";
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Cancelled";
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("File scan failed: {0}", exception);
            if (IsVisible)
            {
                this.FindControl<TextBlock>("EmptyStateTitle")!.Text = "Could not complete the scan";
                this.FindControl<TextBlock>("EmptyStateDescription")!.Text =
                    "No files were changed. Check the selected folders and try again.";
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

    private void CancelScan_OnClick(object? sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        this.FindControl<MenuItem>("CancelScanMenuItem")!.IsEnabled = false;
        this.FindControl<TextBlock>("IndexStatusText")!.Text = "Scan: Cancelling";
    }

    private void SetScanningControls(bool scanning)
    {
        this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = !scanning;
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = !scanning && _folders.Count > 0;
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
