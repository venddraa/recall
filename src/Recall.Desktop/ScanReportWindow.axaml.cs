using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Storage;

namespace Recall.Desktop;

internal sealed record CompletedScanReport(DateTime CompletedAtUtc, string Operation,
    IReadOnlyList<string> Folders, int FoundFiles, string Summary,
    IReadOnlyList<ScanFailure> ScannerFailures, IReadOnlyList<SkippedFileDetail> SkippedFiles)
{
    public const int MaximumScannerDetails = 100;
}

public sealed record ScanIssueRow(string FullPath, string Reason)
{
    public string Filename => Path.GetFileName(FullPath) is { Length: > 0 } name ? name : FullPath;
    public string Folder => Path.GetDirectoryName(FullPath) ?? FullPath;
}

public partial class ScanReportWindow : Window
{
    private readonly string? _reportText;

    public ScanReportWindow() => AvaloniaXamlLoader.Load(this);

    internal ScanReportWindow(CompletedScanReport report) : this()
    {
        var heading = $"{report.Operation} completed {report.CompletedAtUtc.ToLocalTime():g} — {report.FoundFiles:N0} files found";
        this.FindControl<TextBlock>("ReportHeadingText")!.Text = heading;
        var summary = "Folders:\n" + string.Join("\n", report.Folders) + "\n\n" + report.Summary;
        this.FindControl<TextBox>("SummaryText")!.Text = summary;
        var rows = report.ScannerFailures.Select(failure => new ScanIssueRow(failure.Path, failure.Message))
            .Concat(report.SkippedFiles.Select(file => new ScanIssueRow(file.FullPath, Describe(file.Status)))).ToArray();
        var list = this.FindControl<ListBox>("IssuesList")!;
        list.ItemsSource = rows;
        this.FindControl<TextBlock>("EmptyIssuesText")!.IsVisible = rows.Length == 0;
        var note =
            $"Samples: up to {CompletedScanReport.MaximumScannerDetails} scanner issues and {TextIndexResult.MaximumSkippedFileDetails} content skips. Unsupported formats and scanner-skipped links are summarized above.\n"
            + "This report covers the last completed scan in this session. Later automatic updates are not included.";
        this.FindControl<TextBlock>("ReportNoteText")!.Text = note;
        var issues = rows.Length == 0 ? "No detailed issues were recorded."
            : string.Join("\n\n", rows.Select(row => row.FullPath + "\n" + row.Reason));
        // Keep the copied report tied to the same snapshot displayed by this dialog.
        _reportText = heading + "\n\n" + summary + "\n\nSample issues:\n" + issues + "\n\n" + note;
        this.FindControl<Button>("CopyReportButton")!.IsEnabled = true;
        if (rows.Length > 0)
            list.SelectedIndex = 0;
    }

    private static string Describe(TextExtractionStatus status) => status switch
    {
        TextExtractionStatus.TooLarge => "Content exceeds the indexing size limit.",
        TextExtractionStatus.InvalidEncoding => "Text encoding could not be read.",
        TextExtractionStatus.Binary => "Binary data was skipped for content indexing.",
        TextExtractionStatus.Changed => "File changed while its contents were being read. Scan again to retry.",
        TextExtractionStatus.Unavailable => "File could not be read. Check its availability and permissions.",
        TextExtractionStatus.SymbolicLink => "Content was skipped because the file or its path was a symbolic link.",
        TextExtractionStatus.NoSearchableText => "Document contains no searchable text.",
        TextExtractionStatus.ParserFailed => "Document text extraction failed. Reindex its folder to retry.",
        _ => "Content was not indexed."
    };

    private void IssuesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var details = this.FindControl<TextBox>("IssueDetailsText");
        if (details is not null)
            details.Text = (sender as ListBox)?.SelectedItem is ScanIssueRow row
                ? row.FullPath + "\n" + row.Reason : "Select an issue to see its full path and reason.";
    }

    private async void CopyReport_OnClick(object? sender, RoutedEventArgs e)
    {
        var button = this.FindControl<Button>("CopyReportButton")!;
        if (!button.IsEnabled || _reportText is not { } text)
            return;
        var status = this.FindControl<TextBlock>("CopyStatusText")!;
        button.IsEnabled = false;
        status.Foreground = Brushes.Black;
        status.Text = "Copying report...";
        try
        {
            // The owner stays open when the report dialog closes.
            var clipboard = Owner?.Clipboard ?? Clipboard
                ?? throw new InvalidOperationException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(text);
            if (IsVisible)
                status.Text = "Report copied";
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not copy the scan report: {0}", exception.GetType().Name);
            if (IsVisible)
            {
                status.Foreground = Brushes.DarkRed;
                status.Text = "Could not copy report. Try again.";
            }
        }
        finally
        {
            if (IsVisible)
                button.IsEnabled = true;
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
