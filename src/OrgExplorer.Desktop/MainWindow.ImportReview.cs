using System.Windows;
using System.Windows.Automation;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop;

internal sealed class ImportReviewEntry(ImportPersonPreview person)
{
    public string Email => person.Email;
    public string AutomationId => "ImportPerson:" + Email;
    public bool IsExpanded { get; set; }
    public string Summary => person.IsNew ? "Add" :
        string.Join("; ", person.Changes.Where(c => c.Kind is ImportChangeKind.Archive or ImportChangeKind.Reactivate)
            .Select(c => c.Label).Append($"{person.Changes.Count} {(person.Changes.Count == 1 ? "change" : "changes")}"));
    public string AccessibleSummary => $"{Email} — {Summary}";
    public string DetailHeading => person.IsNew ? "Supplied initial values" : "Changes";
    public IReadOnlyList<string> Details { get; } = person.IsNew
        ? person.InitialValues.Select(v => $"{v.Label}: {v.Value ?? "(none)"}").ToArray()
        : person.Changes.Select(c => c.Kind == ImportChangeKind.Photo
            ? $"Photo: {(c.After is null ? "Clear photo" : "Replace with supplied local image")}"
            : $"{c.Label}: {c.Before ?? "(none)"} → {c.After ?? "(clear)"}").ToArray();
}

internal sealed class ImportUnchangedSection(IReadOnlyList<string> emails)
{
    public IReadOnlyList<string> Emails => emails;
    public string Summary => $"No Person changes: {emails.Count}";
    public bool IsExpanded { get; set; }
}

public partial class MainWindow
{
    private void ShowImportReview(ImportPreview result)
    {
        var diagnostics = result.Errors.Concat(result.Warnings).Concat(result.Messages).ToArray();
        ImportDiagnostics.Text = string.Join("\n", diagnostics);
        ImportDiagnostics.Visibility = diagnostics.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ImportDiagnostics, ImportDiagnostics.Text);
        var impact = result.Impact;
        ImportImpactText.Visibility = ImportActionsText.Visibility = ImportObservationText.Visibility = Visibility.Collapsed;
        ImportImpactText.Text = ImportActionsText.Text = ImportObservationText.Text = "";
        var entries = new List<object>();
        if (impact is not null)
        {
            ImportImpactText.Text = $"People added: {impact.PeopleAdded}; Existing people changed: {impact.ExistingPeopleChanged}; No Person changes: {impact.NoPersonChanges}";
            ImportImpactText.Visibility = ImportObservationText.Visibility = Visibility.Visible;
            ImportObservationText.Text = (impact.PeopleAdded == 0 && impact.ExistingPeopleChanged == 0 ? "Zero Person changes. " : "") +
                "Omitted fields and people stay unchanged. Supplied Observations may still be recorded without Person differences; email-only records supply no field Observation.";
            var actions = new[]
            {
                ("Archived", impact.Archived), ("Reactivated", impact.Reactivated),
                ("Manager changed", impact.ManagerChanged), ("Photo changed", impact.PhotoChanged),
                ("Favorite changed", impact.FavoriteChanged)
            }.Where(action => action.Item2 > 0).Select(action => $"{action.Item1}: {action.Item2}").ToArray();
            if (actions.Length > 0)
            {
                ImportActionsText.Text = "Overlapping actions for existing people — " + string.Join("; ", actions);
                ImportActionsText.Visibility = Visibility.Visible;
            }
            entries.AddRange(result.People.Where(p => p.IsNew || p.Changes.Count > 0).Select(p => new ImportReviewEntry(p)));
            var unchanged = result.People.Where(p => !p.IsNew && p.Changes.Count == 0).Select(p => p.Email).ToArray();
            if (unchanged.Length > 0) entries.Add(new ImportUnchangedSection(unchanged));
        }
        PreviewText.ItemsSource = entries;
        AutomationProperties.SetName(PreviewText, string.Join("\n",
            diagnostics.Concat(new[] { ImportImpactText.Text, ImportActionsText.Text, ImportObservationText.Text })
                .Concat(entries.OfType<ImportReviewEntry>().SelectMany(e => new[] { e.AccessibleSummary }.Concat(e.Details)))
                .Concat(entries.OfType<ImportUnchangedSection>().SelectMany(e => e.Emails))));
    }
}
