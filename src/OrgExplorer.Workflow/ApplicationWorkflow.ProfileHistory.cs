using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace OrgExplorer.Workflow;

public enum ProfileChangeSource { Import, LocalEdit }

public sealed record FieldEvidence(string Field, JsonElement Value, ProfileChangeSource Source,
    DateTimeOffset? ObservedAt, [property: JsonRequired] DateTimeOffset RecordedAt, Guid? BatchId, string? Producer);

public sealed record ProfileChange(string Field, JsonElement OldValue, JsonElement NewValue,
    FieldEvidence? PreviousEvidence, FieldEvidence Evidence)
{
    [JsonIgnore]
    public ObservedReportingInterval? ReportingInterval => Field == "managerEmail" &&
        PreviousEvidence?.ObservedAt is { } after && Evidence.ObservedAt is { } by && by > after
            ? new ObservedReportingInterval(after, by) : null;
    [JsonIgnore]
    public DateTimeOffset DetectedAt => Evidence.RecordedAt;
    [JsonIgnore]
    public bool IsStaleEvidence => Evidence.ObservedAt is { } observed && PreviousEvidence is { } prior &&
        observed < (prior.ObservedAt ?? prior.RecordedAt);
}

public sealed record ObservedReportingInterval(DateTimeOffset After, DateTimeOffset By);

public sealed record ProfileTimeline(IReadOnlyList<ProfileChange> Changes,
    IReadOnlyList<FieldEvidence> Confirmations, IReadOnlyList<FieldEvidence> Observations)
{
    public string DisplayChanges => Changes.Count == 0
        ? "No recorded Profile changes. Initial import is a baseline; pre-import history is unknown."
        : string.Join("\n\n", Changes.Reverse().Select(c =>
            $"{ApplicationWorkflow.ProfileFieldLabel(c.Field)}: {DisplayValue(c.OldValue, "(none)")} → {DisplayValue(c.NewValue, "(clear)")}\n" +
            DisplayEvidence(c.Evidence) +
            (c.PreviousEvidence is { } prior ? "\nPrior evidence: " + DisplayEvidence(prior) : "\nPrior evidence unknown.") +
            (c.IsStaleEvidence ? "\nOlder evidence reapplied; do not infer a backwards interval." : "") +
            (c.Field == "managerEmail" ? c.ReportingInterval is { } interval
                ? $"\nObserved interval: after {Utc(interval.After)}, by {Utc(interval.By)}; exact effective date unknown."
                : $"\nDetected {Utc(c.DetectedAt)}; effective date unknown."
                : c.Field == "archived" ? $"\nDetected {Utc(c.DetectedAt)}; effective date unknown." : "")));
    public string DisplayConfirmations => Confirmations.Count == 0 ? "No recorded field confirmations." :
        string.Join("\n", Confirmations.OrderBy(o => o.Field).Select(o =>
            $"{ApplicationWorkflow.ProfileFieldLabel(o.Field)}: {DisplayValue(o.Value, "(clear)")} — {DisplayEvidence(o)}"));
    public string DisplayObservations => Observations.Count == 0 ? "No recorded evidence." :
        string.Join("\n", Observations.Reverse().Select(o =>
            $"{ApplicationWorkflow.ProfileFieldLabel(o.Field)}: {DisplayValue(o.Value, "(clear)")} — {DisplayEvidence(o)}"));

    private static string DisplayEvidence(FieldEvidence evidence) => evidence.Source == ProfileChangeSource.Import
        ? $"Imported; Observation {Utc(evidence.ObservedAt!.Value)}; Recorded {Utc(evidence.RecordedAt)}; " +
          $"batch {evidence.BatchId}" + (evidence.Producer is { } producer ? $"; producer {producer}" : "")
        : $"Local edit; Recorded {Utc(evidence.RecordedAt)}; no source Observation";
    private static string DisplayValue(JsonElement value, string empty) => value.ValueKind == JsonValueKind.Null ? empty :
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
}

public sealed partial class ApplicationWorkflow
{
    private readonly TimeProvider timeProvider;
    private Dictionary<string, PersonHistory> profileHistory = new(StringComparer.OrdinalIgnoreCase);
    private sealed record PersonHistory(string Email, List<ProfileChange> Changes, List<FieldEvidence> Observations);

    public ProfileTimeline? GetProfileTimeline(string email)
    { if (queryView is { } view) return view.GetProfileTimeline(email);
      lock (workspaceGate) return GetProfileTimelineCore(email); }

    private ProfileTimeline? GetProfileTimelineCore(string email)
    {
        if (!people.ContainsKey(email)) return null;
        if (!profileHistory.TryGetValue(email, out var history)) return new ProfileTimeline([], [], []);
        return new ProfileTimeline(history.Changes.ToArray(),
            CurrentEvidence(history).Values.ToArray(), history.Observations.ToArray());
    }

    private Dictionary<string, PersonHistory> CaptureImportHistory(JsonElement source,
        Dictionary<string, PersonProfile> proposed, DateTimeOffset recordedAt)
    {
        var history = new Dictionary<string, PersonHistory>(profileHistory, StringComparer.OrdinalIgnoreCase);
        var observedAt = DateTimeOffset.Parse(source.GetProperty("observedAt").GetString()!, CultureInfo.InvariantCulture);
        var batchId = source.GetProperty("batchId").GetGuid();
        var producer = source.TryGetProperty("producer", out var value) ? value.GetString() : null;
        foreach (var record in source.GetProperty("people").EnumerateArray())
        {
            var email = record.GetProperty("email").GetString()!;
            var existing = people.GetValueOrDefault(email);
            foreach (var field in record.EnumerateObject().Select(p => p.Name).Where(f => f != "email"))
                AddEvidence(history, proposed[email].Email, existing is null ? null : ProfileValue(existing, field),
                    new FieldEvidence(field, ProfileValue(proposed[email], field), ProfileChangeSource.Import,
                        observedAt, recordedAt, batchId, producer), existing is not null);
        }
        return history;
    }

    private static void AddEvidence(Dictionary<string, PersonHistory> history, string email, JsonElement? oldValue,
        FieldEvidence evidence, bool existing)
    {
        var prior = history.GetValueOrDefault(email) ?? new PersonHistory(email, [], []);
        var previousEvidence = CurrentEvidence(prior).GetValueOrDefault(evidence.Field);
        var changes = new List<ProfileChange>(prior.Changes);
        if (existing && !SameProfileValue(evidence.Field, oldValue!.Value, evidence.Value))
            changes.Add(new ProfileChange(evidence.Field, oldValue.Value, evidence.Value, previousEvidence, evidence));
        history[email] = prior with { Changes = changes, Observations = [.. prior.Observations, evidence] };
    }

    private Dictionary<string, PersonHistory> CaptureLocalHistory(PersonProfile before, PersonProfile after, string field)
    {
        var history = new Dictionary<string, PersonHistory>(profileHistory, StringComparer.OrdinalIgnoreCase);
        var oldValue = ProfileValue(before, field);
        var newValue = ProfileValue(after, field);
        if (!SameProfileValue(field, oldValue, newValue))
            AddEvidence(history, before.Email, oldValue, new FieldEvidence(field, newValue,
                ProfileChangeSource.LocalEdit, null, timeProvider.GetUtcNow(), null, null), true);
        return history;
    }

    private static Dictionary<string, FieldEvidence> CurrentEvidence(PersonHistory history)
    {
        var current = new Dictionary<string, FieldEvidence>();
        foreach (var observation in history.Observations)
        {
            if (current.TryGetValue(observation.Field, out var prior) &&
                SameProfileValue(observation.Field, prior.Value, observation.Value) &&
                observation.ObservedAt is { } observed && observed < (prior.ObservedAt ?? prior.RecordedAt))
                continue;
            current[observation.Field] = observation;
        }
        return current;
    }

    private void AddOlderEvidenceWarnings(string email, JsonElement record, DateTimeOffset observedAt, List<string> warnings)
    {
        if (!profileHistory.TryGetValue(email, out var history)) return;
        var current = CurrentEvidence(history);
        foreach (var field in record.EnumerateObject().Select(p => p.Name))
            if (current.TryGetValue(field, out var prior) && observedAt < (prior.ObservedAt ?? prior.RecordedAt))
                warnings.Add($"{email}.{field}: older evidence observed {observedAt:O} predates " +
                    $"{(prior.ObservedAt is null ? "local recording" : "last confirmation")} {(prior.ObservedAt ?? prior.RecordedAt):O}. " +
                    "Review before Apply; this does not establish an exact effective date.");
    }

    private void WriteProfileHistory(string generation, Dictionary<string, PersonHistory> history,
        Dictionary<string, PersonHistory> previous, string? sourceGeneration, IReadOnlyDictionary<string, byte[]> sourceBytes,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> published)
    {
        var directory = Path.Combine(generation, "profile-history");
        Directory.CreateDirectory(directory);
        RunBoundedFileIo(history.Values, person =>
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(person.Email.ToLowerInvariant()))).ToLowerInvariant();
            var target = Path.Combine(directory, key + ".json");
            var source = sourceGeneration is null ? null : Path.Combine(sourceGeneration, "profile-history", key + ".json");
            if (ReferenceEquals(previous.GetValueOrDefault(person.Email), person) &&
                source is not null && sourceBytes.TryGetValue(source, out var bytes))
                WriteDurablyBytes(target, bytes, published);
            else WriteVersionedRecord(target, person, published);
        });
    }

    private static Dictionary<string, PersonHistory> ReadProfileHistory(string generation,
        Dictionary<string, PersonProfile> people, int workspaceVersion)
    {
        var history = new Dictionary<string, PersonHistory>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(generation, "profile-history");
        if (!Directory.Exists(directory)) return history;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(ReadWorkspaceText(file));
            CheckRecordVersion(document.RootElement, "Profile history", workspaceVersion);
            CheckProperties(document.RootElement, ["schemaVersion", "email", "changes", "observations"], "Profile history");
            var person = DeserializeVersionedRecord<PersonHistory>(document.RootElement)
                ?? throw new FormatException("Invalid Profile history.");
            if (person.Email is null || !people.ContainsKey(person.Email) || !history.TryAdd(person.Email, person))
                throw new FormatException("Profile history has an unknown or duplicate Person.");
            ValidateProfileHistory(person, people[person.Email]);
        }
        return history;
    }

    private static void ValidateProfileHistory(PersonHistory history, PersonProfile person)
    {
        if (history.Changes is null || history.Observations is null)
            throw new FormatException("Profile history requires changes and observations arrays.");
        foreach (var evidence in history.Observations) ValidateEvidence(evidence);
        foreach (var change in history.Changes)
        {
            if (change is null) throw new FormatException("Profile history contains an invalid change.");
            ValidateEvidence(change.Evidence);
            if (change.PreviousEvidence is { } prior) ValidateEvidence(prior);
            ValidateHistoryValue(change.Field, change.OldValue);
            if (change.Field != change.Evidence.Field ||
                !SameProfileValue(change.Field, change.NewValue, change.Evidence.Value) ||
                SameProfileValue(change.Field, change.OldValue, change.NewValue) ||
                change.PreviousEvidence is { } previous && (previous.Field != change.Field ||
                    !SameProfileValue(change.Field, previous.Value, change.OldValue)))
                throw new FormatException("Profile history change values do not match their evidence.");
        }
        foreach (var evidence in CurrentEvidence(history).Values)
            if (!SameProfileValue(evidence.Field, evidence.Value, ProfileValue(person, evidence.Field)))
                throw new FormatException($"Profile history {person.Email}.{evidence.Field} does not match the current Profile.");
    }

    private static void ValidateEvidence(FieldEvidence? evidence)
    {
        if (evidence is null || !Enum.IsDefined(evidence.Source) || evidence.RecordedAt.Offset != TimeSpan.Zero ||
            evidence.Source == ProfileChangeSource.Import && (evidence.ObservedAt is null || evidence.BatchId is null) ||
            evidence.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero ||
            evidence.Source == ProfileChangeSource.LocalEdit &&
            (evidence.ObservedAt is not null || evidence.BatchId is not null || evidence.Producer is not null))
            throw new FormatException("Profile history has invalid source dates or provenance.");
        ValidateHistoryValue(evidence.Field, evidence.Value);
    }

    private static void ValidateHistoryValue(string field, JsonElement value)
    {
        if (field == "managerEmail")
        {
            if (value.ValueKind != JsonValueKind.Null) Email(value, "Profile history.managerEmail");
        }
        else if (field is "archived" or "isFavorite") Boolean(value, $"Profile history.{field}");
        else if (field == "photoPath")
        {
            if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String ||
                !System.Text.RegularExpressions.Regex.IsMatch(value.GetString()!, @"^[a-f0-9]{64}\.(png|jpg)$")))
                throw new FormatException("Profile history.photoPath: expected a Workspace photo content reference or null.");
        }
        else if (TextFields.Any(f => f.Property == field))
            ValidateText(value, TextFields.Single(f => f.Property == field).Field, $"Profile history.{field}");
        else throw new FormatException($"Profile history.{field}: unsupported structured field.");
    }

    private static bool SameProfileValue(string field, JsonElement left, JsonElement right) =>
        field == "managerEmail" && left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String
            ? string.Equals(left.GetString(), right.GetString(), StringComparison.OrdinalIgnoreCase)
            : JsonElement.DeepEquals(left, right);

    private static JsonElement ProfileValue(PersonProfile person, string property)
    {
        if (property == "managerEmail") return JsonSerializer.SerializeToElement(person.ManagerEmail);
        if (property == "archived") return JsonSerializer.SerializeToElement(person.Archived);
        if (property == "isFavorite") return JsonSerializer.SerializeToElement(person.IsFavorite);
        if (property == "photoPath") return JsonSerializer.SerializeToElement(person.PhotoPath);
        return JsonSerializer.SerializeToElement(ReadText(person, TextFields.Single(f => f.Property == property).Field));
    }

    internal static string ProfileFieldLabel(string field) => field switch
    {
        "managerEmail" => "Manager",
        "archived" => "Archived",
        "isFavorite" => "Favorite",
        "photoPath" => "Photo",
        _ => TextFields.FirstOrDefault(f => f.Property == field).Label ?? field
    };
}
