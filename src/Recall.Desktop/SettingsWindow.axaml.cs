using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Recall.Core.Settings;

namespace Recall.Desktop;

public partial class SettingsWindow : Window
{
    private ApplicationSettingsStore? _store;
    private bool _busy;

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, e) => e.Cancel = _busy;
        SetBusy(false);
    }

    public SettingsWindow(ApplicationSettingsStore store, ApplicationSettings settings, string? problem) : this()
    {
        _store = store;
        this.FindControl<CheckBox>("AutomaticUpdatesCheckBox")!.IsChecked = settings.AutomaticUpdates;
        this.FindControl<CheckBox>("ScanOnStartupCheckBox")!.IsChecked = settings.ScanOnStartup;
        if (problem is not null)
            ShowError("Could not read saved settings. Automatic updates and startup scanning are off. Saving here replaces the unreadable settings.");
        SetBusy(false);
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _store is not { } store)
            return;
        var settings = new ApplicationSettings(
            AutomaticUpdates: this.FindControl<CheckBox>("AutomaticUpdatesCheckBox")!.IsChecked == true,
            ScanOnStartup: this.FindControl<CheckBox>("ScanOnStartupCheckBox")!.IsChecked == true);
        SetBusy(true);
        try
        {
            await Task.Run(() => store.SaveAsync(settings));
            SetBusy(false);
            Close(settings);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not save application settings: {0}", exception.GetType().Name);
            ShowError("Could not save settings. The active setting has not changed. Check your configuration directory and try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        this.FindControl<CheckBox>("AutomaticUpdatesCheckBox")!.IsEnabled = !busy;
        this.FindControl<CheckBox>("ScanOnStartupCheckBox")!.IsEnabled = !busy;
        this.FindControl<Button>("SaveButton")!.IsEnabled = !busy && _store is not null;
        this.FindControl<Button>("CancelButton")!.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        var text = this.FindControl<TextBlock>("MessageText")!;
        text.Text = message;
        text.Foreground = Brushes.DarkRed;
        text.IsVisible = true;
    }
}
