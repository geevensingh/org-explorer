using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
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
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == ExpandCollapseState.Expanded);
        var list = Find(window, "ImportUnchangedIdentities");
        var entries = list.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (!entries.Cast<AutomationElement>().Select(e => e.Current.Name).SequenceEqual(emails) ||
            entries.Cast<AutomationElement>().Any(e => e.Current.IsOffscreen || e.Current.BoundingRectangle.IsEmpty) ||
            list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)).Count != 0)
            throw new InvalidOperationException("No-Person-changes expansion must render only ordered email identities, without Person/evidence disclosures.");
        if (diagnostic is not null) AssertImportDiagnostic(window, diagnostic);
        CaptureVisualEvidence(window, "import-unchanged-identities");
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
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
        string[] details)
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
            entry.Current.Name != $"{email} — {summary}" ||
            entry.Current.BoundingRectangle.Height > 36)
            throw new InvalidOperationException("Person review must start collapsed on one line with the complete accessible identity and count/Add.");
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
        }
        if (review.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, identity)).Count != 1)
            throw new InvalidOperationException("Expanding a Person duplicated its top-level review entry.");
        CaptureVisualEvidence(window, "import-expanded-" +
            (summary == "Add" ? "addition" : summary.StartsWith("Archive") ? "archive" : "differences"));
        header.SetFocus();
        PressCopyKey(0x20);
        Await(() => disclosure.Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
    }
}
