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
    private static double ImportDetailScrollPercent(Rect viewport, Rect detail, double percent, double viewSize)
    {
        if (viewport.IsEmpty || detail.IsEmpty || viewport.Height <= 0 || detail.Height <= 0 ||
            detail.Height > viewport.Height || !double.IsFinite(viewport.Top) || !double.IsFinite(viewport.Height) ||
            !double.IsFinite(detail.Top) || !double.IsFinite(detail.Height) ||
            !double.IsFinite(percent) || percent is < 0 or > 100 || !double.IsFinite(viewSize) ||
            viewSize is <= 0 or >= 100)
            throw new InvalidOperationException($"Import detail reveal requires a real fitting range and scrollable viewport: detail={detail}; viewport={viewport}; percent={percent}; viewSize={viewSize}.");
        var extent = viewport.Height * (100 / viewSize - 1);
        var delta = detail.Top + detail.Height / 2 - viewport.Top - viewport.Height / 2;
        return Math.Clamp(percent + delta * 100 / extent, 0, 100);
    }

    private static void SetImportDisclosure(AutomationElement header, ExpandCollapsePattern disclosure,
        ExpandCollapseState expected)
    {
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == expected);
    }

    private static void AssertImportBatchImpact(AutomationElement window, string totals, string? actions = null)
    {
        var impact = Find(window, "ImportImpact").Current;
        var review = Find(window, "Preview").Current;
        if (impact.Name != totals || impact.IsOffscreen || impact.BoundingRectangle.IsEmpty ||
            impact.BoundingRectangle.Bottom > review.BoundingRectangle.Top)
            throw new InvalidOperationException("Complete zero-inclusive Import totals must be visible above the scrolling Person list.");
        var action = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportActions"));
        if (actions is null ? action is not null && !action.Current.IsOffscreen :
            action is null || action.Current.IsOffscreen || action.Current.Name != "Overlapping actions for existing people — " + actions)
            throw new InvalidOperationException("Existing-Person action breakdowns must be nonzero, overlapping and distinct from Add baselines.");
    }

    private static void AssertImportUnchangedDisclosure(AutomationElement window, string[] emails, string? diagnostic = null)
    {
        var section = Find(window, "ImportUnchanged");
        var disclosure = (ExpandCollapsePattern)section.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (disclosure.Current.ExpandCollapseState != ExpandCollapseState.Collapsed ||
            section.Current.Name != $"No Person changes: {emails.Length}")
            throw new InvalidOperationException("The counted no-Person-changes section must start collapsed.");
        var header = section.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true))
            ?? throw new InvalidOperationException("The final Import section lacks a native keyboard-accessible header.");
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Expanded);
        var list = Find(window, "ImportUnchangedIdentities");
        var entries = list.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (!entries.Cast<AutomationElement>().Select(e => e.Current.Name).SequenceEqual(emails) ||
            entries.Cast<AutomationElement>().Any(e => e.Current.IsOffscreen || e.Current.BoundingRectangle.IsEmpty) ||
            list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)).Count != 0)
            throw new InvalidOperationException("No-Person-changes expansion must render only ordered email identities, without Person/evidence disclosures.");
        if (diagnostic is not null) AssertImportDiagnostic(window, diagnostic);
        CaptureVisualEvidence(window, "import-unchanged-identities");
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Collapsed);
        if (diagnostic is not null) AssertImportDiagnostic(window, diagnostic);
    }

    private static void AssertImportDiagnostic(AutomationElement window, string expected)
    {
        var diagnostic = Find(window, "ImportDiagnostics").Current;
        var review = Find(window, "Preview").Current;
        if (!diagnostic.Name.Contains(expected, StringComparison.Ordinal) || diagnostic.IsOffscreen ||
            diagnostic.BoundingRectangle.IsEmpty || diagnostic.BoundingRectangle.Bottom > review.BoundingRectangle.Top)
            throw new InvalidOperationException("Import diagnostics/status must remain visible outside and above all Person disclosures.");
    }

    private static void AssertImportHasNoImpact(AutomationElement window)
    {
        foreach (var id in new[] { "ImportImpact", "ImportActions", "ImportObservations", "ImportUnchanged" })
        {
            var entry = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (entry is not null && !entry.Current.IsOffscreen)
                throw new InvalidOperationException("Invalid/replayed Import must not show new complete impact: " + id);
        }
        if (Find(window, "Preview").FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count != 0)
            throw new InvalidOperationException("Invalid/replayed Import must not imply partial/new Person effects.");
    }

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
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Expanded);
        foreach (var detail in details)
        {
            var value = entry.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportDetail"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, detail)));
            if (value is null)
                throw new InvalidOperationException("Expanded import detail is missing: " + detail);
            Await(() => value.Current.BoundingRectangle is { IsEmpty: false, Width: > 0, Height: > 0 });
            value.SetFocus();
            Await(() => value.Current.HasKeyboardFocus);
            var viewport = review.Current.BoundingRectangle;
            var bounds = value.Current.BoundingRectangle;
            if (value.Current.IsOffscreen || !viewport.Contains(bounds))
            {
                var scrolling = (ScrollPattern)review.GetCurrentPattern(ScrollPattern.Pattern);
                var current = scrolling.Current;
                if (!current.VerticallyScrollable)
                    throw new InvalidOperationException($"Expanded Import detail is outside a non-scrollable viewport: detail={bounds}; viewport={viewport}.");
                scrolling.SetScrollPercent(ScrollPattern.NoScroll, ImportDetailScrollPercent(viewport, bounds,
                    current.VerticalScrollPercent, current.VerticalViewSize));
            }
            AssertImportCharacters(window, value, detail, review.Current.BoundingRectangle, singleLine: false);
        }
        if (review.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, identity)).Count != 1)
            throw new InvalidOperationException("Expanding a Person duplicated its top-level review entry.");
        CaptureVisualEvidence(window, "import-expanded-" +
            (capture ?? (accessibleSummary == "Add" ? "addition" : accessibleSummary.StartsWith("Archive") ? "archive" : "differences")));
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Collapsed);
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
        Rect viewport, bool singleLine, string? visibleText = null)
    {
        var line = Rect.Empty;
        var failure = "";
        bool Observe() => ObserveImportCharacters(window, value, expected, viewport, singleLine, visibleText,
            out line, out failure);
        try { Await(Observe); }
        catch (TimeoutException error)
        {
            var current = value.Current;
            var scroll = ((ScrollPattern)Find(window, "Preview").GetCurrentPattern(ScrollPattern.Pattern)).Current;
            throw new TimeoutException($"Import characters were not visibly reached within the existing observation budget: " +
                $"id={current.AutomationId}; type={current.ControlType.ProgrammaticName}; name={current.Name}; offscreen={current.IsOffscreen}; " +
                $"value={current.BoundingRectangle}; viewport={viewport}; scrollPercent={scroll.VerticalScrollPercent}; viewSize={scroll.VerticalViewSize}; " +
                $"textLength={expected.Length}; lastObservation={failure}", error);
        }
        return line;
    }

    private static bool ObserveImportCharacters(AutomationElement window, AutomationElement value, string expected,
        Rect viewport, bool singleLine, string? visibleText, out Rect line, out string failure)
    {
        line = Rect.Empty;
        failure = "";
        if (!value.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            throw new InvalidOperationException("Import reading has no native TextPattern: " + expected);
        var document = ((TextPattern)pattern).DocumentRange;
        var actual = document.GetText(-1);
        if (actual.Replace("\r\n", "\n") != expected.Replace("\r\n", "\n") ||
            !Equals(document.GetAttributeValue(TextPattern.FontNameAttribute), "Segoe UI") ||
            !Equals(document.GetAttributeValue(TextPattern.FontSizeAttribute), 10.5d))
            throw new InvalidOperationException("Import reading lost complete values or changed typography: " + actual);
        var cursor = visibleText is null ? document.Clone() :
            document.FindText(visibleText, false, false)
                ?? throw new InvalidOperationException("Import text lost its requested visible range: " + visibleText);
        if (visibleText is not null) actual = visibleText;
        cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
        if (value.Current.IsOffscreen)
        {
            failure = "Import text control is offscreen.";
            return false;
        }
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
                {
                    failure = "Import character has no rendered rectangle: " + offset;
                    return false;
                }
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
                    {
                        failure = $"Import character is clipped or collapsed text wrapped: index={offset}; glyph={glyph}; value={bounds}; viewport={viewport}.";
                        return false;
                    }
                    line.Union(glyph);
                }
            }
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, character, TextPatternRangeEndpoint.End);
        }
        return true;
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
            AssertImportBatchImpact(window, "People added: 1; Existing people changed: 5; No Person changes: 0",
                "Archived: 1; Reactivated: 1; Manager changed: 1; Photo changed: 1; Favorite changed: 2");
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
            ExerciseTallImportDetail(window, workspace, import);
            ExerciseManyImportDiagnostics(window, workspace, import);
            AssertWorkspaceBytes(workspace, before);
        }
        finally { ResizeWindow(window, originalBounds.Width / scale, originalBounds.Height / scale); }
    }

    private static void ExerciseTallImportDetail(AutomationElement window, string workspace, string import)
    {
        const string tail = "Final building characters";
        var multiline = string.Join("\n", Enumerable.Range(1, 80).Select(line => $"Floor {line:00} complete value")) +
            "\n" + tail;
        File.WriteAllText(import, $$"""
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000091",
             "observedAt":"2026-11-03T17:00:00Z","people":[
             {"email":"avery@example.com","officeBuilding":{{JsonSerializer.Serialize(multiline)}},"officeNumber":"FINAL-0099","officeState":"WA"}]}
            """);
        using (var workflow = OpenSmokeWorkspace(workspace))
        {
            var preview = workflow.PreviewImportFile(import);
            if (!preview.CanApply || preview.People.Single().Changes.Count != 3)
                throw new InvalidOperationException("The viewport-taller multiline fixture must be a valid three-change public preview.");
        }
        ResizeWindow(window, 700, 700);
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        var review = Find(window, "Preview");
        var entry = Find(window, "ImportPerson:avery@example.com");
        AssertCollapsedImportLine(window, entry, "avery@example.com", "3 changes");
        var disclosure = (ExpandCollapsePattern)entry.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        var header = entry.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true))
            ?? throw new InvalidOperationException("Tall Import entry lacks a native keyboard disclosure.");
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Expanded);
        var buildingText = "Office building: (none) → " + multiline;
        var building = entry.FindFirst(TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportDetail"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.NameProperty, buildingText)))
            ?? throw new InvalidOperationException("Tall Import lost the complete multiline value.");
        Await(() => building.Current.BoundingRectangle.Height > review.Current.BoundingRectangle.Height);
        AssertImportCharacters(window, building, buildingText, review.Current.BoundingRectangle,
            singleLine: false, visibleText: "Floor 01 complete value");
        var scroll = (ScrollPattern)review.GetCurrentPattern(ScrollPattern.Pattern);
        if (!scroll.Current.VerticallyScrollable)
            throw new InvalidOperationException("Viewport-taller Import details have no native scrolling.");
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        Await(() => scroll.Current.VerticalScrollPercent == 100);
        AssertImportCharacters(window, building, buildingText, review.Current.BoundingRectangle,
            singleLine: false, visibleText: tail);
        foreach (var next in new[] { "Office number: 007B → FINAL-0099", "Office state: (none) → WA" })
        {
            var field = entry.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ImportDetail"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, next)))
                ?? throw new InvalidOperationException("A field after the tall value is missing.");
            AssertImportCharacters(window, field, next, review.Current.BoundingRectangle, singleLine: false);
        }
        CaptureVisualEvidence(window, "import-tall-value-end-and-next-fields");
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        Await(() => scroll.Current.VerticalScrollPercent == 0);
        SetImportDisclosure(header, disclosure, ExpandCollapseState.Collapsed);
    }

    private static void ExerciseManyImportDiagnostics(AutomationElement window, string workspace, string import)
    {
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-000000000092",
             "observedAt":"2026-10-02T17:00:00Z","people":[
             {"email":"avery@example.com","displayName":"Avery Example","alias":"avery","officeNumber":"FINAL-0099",
              "department":"Research","jobTitle":"Research engineer","officeCountry":"Canada","managerEmail":"morgan@example.com"},
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":null},
             {"email":"V-reference@example.com","displayName":"Vendor reference","managerEmail":"morgan@example.com"},
             {"email":"v-child@example.com","displayName":"Vendor child","managerEmail":"V-reference@example.com"},
             {"email":"archived.reference@example.com","displayName":"Archived reference","managerEmail":null,"archived":true}]}
            """);
        string expected;
        string first;
        string last;
        using (var workflow = OpenSmokeWorkspace(workspace))
        {
            var preview = workflow.PreviewImportFile(import);
            if (!preview.CanApply || preview.Warnings.Count != 16 ||
                preview.Impact is not { PeopleAdded: 0, ExistingPeopleChanged: 1, NoPersonChanges: 4 })
                throw new InvalidOperationException("The native many-diagnostics fixture must contain sixteen real older-evidence warnings and one changed Person.");
            expected = string.Join("\n", preview.Errors.Concat(preview.Warnings).Concat(preview.Messages));
            first = preview.Warnings.First();
            last = preview.Warnings.Last();
        }
        Click(window, "PreviewImport");
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        foreach (var height in new[] { 700, 500 })
        {
            ResizeWindow(window, 700, height);
            var diagnostics = Find(window, "ImportDiagnostics");
            var scroll = (ScrollPattern)diagnostics.GetCurrentPattern(ScrollPattern.Pattern);
            if (!diagnostics.Current.IsKeyboardFocusable || !scroll.Current.VerticallyScrollable ||
                Find(window, "Preview").Current.BoundingRectangle.Height < 72 * DpiScale(window))
                throw new InvalidOperationException("Many diagnostics must be independently keyboard-scrollable without consuming usable Person review space.");
            AssertImportBatchImpact(window, "People added: 0; Existing people changed: 1; No Person changes: 4");
            AssertImportDiagnostic(window, expected);
            foreach (var (key, percent, visible) in new[] { ((byte)0x24, 0d, first), ((byte)0x23, 100d, last) })
            {
                diagnostics.SetFocus();
                keybd_event(0x11, 0, 0, 0);
                try { PressCopyKey(key); }
                finally { keybd_event(0x11, 0, 2, 0); }
                Await(() => scroll.Current.VerticalScrollPercent == percent);
                AssertImportCharacters(window, diagnostics, expected, diagnostics.Current.BoundingRectangle,
                    singleLine: false, visibleText: visible);
            }
            CaptureVisualEvidence(window, "import-many-diagnostics-end-" + height);
            AssertGroupedImportDisclosure(window, "avery@example.com", "1 change",
                ["Office number: 007B → FINAL-0099"], capture: "many-diagnostics-person-" + height);
            AssertImportBatchImpact(window, "People added: 0; Existing people changed: 1; No Person changes: 4");
            AssertImportDiagnostic(window, expected);
        }
    }
}
