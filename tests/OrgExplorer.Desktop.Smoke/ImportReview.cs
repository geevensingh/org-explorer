using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
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
