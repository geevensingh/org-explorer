using System.Text.Json;

namespace OrgExplorer.Workflow;

public enum PersonTextField
{
    DirectoryName, PreferredName, Alias, JobTitle, Department,
    OfficeBuilding, OfficeNumber, OfficeState, OfficeCountry, LinkedInProfileUrl
}

public interface IProfileLinkOpener
{
    void Open(Uri profileUrl);
}

public sealed partial class ApplicationWorkflow
{
    private readonly IProfileLinkOpener? profileLinkOpener;

    public bool OpenLinkedInProfile(string email)
    { lock (workspaceGate) return OpenLinkedInProfileCore(email); }

    private bool OpenLinkedInProfileCore(string email)
    {
        if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
        if (person.LinkedInProfileUrl is null) return Fail("No LinkedIn URL is stored for this Person.");
        try
        {
            var validated = ValidateText(JsonSerializer.SerializeToElement(person.LinkedInProfileUrl),
                PersonTextField.LinkedInProfileUrl, $"{person.Email}.linkedInProfileUrl");
            if (profileLinkOpener is null) return Fail("Browser opening is unavailable.");
            profileLinkOpener.Open(new Uri(validated!, UriKind.Absolute));
            Status = "Opened LinkedIn in your browser.";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or
                                   UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return Fail("LinkedIn could not be opened: " + ex.Message); }
    }

    public bool EditPersonText(string email, PersonTextField field, string? value)
    { lock (workspaceGate) return EditPersonTextCore(email, field, value); }

    private bool EditPersonTextCore(string email, PersonTextField field, string? value)
    {
        if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
        try
        {
            if (!Enum.IsDefined(field)) throw new FormatException("Choose a supported textual Person field; email is not editable.");
            var property = TextFields.Single(f => f.Field == field).Property;
            var validated = ValidateText(JsonSerializer.SerializeToElement(value), field, $"{person.Email}.{property}");
            var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase)
            { [person.Email] = WithText(person, field, validated) };
            var history = CaptureLocalHistory(person, proposed[person.Email], property);
            if (!Save(proposed, batches, history: history, operation: $"profile:{email.ToLowerInvariant()}:{field}")) return false;
            people = proposed;
            profileHistory = history;
            InvalidateProjections();
            pending = null;
            Status = "Saved locally.";
            return true;
        }
        catch (FormatException ex)
        {
            return SaveFailed($"profile:{email.ToLowerInvariant()}:{field}",
                "Edit rejected; changes are NOT saved locally: " + ex.Message);
        }
    }

    private static readonly (PersonTextField Field, string Property, string Label)[] TextFields =
    [
        (PersonTextField.DirectoryName, "displayName", "Directory name"),
        (PersonTextField.PreferredName, "preferredName", "Preferred name"),
        (PersonTextField.Alias, "alias", "Alias"),
        (PersonTextField.JobTitle, "jobTitle", "Job title"),
        (PersonTextField.Department, "department", "Department"),
        (PersonTextField.OfficeBuilding, "officeBuilding", "Office building"),
        (PersonTextField.OfficeNumber, "officeNumber", "Office number"),
        (PersonTextField.OfficeState, "officeState", "Office state"),
        (PersonTextField.OfficeCountry, "officeCountry", "Office country"),
        (PersonTextField.LinkedInProfileUrl, "linkedInProfileUrl", "LinkedIn URL")
    ];

    private static string? ValidateText(JsonElement value, PersonTextField field, string scope)
    {
        var text = Text(value, scope);
        if (text is not null && field == PersonTextField.LinkedInProfileUrl &&
            (!text.StartsWith("https://", StringComparison.Ordinal) || text.Any(char.IsWhiteSpace) ||
             !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
             uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)))
            throw new FormatException($"{scope}: expected an absolute HTTPS URL or null; use https:// or clear the link.");
        return text;
    }

    private static string? ReadText(PersonProfile person, PersonTextField field) => field switch
    {
        PersonTextField.DirectoryName => person.DirectoryName,
        PersonTextField.PreferredName => person.PreferredName,
        PersonTextField.Alias => person.Alias,
        PersonTextField.JobTitle => person.JobTitle,
        PersonTextField.Department => person.Department,
        PersonTextField.OfficeBuilding => person.OfficeBuilding,
        PersonTextField.OfficeNumber => person.OfficeNumber,
        PersonTextField.OfficeState => person.OfficeState,
        PersonTextField.OfficeCountry => person.OfficeCountry,
        PersonTextField.LinkedInProfileUrl => person.LinkedInProfileUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static PersonProfile WithText(PersonProfile person, PersonTextField field, string? value) => field switch
    {
        PersonTextField.DirectoryName => person with { DirectoryName = value },
        PersonTextField.PreferredName => person with { PreferredName = value },
        PersonTextField.Alias => person with { Alias = value },
        PersonTextField.JobTitle => person with { JobTitle = value },
        PersonTextField.Department => person with { Department = value },
        PersonTextField.OfficeBuilding => person with { OfficeBuilding = value },
        PersonTextField.OfficeNumber => person with { OfficeNumber = value },
        PersonTextField.OfficeState => person with { OfficeState = value },
        PersonTextField.OfficeCountry => person with { OfficeCountry = value },
        PersonTextField.LinkedInProfileUrl => person with { LinkedInProfileUrl = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
