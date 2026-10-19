using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Recall.Desktop;

public partial class SearchHelpWindow : Window
{
    public SearchHelpWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => this.FindControl<ScrollViewer>("HelpContentScroll")!.Focus();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
