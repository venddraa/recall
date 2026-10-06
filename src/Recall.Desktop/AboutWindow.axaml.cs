using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Recall.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
