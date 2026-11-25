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

public partial class MainWindow
{
    private void ShowImportReview(ImportPreview result)
    {
        var entries = new List<object>();
        entries.AddRange(result.Errors);
        entries.AddRange(result.Warnings);
        entries.AddRange(result.Messages);
        foreach (var person in result.People)
        {
            if (person.IsNew || person.Changes.Count > 0) entries.Add(new ImportReviewEntry(person));
            else entries.Add($"{person.Email}: No explicit field changes; omitted values and people stay unchanged.");
        }
        PreviewText.ItemsSource = entries;
        AutomationProperties.SetName(PreviewText, string.Join("\n",
            result.Errors.Concat(result.Warnings).Concat(result.Changes)));
    }
}
