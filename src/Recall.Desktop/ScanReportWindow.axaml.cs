using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    public ScanReportWindow() => AvaloniaXamlLoader.Load(this);

    internal ScanReportWindow(CompletedScanReport report) : this()
    {
        this.FindControl<TextBlock>("ReportHeadingText")!.Text =
            $"{report.Operation} completed {report.CompletedAtUtc.ToLocalTime():g} — {report.FoundFiles:N0} files found";
        this.FindControl<TextBox>("SummaryText")!.Text =
            "Folders:\n" + string.Join("\n", report.Folders) + "\n\n" + report.Summary;
        var rows = report.ScannerFailures.Select(failure => new ScanIssueRow(failure.Path, failure.Message))
            .Concat(report.SkippedFiles.Select(file => new ScanIssueRow(file.FullPath, Describe(file.Status)))).ToArray();
        var list = this.FindControl<ListBox>("IssuesList")!;
        list.ItemsSource = rows;
        this.FindControl<TextBlock>("EmptyIssuesText")!.IsVisible = rows.Length == 0;
        this.FindControl<TextBlock>("ReportNoteText")!.Text =
            $"Samples: up to {CompletedScanReport.MaximumScannerDetails} scanner issues and {TextIndexResult.MaximumSkippedFileDetails} content skips. Unsupported formats and scanner-skipped links are summarized above.\n"
            + "This report covers the last completed scan in this session. Later automatic updates are not included.";
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

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
