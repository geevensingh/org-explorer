using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void ShowHistoricalTeamContext(string? email)
    {
        System.Windows.Automation.AutomationProperties.SetHelpText(HistoricalTeamReading, "");
        var captures = email is null ? [] : workflow.GetHistoricalTeamContexts(email);
        HistoricalTeamSummary.Text = email is null ? "Select a Person to review Historical team context." :
            captures.Count == 0 ? "No captured former context. Initial creation is a baseline; pre-import history is unknown." :
            $"Historical captures: {captures.Count}";
        var document = PrivateMarkdownReading.Create("", OpenHistoricalLink);
        document.TextAlignment = TextAlignment.Left;
        foreach (var capture in captures.Reverse())
        {
            var section = new Section { Tag = capture.Id, Margin = new Thickness(0, 12, 0, 20) };
            section.Blocks.Add(new Paragraph(new Bold(new Run(
                $"Captured locally {capture.CapturedAt.UtcDateTime:yyyy-MM-dd} (UTC) — " +
                string.Join("; ", capture.Transitions.Select(t => t.Change.Field == "archived"
                    ? "Archival closure" : "Reporting transition").Distinct())))));
            section.Blocks.Add(new Paragraph(new Bold(new Run(FormerChain(capture)))));
            section.Blocks.Add(CaptureMetadata(
                "Frozen former context, not current inheritance. Writing was locally known at capture, " +
                "not guaranteed at the actual change time. Exact effective date unknown."));
            var evidence = new TextBox
            {
                Text = $"Capture {capture.Id}\n{capture.DisplayProvenance}\n\n" +
                    string.Join("\n\n", capture.TeamNotes.Select(n => FrozenNoteEvidence(capture, n))),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Foreground = (Brush)FindResource("MutedBrush"), FontSize = 13,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            AutomationProperties.SetAutomationId(evidence, $"HistoricalCaptureEvidence-{capture.Id}");
            AutomationProperties.SetName(evidence, "Capture evidence; read-only, select text to copy");
            var details = new Expander
            {
                Header = "Capture details", Content = evidence,
                Style = (Style)FindResource("ReadingDisclosure")
            };
            details.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = HistoricalTeamReading });
            AutomationProperties.SetAutomationId(details, $"HistoricalCaptureDetails-{capture.Id}");
            AutomationProperties.SetName(details, $"Capture details — captured locally {capture.CapturedAt.UtcDateTime:yyyy-MM-dd} (UTC)");
            section.Blocks.Add(new BlockUIContainer(details));
            if (capture.TeamNotes.Count == 0)
                section.Blocks.Add(CaptureMetadata("No active Team notes were locally known for this former chain."));
            foreach (var note in capture.TeamNotes)
            {
                var reading = new Section
                {
                    Tag = note.Id, BorderBrush = (Brush)FindResource("SeparatorBrush"),
                    BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0),
                    Margin = new Thickness(0, 12, 0, 16)
                };
                reading.Blocks.Add(new Paragraph(new Bold(new Run($"Frozen Team note: {note.Title}"))));
                reading.Blocks.Add(CaptureMetadata($"Owned by {FrozenOwner(capture, note)}"));
                reading.Blocks.Add(CaptureMetadata(note.NeedsReview
                    ? "Needs review — needed review when captured."
                    : "Confirmed context when captured."));
                var rendered = PrivateMarkdownReading.Create(note.Markdown, OpenHistoricalLink);
                while (rendered.Blocks.FirstBlock is { } block)
                {
                    rendered.Blocks.Remove(block);
                    reading.Blocks.Add(block);
                }
                section.Blocks.Add(reading);
            }
            document.Blocks.Add(section);
        }
        HistoricalTeamReading.Document = document;
    }

    private static string FormerChain(HistoricalTeamContext capture) =>
        "Former Reporting chain: " + (capture.ReportingChain.Count == 0 ? "(explicit no-Manager)" :
            string.Join(" → ", capture.ReportingChain.Select(p => $"{p.Heading} ({p.Email})")));

    private Paragraph CaptureMetadata(string text) => new(new Run(text))
    {
        Foreground = (Brush)FindResource("MutedBrush"), FontSize = 13,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static string FrozenOwner(HistoricalTeamContext capture, CapturedTeamNote note)
    {
        var owner = capture.ReportingChain.Single(p => string.Equals(p.Email, note.OwnerEmail, StringComparison.OrdinalIgnoreCase));
        return $"{owner.Heading} ({owner.Email})";
    }

    private static string FrozenNoteEvidence(HistoricalTeamContext capture, CapturedTeamNote note) =>
        $"Frozen '{note.Title}' — owned by {FrozenOwner(capture, note)}; note {note.Id}; version {note.Version}; " +
            $"local document updated {note.UpdatedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'}" +
            (note.NeedsReview ? "; needed review when captured." : "; confirmed context when captured.");

    private void OpenHistoricalLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { StatusText.Text = "External historical link could not be opened: " + ex.Message; }
    }
}
