using System.Diagnostics;
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
    [InlineData(0, "lookup", "lookup unavailable")]
    [InlineData(1, "lookup", "lookup unavailable")]
    [InlineData(1, "identity", "identity")]
    [InlineData(1, "stale", "freshly queried")]
    [InlineData(1, "field", "producer-managed")]
    [InlineData(1, "evidence", "full tool payload")]
    [InlineData(1, "malformed", null)]
    public void Mixed_profile_batches_retain_later_successes_and_only_need_corrected_failures(
        int failedIndex, string failure, string? diagnostic)
    {
        var path = Seed();
        using (var session = ExportSession.Open(path))
        {
            Ingest(session,
                (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId), User(AltId)) })),
                (ExportSession.ReportsPath(ChildId), Empty()),
                (ExportSession.ReportsPath(AltId), Empty()),
                (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")),
                (ExportSession.PhotoPath(ChildId), Missing("ImageNotFound")),
                (ExportSession.PhotoPath(AltId), Missing("ImageNotFound")));
            var people = session.Plan().People;
            var inputs = people.Select(person => ProfileInput(person)).ToArray();
            var failed = people[failedIndex];
            var original = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(inputs[failedIndex]), ExportSession.Json)!;
            if (failure == "malformed") File.WriteAllText(inputs[failedIndex], "{invalid");
            else Save(inputs[failedIndex], failure switch
            {
                "lookup" => original with { Error = "Work-profile lookup unavailable." },
                "identity" => original with { Email = "wrong-person@example.test" },
                "stale" => original with { ObservedAt = DateTimeOffset.UtcNow.AddDays(-1) },
                "field" => original with { Fields = new(original.Fields) { ["archived"] = new("clear", null, "Invalid field.") } },
                "evidence" => original with { EvidenceFiles = [Path.Combine(root, "missing-source.json")] },
                _ => throw new ArgumentException("Unknown test failure.")
            });
            var batch = Path.Combine(root, "profile-batch.json");
            Save(batch, inputs);

            var result = session.Profiles(batch);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            var outcomes = result.Items;
            Assert.Equal(3, outcomes.Length);
            for (var index = 0; index < people.Length; index++)
            {
                Assert.Equal(index, outcomes[index].Index);
                Assert.Equal(inputs[index], outcomes[index].InputFile);
                Assert.Equal(index == failedIndex && failure == "malformed" ? null : people[index].Id, outcomes[index].DirectoryId);
                Assert.Equal(index != failedIndex, outcomes[index].Recorded);
            }
            Assert.False(string.IsNullOrWhiteSpace(outcomes[failedIndex].Error));
            if (diagnostic is not null) Assert.Contains(diagnostic, outcomes[failedIndex].Error);
            var successes = people.Where(person => person.Id != failed.Id).ToDictionary(
                person => person.Id, person => File.ReadAllBytes(Path.Combine(path, "profiles", person.Id + ".json")));
            var evidence = Directory.GetFiles(Path.Combine(path, "sources")).ToDictionary(file => file, File.ReadAllBytes);
            Assert.Equal(2, successes.Count);
            Assert.Equal(2, evidence.Count);
            Assert.Throws<InvalidOperationException>(() => session.Publish());

            Save(inputs[failedIndex], original);
            Save(batch, new[] { inputs[failedIndex] });
            var corrected = session.Profiles(batch);
            Assert.Equal(1, corrected.Succeeded);
            Assert.Equal(0, corrected.Failed);
            foreach (var (id, bytes) in successes)
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(path, "profiles", id + ".json")));
            foreach (var (file, bytes) in evidence) Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        using var reopened = ExportSession.Open(path, publishing: true);
        var import = JsonSerializer.SerializeToNode(reopened.Publish())!["importPath"]!.GetValue<string>();
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task Cli_reports_mixed_results_and_retains_failures_over_earlier_success_until_corrected(
        int failedIndex, bool malformed)
    {
        var path = Seed();
        string failedInput;
        string successfulInput;
        byte[] earlier;
        using (var session = ExportSession.Open(path))
        {
            Ingest(session,
                (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
                (ExportSession.ReportsPath(ChildId), Empty()),
                (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")),
                (ExportSession.PhotoPath(ChildId), Missing("ImageNotFound")));
            var people = session.Plan().People;
            failedInput = ProfileInput(people.Single(person => person.Id == RootId));
            successfulInput = ProfileInput(people.Single(person => person.Id == ChildId));
            session.Profile(RootId, failedInput);
            earlier = File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json"));
        }
        var original = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(failedInput), ExportSession.Json)!;
        if (malformed) File.WriteAllText(failedInput, "{invalid");
        else Save(failedInput, original with { Email = "wrong-person@example.test" });
        var batch = Path.Combine(root, "cli-batch.json");
        var inputs = failedIndex == 0 ? new[] { failedInput, successfulInput } : new[] { successfulInput, failedInput, successfulInput };
        Save(batch, inputs);

        var result = await RunCli("profiles", path, batch);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Error);
        var json = JsonNode.Parse(result.Output)!;
        Assert.Equal(inputs.Length - 1, json["succeeded"]!.GetValue<int>());
        Assert.Equal(1, json["failed"]!.GetValue<int>());
        Assert.Equal(inputs.Length, json["items"]!.AsArray().Count);
        for (var index = 0; index < inputs.Length; index++)
        {
            Assert.Equal(index, json["items"]![index]!["index"]!.GetValue<int>());
            Assert.Equal(inputs[index], json["items"]![index]!["inputFile"]!.GetValue<string>());
            Assert.Equal(index != failedIndex, json["items"]![index]!["recorded"]!.GetValue<bool>());
        }
        Assert.False(string.IsNullOrWhiteSpace(json["items"]![failedIndex]!["error"]!.GetValue<string>()));
        if (malformed) Assert.Null(json["items"]![failedIndex]!["directoryId"]);
        else Assert.Contains("identity", json["items"]![failedIndex]!["error"]!.GetValue<string>());
        Assert.Equal(earlier, File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json")));
        var successful = File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json"));
        var sources = Directory.GetFiles(Path.Combine(path, "sources")).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        var rejected = await RunCli("publish", path);
        Assert.Equal(1, rejected.ExitCode);
        Assert.Empty(rejected.Output);
        Assert.Contains("Unresolved profile inputs", JsonNode.Parse(rejected.Error)!["error"]!.GetValue<string>());

        Save(failedInput, original);
        Save(batch, new[] { failedInput });
        var corrected = await RunCli("profiles", path, batch);
        Assert.Equal(0, corrected.ExitCode);
        Assert.Empty(corrected.Error);
        Assert.Single(JsonNode.Parse(corrected.Output)!["items"]!.AsArray());
        Assert.Equal(successful, File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json")));
        var published = await RunCli("publish", path);
        Assert.Equal(0, published.ExitCode);
        Assert.Empty(published.Error);
        var import = JsonNode.Parse(published.Output)!["importPath"]!.GetValue<string>();
        var destination = Path.GetDirectoryName(import)!;
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray().Count);
        Assert.Equal(successful, File.ReadAllBytes(Path.Combine(destination, "profiles", ChildId + ".json")));
        foreach (var (file, bytes) in sources)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, "sources", file)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cli_profile_arrays_have_stable_counts_and_item_arrays(int count)
    {
        var path = Seed();
        string input;
        using (var session = ExportSession.Open(path)) input = ProfileInput(session.Plan().People.Single());
        var batch = Path.Combine(root, "cardinality.json");
        Save(batch, Enumerable.Repeat(input, count).ToArray());

        var result = await RunCli("profiles", path, batch);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var json = JsonNode.Parse(result.Output)!.AsObject();
        Assert.Equal(count, json["succeeded"]!.GetValue<int>());
        Assert.Equal(0, json["failed"]!.GetValue<int>());
        var items = json["items"]!.AsArray();
        Assert.Equal(count, items.Count);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(index, items[index]!["index"]!.GetValue<int>());
            Assert.Equal(input, items[index]!["inputFile"]!.GetValue<string>());
            Assert.Equal(RootId, items[index]!["directoryId"]!.GetValue<string>());
            Assert.True(items[index]!["recorded"]!.GetValue<bool>());
            Assert.Null(items[index]!["error"]);
        }
        Assert.Equal(count == 0 ? 0 : 1, Directory.GetFiles(Path.Combine(path, "profiles")).Length);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not-an-array\"")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    public async Task Cli_rejects_invalid_batch_shapes_before_processing_members(string content)
    {
        var path = Seed();
        var batch = Path.Combine(root, "invalid-batch.json");
        if (JsonNode.Parse(content) is JsonArray array)
        {
            using var session = ExportSession.Open(path);
            array.Insert(0, JsonValue.Create(ProfileInput(session.Plan().People.Single())));
            content = array.ToJsonString();
        }
        File.WriteAllText(batch, content);

        var result = await RunCli("profiles", path, batch);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.False(string.IsNullOrWhiteSpace(JsonNode.Parse(result.Error)!["error"]!.GetValue<string>()));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "profiles")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_success_cannot_resolve_a_newer_failed_observation_even_at_the_failed_input_path(bool cli)
    {
        var path = Seed();
        string earlierInput;
        using (var session = ExportSession.Open(path))
        {
            Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
            earlierInput = ProfileInput(session.Plan().People.Single());
            session.Profile(RootId, earlierInput);
        }
        var earlier = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(earlierInput), ExportSession.Json)!;
        var saved = File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json"));
        var newer = earlier with { ObservedAt = earlier.ObservedAt.AddSeconds(1), Error = "Newer source lookup failed." };
        var failedInput = Path.Combine(root, "newer-failure.json");
        Save(failedInput, newer);
        var batch = Path.Combine(root, "ordered-profiles.json");
        Save(batch, new[] { failedInput, earlierInput });

        var failed = await ProfilesAtSeam(cli, path, batch, 1);

        Assert.Equal(0, failed.Succeeded);
        Assert.Equal(2, failed.Failed);
        Assert.Contains("earlier", failed.Items[1].Error);
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json")));
        using (var session = ExportSession.Open(path, publishing: true))
            Assert.Throws<InvalidOperationException>(() => session.Publish());

        Save(failedInput, earlier);
        Save(batch, new[] { failedInput });
        var replayed = await ProfilesAtSeam(cli, path, batch, 1);
        Assert.Equal(1, replayed.Failed);
        Assert.Contains("earlier", replayed.Items.Single().Error);
        using (var session = ExportSession.Open(path, publishing: true))
            Assert.Throws<InvalidOperationException>(() => session.Publish());

        var correctedInput = Path.Combine(root, "corrected-replacement.json");
        Save(correctedInput, newer with { Error = null });
        Save(batch, new[] { correctedInput });
        var corrected = await ProfilesAtSeam(cli, path, batch, 0);
        Assert.Equal(1, corrected.Succeeded);
        Assert.Equal(0, corrected.Failed);
        using var reopened = ExportSession.Open(path, publishing: true);
        Assert.NotNull(JsonSerializer.SerializeToNode(reopened.Publish())!["importPath"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correcting_an_identity_mismatch_resolves_its_input_without_replaying_other_successes(bool cli)
    {
        var path = Seed();
        string rootInput;
        using (var session = ExportSession.Open(path))
        {
            Ingest(session,
                (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
                (ExportSession.ReportsPath(ChildId), Empty()),
                (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")),
                (ExportSession.PhotoPath(ChildId), Missing("ImageNotFound")));
            var people = session.Plan().People;
            rootInput = ProfileInput(people.Single(person => person.Id == RootId));
            session.Profile(RootId, rootInput);
            session.Profile(ChildId, ProfileInput(people.Single(person => person.Id == ChildId)));
        }
        var other = File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json"));
        var sources = Directory.GetFiles(Path.Combine(path, "sources")).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        var correct = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(rootInput), ExportSession.Json)!;
        var failedInput = Path.Combine(root, "mismatched-identity.json");
        Save(failedInput, correct with { DirectoryId = ChildId });
        var batch = Path.Combine(root, "identity-correction.json");
        Save(batch, new[] { failedInput });

        var failed = await ProfilesAtSeam(cli, path, batch, 1);

        Assert.Equal(1, failed.Failed);
        Assert.Contains("identity", failed.Items.Single().Error);
        using (var session = ExportSession.Open(path, publishing: true))
            Assert.Throws<InvalidOperationException>(() => session.Publish());
        Save(failedInput, correct);
        var corrected = await ProfilesAtSeam(cli, path, batch, 0);
        Assert.Equal(1, corrected.Succeeded);
        Assert.Equal(0, corrected.Failed);
        Assert.Equal(other, File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json")));
        using var reopened = ExportSession.Open(path, publishing: true);
        var import = JsonSerializer.SerializeToNode(reopened.Publish())!["importPath"]!.GetValue<string>();
        foreach (var (file, bytes) in sources)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(import)!, "sources", file)));
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
        session.Profile(person.Id, ProfileInput(person));
    }
    private string ProfileInput(ExportSession.Person person)
    {
        var evidence = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(evidence, new { directoryId = person.Id, workPosition = new { city = "Fictitious city" } });
        var input = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(input, new ExportSession.Observation(person.Id, person.Email, UnknownFields(), [evidence], [], null, DateTimeOffset.UtcNow));
        return input;
    }
    private static async Task<(int ExitCode, string Output, string Error)> RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig",
            Path.ChangeExtension(typeof(OrgExportTests).Assembly.Location, ".runtimeconfig.json"),
            typeof(ExportSession).Assembly.Location }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    private static async Task<ExportSession.ProfileBatchResult> ProfilesAtSeam(
        bool cli, string path, string batch, int expectedExitCode)
    {
        if (!cli)
        {
            using var session = ExportSession.Open(path);
            return session.Profiles(batch);
        }
        var result = await RunCli("profiles", path, batch);
        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Empty(result.Error);
        return JsonSerializer.Deserialize<ExportSession.ProfileBatchResult>(result.Output, ExportSession.Json)!;
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
