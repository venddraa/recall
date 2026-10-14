using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Recall.Desktop;

public partial class MainWindow
{
    private ContextMenu? _resultContextMenu;

    private void MainWindow_OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F || e.KeyModifiers != KeyModifiers.Control)
            return;
        var searchBox = this.FindControl<TextBox>("SearchBox")!;
        if (!searchBox.IsEnabled)
            return;
        e.Handled = true;
        CloseResultContextMenu();
        searchBox.Focus();
        searchBox.SelectAll();
    }

    private bool FocusSearchResults()
    {
        var results = this.FindControl<ListBox>("ResultsList")!;
        if (results.ItemCount == 0 || _scanCancellation is not null)
            return false;
        if (results.SelectedIndex < 0)
            results.SelectedIndex = 0;
        results.ScrollIntoView(results.SelectedIndex);
        (results.ContainerFromIndex(results.SelectedIndex) ?? results).Focus();
        return true;
    }

    private async void ResultsList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _scanCancellation is not null
            || this.FindControl<ListBox>("ResultsList")!.SelectedItem is not SearchResultRow row)
            return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            await OpenFileAsync(row);
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await OpenFolderAsync(row);
        }
        else if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            await CopyPathAsync(row);
        }
    }

    private void ResultsList_OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        CloseResultContextMenu();
        if (_scanCancellation is not null || sender is not ListBox results)
            return;

        var fromPointer = e.TryGetPosition(results, out var position);
        Control? target;
        SearchResultRow? row;
        if (fromPointer)
        {
            var hit = results.InputHitTest(position) as Control;
            var item = hit as ListBoxItem ?? hit?.FindAncestorOfType<ListBoxItem>();
            target = item;
            row = item?.DataContext as SearchResultRow;
        }
        else
        {
            row = results.SelectedItem as SearchResultRow;
            target = row is null ? null : results.ContainerFromItem(row);
        }
        // A click in empty space must never act on a previously selected row.
        if (row is null)
            return;
        results.SelectedItem = row;
        (target ?? results).Focus();

        var openFile = new MenuItem { Header = "Open File", InputGesture = new(Key.Enter), IsEnabled = !_openingFile };
        var openFolder = new MenuItem { Header = "Open Containing Folder", InputGesture = new(Key.Enter, KeyModifiers.Control), IsEnabled = !_openingFolder };
        var copyPath = new MenuItem { Header = "Copy Path", InputGesture = new(Key.C, KeyModifiers.Control | KeyModifiers.Shift), IsEnabled = !_copyingPath };
        // Capture the clicked row rather than reading a potentially refreshed selection on click.
        openFile.Click += async (_, _) => await OpenFileAsync(row);
        openFolder.Click += async (_, _) => await OpenFolderAsync(row);
        copyPath.Click += async (_, _) => await CopyPathAsync(row);
        var menu = new ContextMenu
        {
            Placement = fromPointer ? PlacementMode.Pointer : PlacementMode.Bottom,
            ItemsSource = new Control[] { openFile, openFolder, new Separator(), copyPath }
        };
        _resultContextMenu = menu;
        menu.Open(target ?? results);
    }

    private void CloseResultContextMenu()
    {
        var menu = _resultContextMenu;
        _resultContextMenu = null;
        menu?.Close();
    }
}
