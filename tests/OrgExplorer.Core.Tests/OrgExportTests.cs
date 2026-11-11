using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.OrgExport;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Core.Tests;

[Trait("Category", "Filesystem")]
public sealed class OrgExportTests : IDisposable
{
    private const string RootId = "10000000-0000-0000-0000-000000000001";
    private const string ChildId = "10000000-0000-0000-0000-000000000002";
    private const string AltId = "10000000-0000-0000-0000-000000000003";
    private const string AccountId = "20000000-0000-0000-0000-000000000001";
    private readonly string root = Path.Combine(Path.GetTempPath(), "OrgExportTests-" + Guid.NewGuid());
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Reports_require_every_page_and_leaf_and_keep_distinct_same_named_people()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        const string next = "/users/" + RootId + "/directReports?$skiptoken=a%2Bb%2F";
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject
            {
                ["value"] = new JsonArray(User(ChildId)), ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0" + next
            })));
        var pending = session.Plan().Pending;
        Assert.Contains(next, pending);
        Assert.Contains(ExportSession.ReportsPath(ChildId), pending);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
        Ingest(session,
            (next, Ok(new JsonObject { ["value"] = new JsonArray(User(AltId, "Example (NON EA SC ALT)")) })),
            (ExportSession.ReportsPath(ChildId), Empty()),
            (ExportSession.ReportsPath(AltId), Empty()));
        var complete = session.Plan();
        Assert.Empty(complete.Pending);
        Assert.Empty(complete.Errors);
        Assert.Equal(2, complete.People.Length);
        Assert.Single(complete.Excluded);
        Assert.Equal(2, complete.People.Select(p => p.Email).Distinct().Count());
        Assert.Single(complete.People.Select(p => p.Name).Distinct());
    }

    [Fact]
    public void Unresolved_source_errors_do_not_publish_or_masquerade_as_no_photo()
    {
        var path = Seed();
        using var session = ExportSession.Open(path, publishing: true);
        RecordProfile(session);
        Ingest(session, (ExportSession.PhotoPath(RootId), new JsonObject
        {
            ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ErrorStoreServerUnavailable" }
        }));
        Assert.Throws<InvalidOperationException>(() => session.Publish());
        Assert.True(Directory.Exists(path));
        Assert.Single(Directory.GetDirectories(root));
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("OtherImageNotFoundError")));
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Theory]
    [InlineData("HOME OFFICE")]
    [InlineData("NO WORKSPACE")]
    public void Refresh_clears_explicitly_absent_offices_but_omits_unknown_fields_and_photos(string location)
    {
        var path = Seed(location);
        object result;
        using (var session = ExportSession.Open(path, publishing: true))
        {
            RecordProfile(session);
            Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
            result = session.Publish();
        }
        var jsonPath = JsonSerializer.SerializeToNode(result)!.AsObject()["importPath"]!.GetValue<string>();
        var batch = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
        var person = batch["people"]!.AsArray().Single()!.AsObject();
        Assert.True(person.ContainsKey("officeBuilding"));
        Assert.Null(person["officeBuilding"]);
        Assert.True(person.ContainsKey("officeNumber"));
        Assert.Null(person["officeNumber"]);
        Assert.False(person.ContainsKey("officeState"));
        Assert.False(person.ContainsKey("officeCountry"));
        Assert.False(person.ContainsKey("photoPath"));
        Assert.False(person.ContainsKey("archived"));
        Assert.False(person.ContainsKey("preferredName"));
        Assert.False(person.ContainsKey("isFavorite"));
        Assert.Throws<InvalidOperationException>(() => ExportSession.Open(Path.GetDirectoryName(jsonPath)!));

        using var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious-org-refresh"));
        var prior = Path.Combine(root, "prior.json");
        Save(prior, new
        {
            schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-01-01T00:00:00Z",
            people = new object[]
            {
                new { email = "person1@example.test", displayName = "Example Person",
                    managerEmail = (string?)null, officeBuilding = "OLD", officeNumber = "123", officeState = "Saved state" },
                new { email = "untouched@example.test", displayName = "Outside this reporting subtree", managerEmail = (string?)null }
            }
        });
        Assert.True(app.ApplyImport(app.PreviewImportFile(prior).Id));
        Assert.True(app.ApplyImport(app.PreviewImportFile(jsonPath).Id));
        Assert.Equal(2, app.People.Count);
        var refreshed = app.People.Single(p => p.Email == "person1@example.test");
        Assert.Null(refreshed.OfficeBuilding);
        Assert.Null(refreshed.OfficeNumber);
        Assert.Equal("Saved state", refreshed.OfficeState);
    }

    [Fact]
    public void Sourced_clears_and_country_normalization_survive_directory_fallbacks()
    {
        var path = Seed();
        using var session = ExportSession.Open(path, publishing: true);
        var evidence = Path.Combine(root, "sourced-fields.json");
        Save(evidence, new { directoryId = RootId, workPosition = new { country = "United States of America", roomRemoved = true } });
        var fields = UnknownFields();
        fields["officeCountry"] = new("value", "United States of America", "Own work-position country.");
        fields["officeNumber"] = new("clear", null, "Explicit sourced removal supersedes the older directory room.");
        var input = Path.Combine(root, "sourced-observation.json");
        Save(input, new ExportSession.Observation(RootId, "person1@example.test", fields,
            [evidence], [], null, DateTimeOffset.UtcNow));
        session.Profile(RootId, input);
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        var jsonPath = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        var person = JsonNode.Parse(File.ReadAllText(jsonPath))!["people"]!.AsArray().Single()!.AsObject();
        Assert.Equal("USA", person["officeCountry"]!.GetValue<string>());
        Assert.True(person.ContainsKey("officeNumber"));
        Assert.Null(person["officeNumber"]);
    }

    [Theory]
    [InlineData("HOME OFFICE")]
    [InlineData("NO WORKSPACE")]
    public void Sourced_office_values_override_conflicting_directory_sentinel_clears(string location)
    {
        var path = Seed(location);
        using var session = ExportSession.Open(path, publishing: true);
        var evidence = Path.Combine(root, "sourced-office.json");
        Save(evidence, new { directoryId = RootId, workPosition = new { officeLocation = "NEW SITE/001.2" } });
        var fields = UnknownFields();
        fields["officeBuilding"] = new("value", "NEW SITE", "Own work-position building.");
        fields["officeNumber"] = new("value", "001.2", "Own work-position office number.");
        var input = Path.Combine(root, "sourced-office-observation.json");
        Save(input, new ExportSession.Observation(RootId, "person1@example.test", fields,
            [evidence], [], null, DateTimeOffset.UtcNow));
        session.Profile(RootId, input);
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        var jsonPath = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        var person = JsonNode.Parse(File.ReadAllText(jsonPath))!["people"]!.AsArray().Single()!.AsObject();
        Assert.Equal("NEW SITE", person["officeBuilding"]?.GetValue<string>());
        Assert.Equal("001.2", person["officeNumber"]?.GetValue<string>());
        var warnings = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(jsonPath)!, "review-required.json")))!.AsArray();
        Assert.Contains(warnings, warning => warning!.GetValue<string>().Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Photo_reuse_requires_fresh_matching_metadata_and_intact_bytes()
    {
        var first = Seed();
        string completed;
        using (var session = ExportSession.Open(first, publishing: true))
        {
            RecordProfile(session);
            Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
            Assert.False(JsonSerializer.SerializeToNode(session.ReusePhoto(RootId))!["reused"]!.GetValue<bool>());
            session.Photo(RootId, Blob());
            completed = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        }
        var priorBytes = File.ReadAllBytes(completed);
        var second = Seed();
        using var refreshed = ExportSession.Open(second);
        Assert.Throws<InvalidOperationException>(() => refreshed.ReusePhoto(RootId));
        Ingest(refreshed, (ExportSession.PhotoPath(RootId), Metadata("v2")));
        Assert.False(JsonSerializer.SerializeToNode(refreshed.ReusePhoto(RootId))!["reused"]!.GetValue<bool>());
        Ingest(refreshed, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        Assert.True(JsonSerializer.SerializeToNode(refreshed.ReusePhoto(RootId))!["reused"]!.GetValue<bool>());
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(second, "photos", RootId + ".png")));
        Assert.Equal(priorBytes, File.ReadAllBytes(completed));
        File.WriteAllBytes(Path.Combine(second, "photos", RootId + ".png"), [1, 2, 3]);
        RecordProfile(refreshed);
        Assert.Throws<InvalidOperationException>(() => refreshed.Publish());
    }

    [Fact]
    public void Interrupted_downloads_can_be_reused_only_by_the_same_connected_account()
    {
        var first = Seed();
        using (var session = ExportSession.Open(first))
        {
            Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
            session.Photo(RootId, Blob());
        }
        var second = Seed();
        using var retry = ExportSession.Open(second);
        Ingest(retry, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        Assert.True(JsonSerializer.SerializeToNode(retry.ReusePhoto(RootId))!["reused"]!.GetValue<bool>());
        var other = Seed(account: "20000000-0000-0000-0000-000000000002");
        using var independent = ExportSession.Open(other);
        Ingest(independent, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        Assert.False(JsonSerializer.SerializeToNode(independent.ReusePhoto(RootId))!["reused"]!.GetValue<bool>());
    }

    [Fact]
    public void Empty_or_denied_manager_results_do_not_create_an_invented_root()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        Ingest(session, (ExportSession.ManagerPath(RootId), Ok(new JsonObject())));
        Assert.NotEmpty(session.Plan().Errors);
        Ingest(session, (ExportSession.ManagerPath(RootId), Missing("Authorization_RequestDenied")));
        Assert.NotEmpty(session.Plan().Errors);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Fact]
    public void Previous_run_payloads_cannot_be_restamped_as_fresh_directory_reads()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var requests = Path.Combine(root, "old-requests.json");
        var response = Path.Combine(root, "old-response.json");
        Save(requests, new[] { ExportSession.UserPath(RootId) });
        Save(response, new JsonObject { ["results"] = new JsonArray(Ok(User(RootId))) });
        File.SetLastWriteTimeUtc(response, DateTime.UtcNow.AddDays(-1));
        Assert.Throws<InvalidOperationException>(() => session.Ingest(requests, response));
    }

    [Theory]
    [InlineData("value", "OLD SITE")]
    [InlineData("clear", null)]
    public void Current_observation_cannot_relabel_stale_profile_source_as_fresh(string state, string? value)
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var evidence = Path.Combine(root, "previous-profile-source.json");
        Save(evidence, new { directoryId = RootId, workPosition = new { building = value } });
        File.SetLastWriteTimeUtc(evidence, DateTime.UtcNow.AddDays(-1));
        var fields = UnknownFields();
        fields["officeBuilding"] = new(state, value, "An old source result with a new observation timestamp.");
        var input = Path.Combine(root, "current-observation.json");
        Save(input, new ExportSession.Observation(RootId, "person1@example.test", fields,
            [evidence], [], null, DateTimeOffset.UtcNow));
        var error = Assert.Throws<InvalidOperationException>(() => session.Profile(RootId, input));
        Assert.Contains("previous run", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "profiles")));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "sources")));
    }

    [Fact]
    public void Replanning_keeps_dispatched_job_files_immutable_while_workers_read_them()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var job = JsonSerializer.SerializeToNode(session.Jobs())!["jobFiles"]!.AsArray().Single()!.GetValue<string>();
        var original = File.ReadAllBytes(job);
        using var reader = new FileStream(job, FileMode.Open, FileAccess.Read, FileShare.Read);
        session.Jobs();
        Assert.Equal(original, File.ReadAllBytes(job));
    }

    [Fact]
    public void Evidence_identity_freshness_field_scope_and_paths_are_enforced()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var evidence = Path.Combine(path, "workers", "source.json");
        Save(evidence, new { directoryId = RootId, workPosition = new { city = "Fictitious city" } });
        var fields = UnknownFields();
        var original = new ExportSession.Observation(RootId, "person1@example.test", fields,
            [evidence], [], null, DateTimeOffset.UtcNow);
        var input = Path.Combine(path, "workers", "profile.json");
        Save(input, original with { Email = "different@example.test" });
        Assert.Throws<InvalidOperationException>(() => session.Profile(RootId, input));
        Save(input, original with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Throws<InvalidOperationException>(() => session.Profile(RootId, input));
        fields["archived"] = new ExportSession.Field("clear", null, "Not a producer-managed field");
        Save(input, original);
        Assert.Throws<InvalidOperationException>(() => session.Profile(RootId, input));
        Assert.Throws<InvalidOperationException>(() => ExportSession.Normalize("https://example.test/v1.0/users/x"));
        Assert.Equal("/users/x/directReports?$skiptoken=a%2Fb%2B",
            ExportSession.Normalize("https://graph.microsoft.com/v1.0/users/x/directReports?$skiptoken=a%2Fb%2B"));
    }

    [Theory]
    [InlineData("NORTH/001.2", "NORTH", "001.2")]
    [InlineData("NORTH/Mobile", "NORTH", null)]
    [InlineData("2/REDMND", "2", null)]
    public void Office_components_preserve_source_room_text_and_flag_unknown_suffixes(
        string location, string building, string? office)
    {
        var fields = new Dictionary<string, object?>();
        var warnings = new List<string>();
        ExportSession.ApplyOffice(fields, location, warnings, "person@example.test");
        Assert.Equal(building, fields["officeBuilding"]);
        if (office is null)
        {
            Assert.False(fields.ContainsKey("officeNumber"));
            Assert.NotEmpty(warnings);
        }
        else Assert.Equal(office, fields["officeNumber"]);
    }

    private string NewRun(string account = AccountId)
        => ExportSession.Create("Example Person", RootId, account, root);
    private string Seed(string office = "NORTH/001.2", string account = AccountId)
    {
        var path = NewRun(account);
        using var session = ExportSession.Open(path);
        var user = User(RootId);
        user["officeLocation"] = office;
        Ingest(session, (ExportSession.UserPath(RootId), Ok(user)),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Empty()));
        return path;
    }
    private void Ingest(ExportSession session, params (string Path, JsonObject Response)[] values)
    {
        Directory.CreateDirectory(root);
        var requests = Path.Combine(root, Guid.NewGuid() + ".json");
        var response = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(requests, values.Select(v => v.Path).ToArray());
        var results = new JsonArray(values.Select(v => (JsonNode?)v.Response).ToArray());
        Save(response, new JsonObject { ["results"] = results });
        session.Ingest(requests, response);
    }
    private void RecordProfile(ExportSession session)
    {
        var person = session.Plan().People.Single();
        var evidence = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(evidence, new { directoryId = person.Id, workPosition = new { city = "Fictitious city" } });
        var input = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(input, new ExportSession.Observation(person.Id, person.Email, UnknownFields(), [evidence], [], null, DateTimeOffset.UtcNow));
        session.Profile(person.Id, input);
    }
    private static Dictionary<string, ExportSession.Field> UnknownFields() => new()
    {
        ["officeState"] = new("unknown", null, "Source does not establish work state."),
        ["officeCountry"] = new("unknown", null, "Source does not establish work country.")
    };
    private string Blob()
    {
        var file = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(file, new { statusCode = 200, contentType = "image/png", sizeBytes = Png.Length,
            base64Content = Convert.ToBase64String(Png) });
        return file;
    }
    private static JsonObject User(string id, string name = "Example Person") => new()
    {
        ["id"] = id, ["mail"] = id == RootId ? "person1@example.test" : id == ChildId ? "person2@example.test" : "alternate@example.test",
        ["displayName"] = name, ["jobTitle"] = "Example Engineer", ["department"] = "Example Department"
    };
    private static JsonObject Ok(JsonObject data) => new() { ["statusCode"] = 200, ["data"] = data };
    private static JsonObject Empty() => Ok(new JsonObject { ["value"] = new JsonArray() });
    private static JsonObject Missing(string code) => new()
        { ["statusCode"] = 404, ["error"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code } } };
    private static JsonObject Metadata(string etag) => Ok(new JsonObject
        { ["id"] = "default", ["width"] = 1, ["height"] = 1, ["@odata.mediaEtag"] = etag });
    private static void Save(string file, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(value, ExportSession.Json));
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
