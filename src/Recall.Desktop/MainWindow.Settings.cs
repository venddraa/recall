using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Recall.Core.Settings;

namespace Recall.Desktop;

public partial class MainWindow
{
    private ApplicationSettingsStore? _settingsStore;
    private ApplicationSettings _settings = new();
    private string? _settingsProblem;
    private bool _editingSettings;

    private async Task LoadSettingsAsync()
    {
        _settingsStore = new ApplicationSettingsStore(ApplicationSettingsStore.GetFilePath(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        try
        {
            _settings = await Task.Run(() => _settingsStore.LoadAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Preserve unreadable settings and allow manual search/scans without starting watchers.
            Trace.TraceWarning("Could not load application settings: {0}", exception.GetType().Name);
            _settings = new(AutomaticUpdates: false);
            _settingsProblem = "Could not read saved settings.";
        }
    }

    private async void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_settingsStore is null || _editingSettings || _scanCancellation is not null
            || !this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled)
            return;
        _editingSettings = true;
        this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = false;
        this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = false;
        this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = false;
        try
        {
            var dialog = new SettingsWindow(_settingsStore, _settings, _settingsProblem);
            var saved = await dialog.ShowDialog<ApplicationSettings?>(this);
            if (saved is null || !IsVisible)
                return;
            var automaticUpdatesChanged = _settings.AutomaticUpdates != saved.AutomaticUpdates;
            _settings = saved;
            _settingsProblem = null;
            // Saving an unchanged choice must not discard pending filesystem events.
            if (automaticUpdatesChanged)
                await RestartMonitoringAsync();
            if (!IsVisible)
                return;
            this.FindControl<TextBlock>("StoredFileCountText")!.Text = $"Stored files: {_storedFileCount:N0}";
            UpdateMonitoringStatus();
            if (string.IsNullOrWhiteSpace(this.FindControl<TextBox>("SearchBox")!.Text))
                ResetSearchView("Settings saved", (saved.AutomaticUpdates
                    ? "Automatic updates are enabled. Use Index → Scan Folders to catch changes made while updates were off."
                    : "Automatic updates are disabled. Use Index → Scan Folders when you want to update the index.")
                    + (saved.ScanOnStartup
                        ? "\nStartup scanning is enabled. Selected folders will be scanned the next time Recall opens."
                        : "\nStartup scanning is disabled."));
        }
        finally
        {
            _editingSettings = false;
            if (IsVisible)
            {
                this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = true;
                this.FindControl<MenuItem>("IndexedFoldersMenuItem")!.IsEnabled = true;
                this.FindControl<MenuItem>("ScanFoldersMenuItem")!.IsEnabled = _folders.Count > 0 && _databaseReady;
            }
        }
    }
}
