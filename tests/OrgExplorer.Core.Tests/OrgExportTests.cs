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
    public void Capture_correlates_original_inline_results_and_preserves_order_and_nested_failures()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "coordinator.jsonl");
        File.WriteAllText(events, "");
        var requests = new[] { ExportSession.UserPath(RootId), ExportSession.ReportsPath(RootId) };
        var claim = session.ClaimSource("coordinator", "structured", requests);
        var arguments = new JsonObject { ["entityUrls"] = JsonSerializer.SerializeToNode(requests) };
        var requestFile = Path.Combine(root, "request.json");
        Save(requestFile, new { toolName = "workiq-fetch", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "other", toolName = "workiq-fetch",
            arguments = new { entityUrls = new[] { ExportSession.UserPath(ChildId) } } });
        AppendEvent(events, "tool.execution_start", new { toolCallId = "mine", toolName = "workiq-fetch", arguments });
        const string payload = "{ \"results\": [ {\"statusCode\":200,\"data\":{\"id\":\"10000000-0000-0000-0000-000000000001\"}}, {\"statusCode\":503,\"error\":{\"code\":\"Unavailable\",\"retryAfter\":5}} ] }";
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "other", success = true,
            result = new { content = "{\"results\":[]}" } });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "mine", success = false,
            error = new { message = "One source failed" }, result = new { content = payload } });

        var capture = session.Capture(ticket);

        Assert.False(capture.ToolSucceeded);
        Assert.Equal("mine", capture.ToolCallId);
        Assert.Equal(2, capture.ResultCount);
        Assert.Equal(1, capture.NonSuccessResults);
        Assert.Equal(payload, File.ReadAllText(capture.ResponseFile));
        Assert.Equal(requests, JsonSerializer.Deserialize<string[]>(File.ReadAllText(capture.RequestsFile!)));
        Assert.Contains("One source failed", File.ReadAllText(capture.CompletionFile));
        var capturedResults = JsonNode.Parse(File.ReadAllText(capture.ResponseFile))!["results"]!.AsArray();
        var resultsFile = Path.Combine(root, "source-results.json");
        Save(resultsFile, requests.Select((request, index) =>
            new ExportSession.SourceResult(request, capturedResults[index]!.AsObject())).ToArray());
        session.FinishSource(claim.ClaimId!.Value, resultsFile);
        Assert.Equal("completed", session.SourceStatus().Reads.Single(read => read.Target == requests[0]).State);
        Assert.Equal("failed", session.SourceStatus().Reads.Single(read => read.Target == requests[1]).State);
        Assert.Null(session.ClaimSource("queued-worker", "profile", [ChildId]).ClaimId);
        session.Ingest(capture.RequestsFile!, capture.ResponseFile);
        var receipts = session.Plan().Receipts;
        Assert.Equal(200, receipts[requests[0]].Response["statusCode"]!.GetValue<int>());
        Assert.Equal(503, receipts[requests[1]].Response["statusCode"]!.GetValue<int>());
        Assert.Equal(5, receipts[requests[1]].Response["error"]!["retryAfter"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("structured")]
    [InlineData("error")]
    [InlineData("native-structured")]
    [InlineData("native-contents")]
    [InlineData("detailed")]
    public void Capture_transports_original_binary_payloads_directly_to_photo_validation(string transport)
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        var payload = File.ReadAllText(Blob());
        var events = Path.Combine(root, "photo-worker.jsonl");
        File.WriteAllText(events, "");
        var requestFile = Path.Combine(root, "photo-request.json");
        var arguments = new { path = "/users/" + RootId + "/photo/$value" };
        Save(requestFile, new { toolName = "workiq-fetch_blob", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "photo", toolName = "workiq-fetch_blob", arguments });
        object result;
        if (transport == "native-structured")
            result = new { content = "Display summary only", structuredContent = JsonNode.Parse(payload) };
        else if (transport == "native-contents")
            result = new { content = "Display summary only", contents = new[] { new { type = "text", text = payload } } };
        else if (transport == "detailed")
            result = new { content = "Display summary only", detailedContent = payload };
        else if (transport == "structured")
            result = new { content = JsonSerializer.Serialize(new { structuredContent = JsonNode.Parse(payload),
                content = new[] { new { type = "text", text = "Display summary only" } } }) };
        else
            result = new { content = JsonSerializer.Serialize(new { isError = transport == "error",
                content = new[] { new { type = "text", text = payload } } }) };
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "photo", success = transport != "error",
            result });

        var capture = session.Capture(ticket);
        var photo = JsonSerializer.SerializeToNode(session.Photo(RootId, capture.ResponseFile), ExportSession.Json)!;

        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(path, "photos", RootId + ".png")));
        Assert.Equal(Png.Length, photo["sizeBytes"]!.GetValue<int>());
        Assert.Equal("431ced6916a2a21a156e38701afe55bbd7f88969fbbfc56d7fe099d47f265460",
            photo["sha256"]!.GetValue<string>());
        Assert.Equal(JsonNode.Parse(payload)!.ToJsonString(), JsonNode.Parse(File.ReadAllText(capture.ResponseFile))!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_blocks_unverified_host_saved_file_references(bool replaced)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "replaced-file.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "fictitious work profile" };
        var requestFile = Path.Combine(root, "replaced-request.json");
        Save(requestFile, new { toolName = "workiq-retrieve", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "original", toolName = "workiq-retrieve", arguments });
        var saved = Path.Combine(root, "host-output.json");
        File.WriteAllText(saved, "{\"worker\":\"original\"}");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "original", success = true,
            result = new { content = "Output too large to display.\nFull output saved to: " + saved } });
        if (replaced)
            File.WriteAllText(saved, "{\"worker\":\"replacement\"}");

        var error = Assert.Throws<InvalidOperationException>(() => session.Capture(ticket));

        Assert.Contains("Unverified host-saved output reference", error.Message);
        var completion = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ticket)!, "completion.json")))!;
        Assert.Equal("Output too large to display.\nFull output saved to: " + saved,
            completion["data"]!["result"]!["content"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "response.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "receipts")));
    }

    [Fact]
    public async Task Concurrent_workers_capture_only_their_own_results_in_a_shared_event_stream()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "shared.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "fictitious work profile" };
        var tickets = new List<string>();
        foreach (var actor in new[] { "worker-a", "worker-b" })
        {
            var file = Path.Combine(root, actor + ".json");
            Save(file, new { toolName = "workiq-retrieve", arguments, agentId = actor });
            tickets.Add(session.BeginCapture(events, file));
        }
        AppendEvent(events, "tool.execution_start", new { toolCallId = "a", toolName = "workiq-retrieve", arguments }, "worker-a");
        AppendEvent(events, "tool.execution_start", new { toolCallId = "b", toolName = "workiq-retrieve", arguments }, "worker-b");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "b", success = true,
            result = new { content = "{\"worker\":\"b\"}" } }, "worker-b");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "a", success = true,
            result = new { content = "{\"worker\":\"a\"}" } }, "worker-a");

        var captures = await Task.WhenAll(tickets.Select(ticket => Task.Run(() => session.Capture(ticket))));

        Assert.Equal("{\"worker\":\"a\"}", File.ReadAllText(captures[0].ResponseFile));
        Assert.Equal("{\"worker\":\"b\"}", File.ReadAllText(captures[1].ResponseFile));
        Assert.NotEqual(captures[0].ResponseFile, captures[1].ResponseFile);
    }

    [Fact]
    public void Capture_retains_successes_from_a_native_error_without_a_result_wrapper()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "failed-batch.jsonl");
        File.WriteAllText(events, "");
        var requests = new[] { ExportSession.UserPath(RootId), ExportSession.ReportsPath(RootId) };
        var arguments = new { entityUrls = requests };
        var requestFile = Path.Combine(root, "failed-request.json");
        Save(requestFile, new { toolName = "workiq-fetch", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "failed-batch", toolName = "workiq-fetch", arguments });
        var payload = JsonSerializer.Serialize(new { results = new[] { Ok(User(RootId)), Missing("AccessDenied") } });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "failed-batch", success = false,
            error = new { message = "MCP error -32603: " + payload } });

        var capture = session.Capture(ticket);

        Assert.False(capture.ToolSucceeded);
        Assert.Equal(1, capture.NonSuccessResults);
        session.Ingest(capture.RequestsFile!, capture.ResponseFile);
        Assert.Equal(RootId, session.Plan().Receipts[requests[0]].Response["data"]!["id"]!.GetValue<string>());
        Assert.Equal("AccessDenied", session.Plan().Receipts[requests[1]].Response["error"]!["error"]!["code"]!.GetValue<string>());
        Assert.NotEmpty(session.Plan().Errors);
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("no-status")]
    [InlineData("incomplete")]
    [InlineData("truncated")]
    [InlineData("unsupported")]
    [InlineData("missing-saved")]
    [InlineData("stale-saved")]
    [InlineData("foreign-worker")]
    [InlineData("foreign-completion")]
    [InlineData("duplicate")]
    public void Capture_blocks_incomplete_unsupported_or_uncorrelated_results(string fault)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "blocked.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { entityUrls = new[] { ExportSession.UserPath(RootId) } };
        var requestFile = Path.Combine(root, "blocked-request.json");
        Save(requestFile, new { toolName = "workiq-fetch", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        var start = new { toolCallId = "blocked", toolName = "workiq-fetch", arguments };
        AppendEvent(events, "tool.execution_start", start, fault == "foreign-worker" ? "another-worker" : null);
        if (fault == "duplicate")
            AppendEvent(events, "tool.execution_start", start);
        var payload = fault switch
        {
            "null-entry" => "{\"results\":[null]}",
            "no-status" => "{\"results\":[{}]}",
            "incomplete" => "{\"results\":[]}",
            "truncated" => "{\"results\":[",
            "unsupported" => "Payload excerpt only",
            _ => JsonSerializer.Serialize(new { results = new[] { Ok(User(RootId)) } })
        };
        if (fault is "missing-saved" or "stale-saved")
        {
            var saved = Path.Combine(root, "missing-or-stale.json");
            if (fault == "stale-saved")
            {
                File.WriteAllText(saved, payload);
                File.SetLastWriteTimeUtc(saved, DateTime.UtcNow.AddDays(-1));
            }
            payload = "Output too large to display.\nFull output saved to: " + saved;
        }
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "blocked", success = true,
            result = new { content = payload } }, fault == "foreign-completion" ? "another-worker" : null);

        var error = Record.Exception(() => session.Capture(ticket));

        Assert.True(error is InvalidOperationException or JsonException or IOException, error?.ToString() ?? "Capture unexpectedly succeeded.");
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "response.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "receipts")));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("magic")]
    public void Captured_photos_still_require_authoritative_decoded_length_and_content(string fault)
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        var events = Path.Combine(root, "damaged-photo.jsonl");
        File.WriteAllText(events, "");
        var requestFile = Path.Combine(root, "damaged-request.json");
        var arguments = new { path = "/users/" + RootId + "/photo/$value" };
        Save(requestFile, new { toolName = "workiq-fetch_blob", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "damaged", toolName = "workiq-fetch_blob", arguments });
        var payload = JsonNode.Parse(File.ReadAllText(Blob()))!.AsObject();
        if (fault == "length") payload["sizeBytes"] = Png.Length + 1;
        else payload["contentType"] = "image/jpeg";
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "damaged", success = true,
            result = new { content = payload.ToJsonString() } });

        var capture = session.Capture(ticket);

        Assert.Throws<InvalidOperationException>(() => session.Photo(RootId, capture.ResponseFile));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "photos")));
    }

    [Fact]
    public void Captured_profile_evidence_is_copied_losslessly_and_a_result_cannot_be_claimed_twice()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "profile.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "fictitious work profile" };
        var requestFile = Path.Combine(root, "profile-request.json");
        Save(requestFile, new { toolName = "workiq-retrieve", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        var overlapping = session.BeginCapture(events, requestFile);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = "workiq-retrieve", arguments });
        const string payload = "{ \"directoryId\": \"10000000-0000-0000-0000-000000000001\", \"citations\": [\"https://example.test/work-profile\"], \"workPosition\": {} }";
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = true,
            result = new { content = payload } });
        var captured = session.Capture(ticket);
        var input = Path.Combine(root, "captured-observation.json");
        Save(input, new ExportSession.Observation(RootId, "person1@example.test", UnknownFields(),
            [captured.ResponseFile], [], null, DateTimeOffset.UtcNow));
        var previousSources = Directory.GetFiles(Path.Combine(path, "sources"));

        session.Profile(RootId, input);

        Assert.Equal(payload, File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(path, "sources")).Except(previousSources))));
        Assert.Throws<InvalidOperationException>(() => session.Capture(overlapping));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(overlapping)!, "response.json")));
    }

    [Theory]
    [InlineData("changed-history")]
    [InlineData("changed-arguments")]
    [InlineData("missing-completion")]
    [InlineData("duplicate-completion")]
    [InlineData("stale-event")]
    public void Capture_requires_unchanged_history_and_one_fresh_exact_completed_invocation(string fault)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "history.jsonl");
        AppendEvent(events, "session.start", new { marker = "original" });
        var arguments = new { query = "fictitious work profile" };
        var requestFile = Path.Combine(root, "history-request.json");
        Save(requestFile, new { toolName = "workiq-retrieve", arguments });
        var ticket = session.BeginCapture(events, requestFile);
        if (fault == "changed-history")
            File.WriteAllText(events, File.ReadAllText(events).Replace("original", "modified", StringComparison.Ordinal));
        if (fault == "stale-event")
            File.AppendAllText(events, JsonSerializer.Serialize(new { type = "tool.execution_start",
                timestamp = DateTimeOffset.UtcNow.AddDays(-1), data = new { toolCallId = "history", toolName = "workiq-retrieve", arguments } }) + "\n");
        else
            AppendEvent(events, "tool.execution_start", new { toolCallId = "history", toolName = "workiq-retrieve",
                arguments = fault == "changed-arguments" ? new { query = "another profile" } : arguments });
        if (fault != "missing-completion")
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "history", success = true,
                result = new { content = "{\"citations\":[]}" } });
        if (fault == "duplicate-completion")
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "history", success = true,
                result = new { content = "{\"citations\":[]}" } });

        Assert.Throws<InvalidOperationException>(() => session.Capture(ticket));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "response.json")));
        if (fault == "missing-completion")
        {
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "history", success = true,
                result = new { content = "{\"citations\":[]}" } });
            Assert.Equal("{\"citations\":[]}", File.ReadAllText(session.Capture(ticket).ResponseFile));
        }
    }

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

    [Fact]
    public void Shared_source_failures_block_an_otherwise_complete_export_until_recovered_without_replaying_successes()
    {
        var path = Seed();
        using var session = ExportSession.Open(path, publishing: true);
        RecordProfile(session);
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        var claim = session.ClaimSource("extra-evidence", "profile", [RootId, AltId]);
        Assert.Equal([AltId], claim.Targets);
        FinishSource(session, claim, new JsonObject
        {
            ["statusCode"] = 429, ["error"] = new JsonObject { ["code"] = "TooManyRequests" }
        });
        Assert.Contains("Source collection is incomplete", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
        session.BeginSourceRecovery();
        var recovery = session.ClaimSource("replacement", "profile", [RootId, AltId]);
        Assert.Equal([AltId], recovery.Targets);
        FinishSource(session, recovery, Ok(new JsonObject()));
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Kind == "profile" && read.Target == RootId).Attempts);
        var import = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        Assert.True(File.Exists(import));
    }

    [Fact]
    public void Complete_observations_cannot_bypass_the_shared_source_record()
    {
        var path = Seed();
        using var session = ExportSession.Open(path, publishing: true);
        RecordProfile(session, sourceCompleted: false);
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        Assert.Contains("Source read is not completed", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
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
        CompleteSource(session, "profile", RootId, Ok(new JsonObject()));
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
        CompleteSource(session, "profile", RootId, Ok(new JsonObject()));
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
            CompleteSource(session, "photo", $"/users/{RootId}/photo/$value", Ok(new JsonObject()));
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
        var originalSources = Directory.GetFiles(Path.Combine(path, "sources")).Order().ToArray();
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
        Assert.Equal(originalSources, Directory.GetFiles(Path.Combine(path, "sources")).Order().ToArray());
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
            var inputs = people.Select(person => ProfileInput(session, person)).ToArray();
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
            var previousSources = Directory.GetFiles(Path.Combine(path, "sources")).Length;

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
            Assert.Equal(previousSources + 2, evidence.Count);
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
            failedInput = ProfileInput(session, people.Single(person => person.Id == RootId));
            successfulInput = ProfileInput(session, people.Single(person => person.Id == ChildId));
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
        using (var session = ExportSession.Open(path)) input = ProfileInput(session, session.Plan().People.Single());
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
            array.Insert(0, JsonValue.Create(ProfileInput(session, session.Plan().People.Single())));
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
            earlierInput = ProfileInput(session, session.Plan().People.Single());
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
            rootInput = ProfileInput(session, people.Single(person => person.Id == RootId));
            session.Profile(RootId, rootInput);
            session.Profile(ChildId, ProfileInput(session, people.Single(person => person.Id == ChildId)));
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
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Reusing_a_failed_input_for_another_person_preserves_the_verified_failure(bool cli, int laterSeconds)
    {
        var path = Seed();
        string rootInput;
        string childInput;
        using (var session = ExportSession.Open(path))
        {
            Ingest(session,
                (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
                (ExportSession.ReportsPath(ChildId), Empty()),
                (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")),
                (ExportSession.PhotoPath(ChildId), Missing("ImageNotFound")));
            var people = session.Plan().People;
            rootInput = ProfileInput(session, people.Single(person => person.Id == RootId));
            childInput = ProfileInput(session, people.Single(person => person.Id == ChildId));
            session.Profile(RootId, rootInput);
            session.Profile(ChildId, childInput);
        }
        var original = File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json"));
        var rootObservation = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(rootInput), ExportSession.Json)!;
        var childObservation = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(childInput), ExportSession.Json)!;
        var failedObservation = rootObservation with
        {
            ObservedAt = childObservation.ObservedAt.AddSeconds(1),
            Error = "Newer lookup for the root Person failed."
        };
        var reusedInput = Path.Combine(root, "reused-input.json");
        var batch = Path.Combine(root, "reused-input-batch.json");
        Save(reusedInput, failedObservation);
        Save(batch, new[] { reusedInput });
        var failed = await ProfilesAtSeam(cli, path, batch, 1);
        Assert.Equal(1, failed.Failed);

        Save(reusedInput, childObservation with { ObservedAt = failedObservation.ObservedAt.AddSeconds(laterSeconds) });
        var unrelatedSuccess = await ProfilesAtSeam(cli, path, batch, 0);

        Assert.Equal(1, unrelatedSuccess.Succeeded);
        Assert.Equal(0, unrelatedSuccess.Failed);
        Assert.Equal(ChildId, unrelatedSuccess.Items.Single().DirectoryId);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(path, "profiles", RootId + ".json")));
        var savedChild = File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json"));
        using (var session = ExportSession.Open(path, publishing: true))
        {
            var rejection = Assert.Throws<InvalidOperationException>(() => session.Publish());
            Assert.Contains("Unresolved profile inputs", rejection.Message);
        }

        var correctedInput = Path.Combine(root, "corrected-root.json");
        Save(correctedInput, failedObservation with { Error = null });
        Save(batch, new[] { correctedInput });
        var corrected = await ProfilesAtSeam(cli, path, batch, 0);
        Assert.Equal(1, corrected.Succeeded);
        Assert.Equal(0, corrected.Failed);
        Assert.Equal(savedChild, File.ReadAllBytes(Path.Combine(path, "profiles", ChildId + ".json")));
        using var reopened = ExportSession.Open(path, publishing: true);
        var import = JsonSerializer.SerializeToNode(reopened.Publish())!["importPath"]!.GetValue<string>();
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray().Count);
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
        foreach (var value in values)
            CompleteSource(session, "structured", value.Path, value.Response);
    }
    private void RecordProfile(ExportSession session, bool sourceCompleted = true)
    {
        var person = session.Plan().People.Single();
        session.Profile(person.Id, ProfileInput(session, person, sourceCompleted));
    }
    private string ProfileInput(ExportSession session, ExportSession.Person person, bool sourceCompleted = true)
    {
        var evidence = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(evidence, new { directoryId = person.Id, workPosition = new { city = "Fictitious city" } });
        var input = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(input, new ExportSession.Observation(person.Id, person.Email, UnknownFields(), [evidence], [], null, DateTimeOffset.UtcNow));
        if (sourceCompleted) CompleteSource(session, "profile", person.Id, Ok(new JsonObject()));
        return input;
    }
    private void CompleteSource(ExportSession session, string kind, string target, JsonObject response)
    {
        // Existing payload fixtures may deliberately re-ingest a changed result without making another source call.
        if (session.SourceStatus().Reads.Any(read => read.Kind == kind && read.Target == target && read.State == "completed")) return;
        var claim = session.ClaimSource("fixture", kind, [target]);
        if (claim.ClaimId is not null) FinishSource(session, claim, response);
    }
    private void FinishSource(ExportSession session, ExportSession.SourceClaim claim, JsonObject response)
    {
        var results = Path.Combine(root, Guid.NewGuid() + ".json");
        Save(results, new[] { new ExportSession.SourceResult(claim.Targets.Single(), response) });
        session.FinishSource(claim.ClaimId!.Value, results);
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
    private static void AppendEvent(string file, string type, object data, string? agentId = null)
        => File.AppendAllText(file, JsonSerializer.Serialize(new { type, data, agentId, timestamp = DateTimeOffset.UtcNow }) + "\n");
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
