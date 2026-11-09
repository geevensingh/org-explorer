using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrgExplorer.Workflow;

public sealed partial class ApplicationWorkflow
{
    private static void ValidateImportVersion(JsonElement root, string scope, bool workspaceSource = false)
    {
        var version = Required(root, "schemaVersion", scope);
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetDecimal(out var number) || number != 1)
        {
            if (workspaceSource && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var unsupported))
                throw new UnsupportedWorkspaceVersion("accepted batch source", unsupported);
            throw new FormatException("schemaVersion: unsupported import version; upgrade the app.");
        }
    }

    private static (string BatchId, DateTimeOffset ObservedAt, JsonElement Records) ValidateImportEnvelope(
        JsonElement root, string scope, bool workspaceSource = false)
    {
        ValidateImportVersion(root, scope, workspaceSource);
        CheckProperties(root, ["schemaVersion", "batchId", "observedAt", "producer", "people"], scope);
        var batchId = Text(Required(root, "batchId", scope), $"{scope}.batchId", false)!;
        if (!Guid.TryParseExact(batchId, "D", out var parsedId))
            throw new FormatException($"{scope}.batchId: expected UUID.");
        var observed = Text(Required(root, "observedAt", scope), $"{scope}.observedAt", false)!;
        if (!Regex.IsMatch(observed, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$") ||
            !DateTimeOffset.TryParseExact(observed, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observedAt))
            throw new FormatException($"{scope}.observedAt: expected a real UTC calendar timestamp.");
        if (root.TryGetProperty("producer", out var producer)) Text(producer, $"{scope}.producer", false);
        var records = Required(root, "people", scope);
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
            throw new FormatException($"{scope}.people: expected a nonempty array.");
        return (parsedId.ToString(), observedAt, records);
    }

    private static string ValidateImportRecord(JsonElement record, string scope)
    {
        var email = Email(Required(record, "email", scope), $"{scope}.email");
        CheckProperties(record, ["email", "displayName", "preferredName", "alias", "jobTitle", "department",
            "officeBuilding", "officeNumber", "officeState", "officeCountry", "linkedInProfileUrl",
            "managerEmail", "archived", "isFavorite", "photoPath"], email);
        foreach (var (field, property, _) in TextFields)
            if (record.TryGetProperty(property, out var text)) ValidateText(text, field, $"{email}.{property}");
        if (record.TryGetProperty("managerEmail", out var manager) && manager.ValueKind != JsonValueKind.Null)
            Email(manager, $"{email}.managerEmail");
        if (record.TryGetProperty("archived", out var archived)) Boolean(archived, $"{email}.archived");
        if (record.TryGetProperty("isFavorite", out var favorite)) Boolean(favorite, $"{email}.isFavorite");
        if (record.TryGetProperty("photoPath", out var photo) && photo.ValueKind != JsonValueKind.Null)
        {
            var relative = Text(photo, $"{email}.photoPath", false)!;
            if (Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split(['\\', '/']).Any(segment => segment == ".."))
                throw new FormatException($"{email}.photoPath: expected a safe local relative source path.");
        }
        return email;
    }
}
