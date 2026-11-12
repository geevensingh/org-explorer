using System.Diagnostics;
using System.Windows.Documents;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private void ShowHistoricalTeamContext(string? email)
    {
        var captures = email is null ? [] : workflow.GetHistoricalTeamContexts(email);
        HistoricalTeamSummary.Text = email is null ? "Select a Person to review Historical team context." :
            captures.Count == 0 ? "No captured former context. Initial creation is a baseline; pre-import history is unknown." :
            $"Historical captures: {captures.Count}\n\n" +
            string.Join("\n\n", captures.Reverse().Select(c =>
                FormerChain(c) + "\n" + c.DisplayProvenance + "\n" +
                (c.TeamNotes.Count == 0 ? "No active Team notes were locally known for this former chain." :
                    string.Join("\n", c.TeamNotes.Select(n => FrozenNoteLabel(c, n))))));
        var document = PrivateMarkdownReading.Create("", OpenHistoricalLink);
        foreach (var capture in captures.Reverse())
        {
            document.Blocks.Add(new Paragraph(new Bold(new Run(FormerChain(capture)))));
            document.Blocks.Add(new Paragraph(new Run(capture.DisplayProvenance)));
            foreach (var note in capture.TeamNotes)
            {
                document.Blocks.Add(new Paragraph(new Bold(new Run(FrozenNoteLabel(capture, note)))));
                var rendered = PrivateMarkdownReading.Create(note.Markdown, OpenHistoricalLink);
                while (rendered.Blocks.FirstBlock is { } block)
                {
                    rendered.Blocks.Remove(block);
                    document.Blocks.Add(block);
                }
            }
        }
        HistoricalTeamReading.Document = document;
    }

    private static string FormerChain(HistoricalTeamContext capture) =>
        "Former Reporting chain: " + (capture.ReportingChain.Count == 0 ? "(explicit no-Manager)" :
            string.Join(" → ", capture.ReportingChain.Select(p => $"{p.Heading} ({p.Email})")));

    private static string FrozenNoteLabel(HistoricalTeamContext capture, CapturedTeamNote note)
    {
        var owner = capture.ReportingChain.Single(p => string.Equals(p.Email, note.OwnerEmail, StringComparison.OrdinalIgnoreCase));
        return $"Frozen '{note.Title}' — owned by {owner.Heading} ({owner.Email}); note {note.Id}; version {note.Version}; " +
            $"local document updated {note.UpdatedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'}" +
            (note.NeedsReview ? "; needed review when captured." : "; confirmed context when captured.");
    }

    private void OpenHistoricalLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { StatusText.Text = "External historical link could not be opened: " + ex.Message; }
    }
}
