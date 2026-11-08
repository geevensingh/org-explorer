using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgExplorer.Workflow;

public sealed record CapturedTeamNote(Guid Id, string OwnerEmail, string Title, string Markdown,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid ConfirmedContextRevision, bool NeedsReview, string Version);

public sealed record ReportingTransition(string PersonEmail, ProfileChange Change);

public sealed record HistoricalTeamContext(Guid Id, string PersonEmail, DateTimeOffset CapturedAt,
    IReadOnlyList<PersonProfile> ReportingChain, IReadOnlyList<CapturedTeamNote> TeamNotes,
    IReadOnlyList<ReportingTransition> Transitions)
{
    [JsonIgnore]
    public string DisplayProvenance =>
        $"Historical — captured locally {CapturedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'}.\n" +
        "Team-note text/versions were locally known at processing time, not guaranteed at the unknown actual transfer instant.\n" +
        string.Join("\n\n", Transitions.Select(t =>
            $"{(t.Change.Field == "archived" ? "Archival closure" : "Reporting transition")} for {t.PersonEmail}:\n" +
            new ProfileTimeline([t.Change], [], []).DisplayChanges));
}

public partial interface IApplicationWorkflow
{
    IReadOnlyList<HistoricalTeamContext> GetHistoricalTeamContexts(string email);
}

public sealed partial class ApplicationWorkflow
{
    private Dictionary<string, List<HistoricalTeamContext>> historicalTeamContexts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HistoricalTeamContext> GetHistoricalTeamContexts(string email)
    {
        lock (workspaceGate)
            return historicalTeamContexts.TryGetValue(email, out var contexts)
                ? contexts.Select(c => c with
                {
                    ReportingChain = Array.AsReadOnly(c.ReportingChain.ToArray()),
                    TeamNotes = Array.AsReadOnly(c.TeamNotes.ToArray()),
                    Transitions = Array.AsReadOnly(c.Transitions.ToArray())
                }).ToArray()
                : [];
    }

    private Dictionary<string, List<HistoricalTeamContext>> CaptureReportingHistory(
        Dictionary<string, PersonProfile> proposed, Dictionary<string, PersonHistory> history)
    {
        var next = new Dictionary<string, List<HistoricalTeamContext>>(historicalTeamContexts, StringComparer.OrdinalIgnoreCase);
        if (!people.Values.Any(p => proposed.TryGetValue(p.Email, out var after) &&
            (!p.Archived && after.Archived ||
             !string.Equals(p.ManagerEmail, after.ManagerEmail, StringComparison.OrdinalIgnoreCase))))
            return next;

        foreach (var before in people.Values.Where(p => !p.Archived))
        {
            if (!proposed.TryGetValue(before.Email, out var after)) continue;
            var oldChain = ReportingChain(before, people);
            if (!after.Archived && oldChain.SequenceEqual(ReportingChain(after, proposed), StringComparer.OrdinalIgnoreCase)) continue;

            // Only changed edges of the old chain establish why this context closed.
            // New Manager creation/write order cannot manufacture intermediate contexts.
            var transitions = oldChain.Prepend(before.Email)
                .SelectMany(email => history.TryGetValue(email, out var evidence)
                    ? evidence.Changes.Skip(profileHistory.GetValueOrDefault(email)?.Changes.Count ?? 0)
                        .Where(c => c.Field == "managerEmail" ||
                            c.Field == "archived" && string.Equals(email, before.Email, StringComparison.OrdinalIgnoreCase) && after.Archived)
                        .Select(c => new ReportingTransition(people[email].Email, c))
                    : []).ToArray();
            var notes = oldChain.SelectMany(email => GetTeamNotes(email).Select(note =>
            {
                var frozen = new CapturedTeamNote(note.Id, note.OwnerEmail, note.Title, note.Markdown,
                    note.CreatedAt, note.UpdatedAt, teamNoteConfirmations[note.Id], note.NeedsReview, "");
                return frozen with { Version = CapturedNoteVersion(frozen) };
            })).ToArray();
            var capture = new HistoricalTeamContext(Guid.NewGuid(), before.Email,
                transitions[0].Change.Evidence.RecordedAt, oldChain.Select(email => people[email]).ToArray(),
                notes, transitions);
            next[before.Email] = [.. next.GetValueOrDefault(before.Email) ?? [], capture];
        }
        return next;
    }

    private static string CapturedNoteVersion(CapturedTeamNote note) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            note.Id, note.OwnerEmail, note.Title, note.Markdown, note.CreatedAt, note.UpdatedAt, note.ConfirmedContextRevision
        }, JsonOptions)));

    private sealed record HistoricalContextDocument(int SchemaVersion, string Email, List<HistoricalTeamContext> Contexts);

    private static void WriteHistoricalTeamContexts(string generation, Dictionary<string, List<HistoricalTeamContext>> contexts)
    {
        var directory = Path.Combine(generation, "historical-team-context");
        Directory.CreateDirectory(directory);
        foreach (var (email, captures) in contexts)
            WriteDurably(Path.Combine(directory, HistoricalContextKey(email) + ".json"),
                new HistoricalContextDocument(1, email, captures));
    }

    private static string HistoricalContextKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant()))).ToLowerInvariant();

    private static Dictionary<string, List<HistoricalTeamContext>> ReadHistoricalTeamContexts(string generation,
        Dictionary<string, PersonProfile> population)
    {
        var contexts = new Dictionary<string, List<HistoricalTeamContext>>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var directory = Path.Combine(generation, "historical-team-context");
        if (!Directory.Exists(directory)) return contexts;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file, new UTF8Encoding(false, true)));
            RequireVersion(json.RootElement, "Historical team context", [1]);
            RequireHistoricalProperties(json.RootElement, ["schemaVersion", "email", "contexts"]);
            var document = json.RootElement.Deserialize<HistoricalContextDocument>(JsonOptions)
                ?? throw new FormatException("Invalid Historical team context.");
            if (document.SchemaVersion != 1)
                throw new FormatException("Unsupported Historical team context version. Upgrade the app; writes are blocked.");
            if (document.Email is null || !population.ContainsKey(document.Email) || document.Contexts is null ||
                !Path.GetFileName(file).Equals(HistoricalContextKey(document.Email) + ".json", StringComparison.OrdinalIgnoreCase) ||
                !contexts.TryAdd(document.Email, document.Contexts))
                throw new FormatException("Historical team context has an unknown, duplicate, or conflicting Person.");
            foreach (var capture in document.Contexts) ValidateHistoricalCapture(capture, document.Email, ids);
            foreach (var captureJson in json.RootElement.GetProperty("contexts").EnumerateArray())
            {
                RequireHistoricalProperties(captureJson, ["id", "personEmail", "capturedAt", "reportingChain", "teamNotes", "transitions"]);
                foreach (var person in captureJson.GetProperty("reportingChain").EnumerateArray())
                {
                    CheckProperties(person, ["email", "directoryName", "preferredName", "managerEmail", "archived", "alias",
                        "jobTitle", "department", "officeBuilding", "officeNumber", "officeState", "officeCountry",
                        "linkedInProfileUrl", "isFavorite", "photoPath", "heading"], "Historical chain");
                    foreach (var property in new[] { "email", "directoryName", "managerEmail", "archived" })
                        Required(person, property, "Historical chain");
                }
                foreach (var note in captureJson.GetProperty("teamNotes").EnumerateArray())
                    RequireHistoricalProperties(note, ["id", "ownerEmail", "title", "markdown", "createdAt", "updatedAt",
                        "confirmedContextRevision", "needsReview", "version"]);
                foreach (var transition in captureJson.GetProperty("transitions").EnumerateArray())
                {
                    RequireHistoricalProperties(transition, ["personEmail", "change"]);
                    var change = transition.GetProperty("change");
                    RequireHistoricalProperties(change, ["field", "oldValue", "newValue", "previousEvidence", "evidence"]);
                    RequireHistoricalEvidence(change.GetProperty("evidence"));
                    if (change.GetProperty("previousEvidence") is { ValueKind: not JsonValueKind.Null } prior)
                        RequireHistoricalEvidence(prior);
                }
            }
        }
        return contexts;
    }

    private static void RequireHistoricalProperties(JsonElement value, string[] properties)
    {
        CheckProperties(value, properties, "Historical team context");
        foreach (var property in properties) Required(value, property, "Historical team context");
    }

    private static void RequireHistoricalEvidence(JsonElement value) =>
        RequireHistoricalProperties(value, ["field", "value", "source", "observedAt", "recordedAt", "batchId", "producer"]);

    private static void ValidateHistoricalCapture(HistoricalTeamContext? capture, string email, HashSet<Guid> ids)
    {
        if (capture is null || capture.Id == Guid.Empty || !ids.Add(capture.Id) ||
            !string.Equals(capture.PersonEmail, email, StringComparison.OrdinalIgnoreCase) ||
            capture.CapturedAt == default || capture.CapturedAt.Offset != TimeSpan.Zero ||
            capture.ReportingChain is null || capture.TeamNotes is null || capture.Transitions is null ||
            capture.Transitions.Count == 0)
            throw new FormatException("Historical team context has invalid identity, dates, chain, notes, or transitions.");

        var chain = new Dictionary<string, PersonProfile>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < capture.ReportingChain.Count; index++)
        {
            var person = capture.ReportingChain[index];
            if (person is null || person.Archived ||
                string.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase) ||
                !chain.TryAdd(Email(JsonSerializer.SerializeToElement(person.Email), "Historical chain.email"), person) ||
                !string.Equals(person.ManagerEmail, index + 1 < capture.ReportingChain.Count
                    ? capture.ReportingChain[index + 1]?.Email : null, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Historical team context contains an incoherent former Reporting chain.");
            foreach (var (field, property, _) in TextFields)
                ValidateText(JsonSerializer.SerializeToElement(ReadText(person, field)), field, $"Historical chain.{property}");
        }
        var noteIds = new HashSet<Guid>();
        foreach (var note in capture.TeamNotes)
            if (note is null || note.Id == Guid.Empty || !noteIds.Add(note.Id) || note.OwnerEmail is null ||
                !chain.ContainsKey(note.OwnerEmail) || string.IsNullOrWhiteSpace(note.Title) || note.Markdown is null ||
                note.CreatedAt == default || note.UpdatedAt == default || note.CreatedAt > note.UpdatedAt ||
                note.CreatedAt.Offset != TimeSpan.Zero || note.UpdatedAt.Offset != TimeSpan.Zero ||
                note.Version != CapturedNoteVersion(note))
                throw new FormatException("Historical team context contains invalid attributed Team-note text or versions.");

        var changedEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in capture.Transitions)
        {
            if (transition is null || transition.PersonEmail is null ||
                transition.Change is not { Field: "managerEmail" or "archived" } change ||
                !changedEdges.Add(transition.PersonEmail + ":" + change.Field) ||
                change.Field == "archived" && !string.Equals(transition.PersonEmail, email, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(transition.PersonEmail, email, StringComparison.OrdinalIgnoreCase) &&
                !chain.ContainsKey(transition.PersonEmail))
                throw new FormatException("Historical team context contains invalid causal reporting or archival evidence.");
            ValidateEvidence(change.Evidence);
            if (change.PreviousEvidence is { } prior) ValidateEvidence(prior);
            ValidateHistoryValue(change.Field, change.OldValue);
            ValidateHistoryValue(change.Field, change.NewValue);
            var oldManager = string.Equals(transition.PersonEmail, email, StringComparison.OrdinalIgnoreCase)
                ? capture.ReportingChain.FirstOrDefault()?.Email : chain[transition.PersonEmail].ManagerEmail;
            var expectedOld = change.Field == "archived" ? JsonSerializer.SerializeToElement(false) :
                JsonSerializer.SerializeToElement(oldManager);
            if (change.Evidence.Field != change.Field || change.Evidence.RecordedAt != capture.CapturedAt ||
                !SameProfileValue(change.Field, change.OldValue, expectedOld) ||
                change.Field == "archived" && !change.NewValue.GetBoolean() ||
                !SameProfileValue(change.Field, change.NewValue, change.Evidence.Value) ||
                SameProfileValue(change.Field, change.OldValue, change.NewValue) ||
                change.PreviousEvidence is { } previous && (previous.Field != change.Field ||
                    !SameProfileValue(change.Field, previous.Value, change.OldValue)))
                throw new FormatException("Historical team context evidence does not match the former chain or archival closure.");
        }
    }
}
