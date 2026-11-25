using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void AssertGroupedImportDisclosure(AutomationElement window, string email, string summary,
        string[] details, string? capture = null) =>
        AssertGroupedImportDisclosure(window, email, summary, summary, details, capture);

    private static void AssertGroupedImportDisclosure(AutomationElement window, string email, string accessibleSummary,
        string renderedSummary, string[] details, string? capture = null, bool allowCountFallback = false)
    {
        var review = Find(window, "Preview");
        var identity = "ImportPerson:" + email;
        var entries = review.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, identity));
        if (entries.Count != 1)
            throw new InvalidOperationException($"Expected one rendered import entry for {email}, got {entries.Count}.");
        var entry = entries[0];
        var disclosure = (ExpandCollapsePattern)entry.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (disclosure.Current.ExpandCollapseState != ExpandCollapseState.Collapsed ||
            entry.Current.Name != $"{email} — {accessibleSummary}")
            throw new InvalidOperationException("Person review must start collapsed with the complete accessible identity and semantic count/Add.");
        if (allowCountFallback &&
            ((ValuePattern)Find(entry, "ImportSummary").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == accessibleSummary)
            renderedSummary = accessibleSummary;
        AssertCollapsedImportLine(window, entry, email, renderedSummary);
        var header = entry.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true))
            ?? throw new InvalidOperationException("The rendered Person disclosure has no keyboard-accessible header.");
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
        foreach (var detail in details)
        {
            var value = entry.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, detail));
            if (value is null || value.Current.IsOffscreen || value.Current.BoundingRectangle.IsEmpty)
                throw new InvalidOperationException("Expanded import detail is missing or not rendered: " + detail);
            AssertImportCharacters(window, value, detail, review.Current.BoundingRectangle, singleLine: false);
        }
        if (review.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, identity)).Count != 1)
            throw new InvalidOperationException("Expanding a Person duplicated its top-level review entry.");
        CaptureVisualEvidence(window, "import-expanded-" +
            (capture ?? (accessibleSummary == "Add" ? "addition" : accessibleSummary.StartsWith("Archive") ? "archive" : "differences")));
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
    }

    private static void AssertCollapsedImportLine(AutomationElement window, AutomationElement entry, string email,
        string summary)
    {
        var viewport = Find(window, "Preview").Current.BoundingRectangle;
        var identity = Find(entry, "ImportIdentity");
        var reading = Find(entry, "ImportSummary");
        var identityLine = AssertImportCharacters(window, identity, email, viewport, singleLine: true);
        var summaryLine = AssertImportCharacters(window, reading, summary, viewport, singleLine: true);
        var scale = DpiScale(window);
        if (Math.Abs(identityLine.Top - summaryLine.Top) > scale ||
            Math.Abs(identityLine.Height - summaryLine.Height) > scale ||
            identityLine.Right > summaryLine.Left ||
            entry.Current.BoundingRectangle.Height > Math.Max(identityLine.Height, summaryLine.Height) + 12 * scale)
            throw new InvalidOperationException($"The collapsed identity and complete summary are not on one native line: identity={identityLine}; summary={summaryLine}; entry={entry.Current.BoundingRectangle}.");
    }

    private static Rect AssertImportCharacters(AutomationElement window, AutomationElement value, string expected,
        Rect viewport, bool singleLine)
    {
        if (!value.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            throw new InvalidOperationException("Import reading has no native TextPattern: " + expected);
        var document = ((TextPattern)pattern).DocumentRange;
        var actual = document.GetText(-1);
        if (actual.Replace("\r\n", "\n") != expected.Replace("\r\n", "\n") ||
            !Equals(document.GetAttributeValue(TextPattern.FontNameAttribute), "Segoe UI") ||
            !Equals(document.GetAttributeValue(TextPattern.FontSizeAttribute), 10.5d))
            throw new InvalidOperationException("Import reading lost complete values or changed typography: " + actual);
        var cursor = document.Clone();
        cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
        var line = Rect.Empty;
        var scale = DpiScale(window);
        for (var offset = 0; offset < actual.Length;)
        {
            var character = cursor.Clone();
            if (character.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) != 1)
                throw new InvalidOperationException("Import text lost a character range: " + offset);
            var text = character.GetText(-1);
            if (text.Length == 0 || !actual.AsSpan(offset).StartsWith(text, StringComparison.Ordinal))
                throw new InvalidOperationException("Import text range differs from its complete value at " + offset);
            offset += text.Length;
            if (text is not "\r\n" and not "\n" and not "\r")
            {
                var rectangles = character.GetBoundingRectangles();
                if (rectangles.Length == 0)
                    throw new InvalidOperationException("Import character has no rendered rectangle: " + offset);
                foreach (var glyph in rectangles)
                {
                    var bounds = value.Current.BoundingRectangle;
                    if (glyph.IsEmpty || glyph.Width <= 0 || glyph.Height <= 0 ||
                        glyph.Left < bounds.Left - scale || glyph.Right > bounds.Right + scale ||
                        glyph.Top < bounds.Top - scale || glyph.Bottom > bounds.Bottom + scale ||
                        glyph.Left < viewport.Left - scale || glyph.Right > viewport.Right + scale ||
                        glyph.Top < viewport.Top - scale || glyph.Bottom > viewport.Bottom + scale ||
                        singleLine && !line.IsEmpty &&
                        (Math.Abs(glyph.Top - line.Top) > scale || Math.Abs(glyph.Height - line.Height) > scale))
                        throw new InvalidOperationException($"Import character is clipped or collapsed text wrapped: index={offset}; glyph={glyph}; value={bounds}; viewport={viewport}.");
                    line.Union(glyph);
                }
            }
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, character, TextPatternRangeEndpoint.End);
        }
        return line;
    }

    private static void ExerciseAdaptiveImportReview(AutomationElement window, string workspace, string import)
    {
        var before = WorkspaceFixtures.Bytes(workspace);
        var originalBounds = window.Current.BoundingRectangle;
        var scale = DpiScale(window);
        const string inline = "Office state: (none) → Washington; Office country: Canada → United States of America";
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000088",
             "observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"avery@example.com","officeState":"Washington","officeCountry":"United States of America"}]}
            """);
        using (var workflow = OpenSmokeWorkspace(workspace))
        {
            var preview = workflow.PreviewImportFile(import);
            if (!preview.CanApply || preview.People.Single().Changes.Count != 2)
                throw new InvalidOperationException("The adaptive pair is not a valid two-change public preview.");
        }
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        var reviewedName = Find(window, "Preview").Current.Name;
        try
        {
            foreach (var (width, summary, state) in new[]
            {
                (1000, inline, "wide"),
                (700, "2 changes", "narrow"),
                (1000, inline, "restored")
            })
            {
                ResizeWindow(window, width, 700);
                var entry = Find(window, "ImportPerson:avery@example.com");
                Await(() => ((ValuePattern)Find(entry, "ImportSummary").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == summary);
                AssertCollapsedImportLine(window, entry, "avery@example.com", summary);
                if (entry.Current.Name != "avery@example.com — 2 changes" ||
                    ((ExpandCollapsePattern)entry.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState != ExpandCollapseState.Collapsed ||
                    Find(window, "Preview").Current.Name != reviewedName || !Find(window, "ApplyImport").Current.IsEnabled)
                    throw new InvalidOperationException("Width adaptation changed the accepted preview, semantic count or disclosure state.");
                AssertWorkspaceBytes(workspace, before);
                CaptureVisualEvidence(window, "import-adaptive-" + state);
            }
            AssertGroupedImportDisclosure(window, "avery@example.com", "2 changes", inline,
                ["Office state: (none) → Washington", "Office country: Canada → United States of America"], "adaptive-pair");
            var longValue = new string('W', 160) + " complete end";
            const string multiline = "North floor\nSouth floor";
            File.WriteAllText(import, $$"""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000090",
                 "observedAt":"2026-11-03T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery updated","alias":"avery.updated",
                  "jobTitle":"Principal researcher","department":"Applied research","officeBuilding":{{JsonSerializer.Serialize(longValue)}},
                  "officeNumber":"0099","officeState":"Washington","officeCountry":"United States","isFavorite":true,"photoPath":null},
                 {"email":"morgan@example.com","officeBuilding":{{JsonSerializer.Serialize(longValue)}}},
                 {"email":"V-reference@example.com","officeBuilding":{{JsonSerializer.Serialize(multiline)}}},
                 {"email":"v-child@example.com","officeBuilding":{{JsonSerializer.Serialize(longValue)}},"managerEmail":null,"archived":true,"isFavorite":true},
                 {"email":"archived.reference@example.com","officeBuilding":{{JsonSerializer.Serialize(longValue)}},"archived":false},
                 {"email":"new.reference@example.com","displayName":"New reference","managerEmail":null}]}
                """);
            using (var workflow = OpenSmokeWorkspace(workspace))
            {
                var preview = workflow.PreviewImportFile(import);
                if (!preview.CanApply ||
                    !preview.People.Select(person => person.Changes.Count).SequenceEqual(new[] { 10, 1, 1, 4, 2, 0 }) ||
                    !preview.People.Last().IsNew)
                    throw new InvalidOperationException("The many/long/multiline/lifecycle fixture is not the expected accepted public preview: " + workflow.Status);
            }
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            foreach (var (email, summary) in new[]
            {
                ("avery@example.com", "10 changes"), ("morgan@example.com", "1 change"),
                ("V-reference@example.com", "1 change"), ("v-child@example.com", "Archive; 4 changes"),
                ("archived.reference@example.com", "Reactivate; 2 changes"), ("new.reference@example.com", "Add")
            })
                AssertCollapsedImportLine(window, Find(window, "ImportPerson:" + email), email, summary);
            AssertGroupedImportDisclosure(window, "morgan@example.com", "1 change",
                ["Office building: (none) → " + longValue], capture: "adaptive-long");
            AssertGroupedImportDisclosure(window, "V-reference@example.com", "1 change",
                ["Office building: (none) → " + multiline], capture: "adaptive-multiline");
            AssertGroupedImportDisclosure(window, "v-child@example.com", "Archive; 4 changes",
                ["Archive: Current → Archived; retain profile and local context"], capture: "adaptive-archive");
            AssertGroupedImportDisclosure(window, "archived.reference@example.com", "Reactivate; 2 changes",
                ["Reactivate: Archived → Current reporting tree"], capture: "adaptive-reactivate");
            AssertGroupedImportDisclosure(window, "new.reference@example.com", "Add",
                ["Directory name: New reference"], capture: "adaptive-add");
            AssertWorkspaceBytes(workspace, before);
        }
        finally { ResizeWindow(window, originalBounds.Width / scale, originalBounds.Height / scale); }
    }
}
