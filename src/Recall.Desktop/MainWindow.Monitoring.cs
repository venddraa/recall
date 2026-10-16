using System.Diagnostics;
using Avalonia.Controls;
using Recall.Core.Extraction;
using Recall.Core.Monitoring;

namespace Recall.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private FolderMonitor? _monitor;
    private string? _monitorProblem;

    private string[] IndexExclusions() => new[]
    {
        Path.GetDirectoryName(_metadataStore!.DatabasePath)!,
        Path.GetDirectoryName(_folderStore!.FilePath)!
    };

    private async Task StopMonitoringAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is null)
            return;
        try
        {
            await monitor.DisposeAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not stop folder monitoring: {0}", exception.GetType().Name);
        }
    }

    private async Task RestartMonitoringAsync()
    {
        await StopMonitoringAsync();
        _monitorProblem = null;
        if (!_settings.AutomaticUpdates || !_databaseReady || _folders.Count == 0 || !IsVisible || _lifetime.IsCancellationRequested)
            return;
        FolderMonitor? monitor = null;
        var progress = new Progress<FolderMonitorUpdate>(async update =>
        {
            if (monitor is null || !ReferenceEquals(_monitor, monitor) || !IsVisible || _scanCancellation is not null)
                return;
            if (update.Error is not null)
            {
                _monitorProblem = update.Error;
                UpdateMonitoringStatus();
                return;
            }
            if (update.Updating)
            {
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Index: Updating";
                return;
            }
            if (update.Result is not { } result)
                return;
            _storedFileCount = result.StoredFiles;
            _textFileCount = result.StoredTextFiles;
            this.FindControl<TextBlock>("StoredFileCountText")!.Text = $"Stored files: {_storedFileCount:N0}";
            UpdateMonitoringStatus();
            if (result.CleanupSkippedFolders > 0 || result.SkippedFiles.Keys.Any(status => status is
                TextExtractionStatus.Changed or TextExtractionStatus.Unavailable or TextExtractionStatus.ParserFailed
                or TextExtractionStatus.InvalidEncoding or TextExtractionStatus.Binary))
                this.FindControl<TextBlock>("IndexStatusText")!.Text = "Some updates skipped. Use Scan Folders to retry.";
            if (!string.IsNullOrWhiteSpace(this.FindControl<TextBox>("SearchBox")!.Text))
                // A watcher refresh must not bypass the delay while the user is typing.
                await RunSearchAsync(debounce: true);
            else
                ResetSearchView("Index updated", "Automatic updates are active while Recall is open.");
        });
        monitor = new FolderMonitor(_metadataStore!,
            new FileTextExtractor(MaximumTextFileBytes, MaximumDocumentFileBytes, MaximumDocumentTextCharacters),
            _folders, IndexExclusions(), progress);
        _monitor = monitor;
        try
        {
            await monitor.StartAsync(_lifetime.Token);
            _storedFileCount = await _metadataStore!.GetFileCountAsync(_lifetime.Token);
            _textFileCount = await _metadataStore.GetTextFileCountAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await StopMonitoringAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Automatic updates unavailable: {0}", exception.GetType().Name);
            await StopMonitoringAsync();
            _monitorProblem = "Automatic updates unavailable. Use Index → Scan Folders.";
        }
    }

    private void UpdateMonitoringStatus()
    {
        if (!IsVisible || !_databaseReady)
            return;
        var status = this.FindControl<TextBlock>("IndexStatusText")!;
        if (_settingsProblem is not null)
            status.Text = "Auto: settings error; use Settings";
        else if (_folders.Count == 0)
            status.Text = "Index: Not configured";
        else if (!_settings.AutomaticUpdates)
            status.Text = $"Text files: {_textFileCount:N0} (manual)";
        else if (_monitorProblem is not null || _monitor?.FailureReason is not null)
            status.Text = "Auto: stopped; use Scan Folders";
        else
            status.Text = _monitor is not null ? $"Text files: {_textFileCount:N0} (watching)" : $"Text files: {_textFileCount:N0}";
    }

    private async Task<IDisposable?> PauseAutomaticUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_settings.AutomaticUpdates)
            return null;
        if (_monitor is null || _monitor.FailureReason is not null)
            await RestartMonitoringAsync();
        var monitor = _monitor;
        if (monitor is null)
            return null;
        try
        {
            return await monitor.PauseAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && monitor.FailureReason is not null)
        {
            // A failed watcher has stopped its worker; a manual scan remains available.
            return null;
        }
    }
}
