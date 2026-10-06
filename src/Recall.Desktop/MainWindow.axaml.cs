using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Recall.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => this.FindControl<TextBox>("SearchBox")?.Focus();
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
