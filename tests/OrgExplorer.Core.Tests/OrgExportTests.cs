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

    [Theory]
    [InlineData("MCP error -32603: ", false)]
    [InlineData("MCP server 'workiq': ", false)]
    [InlineData("MCP server 'workiq': ", true)]
    public void Capture_retains_successes_from_a_native_error_without_a_result_wrapper(string prefix, bool resultContent)
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
            error = new { message = prefix + payload },
            result = resultContent ? new { content = prefix + payload } : null });

        var capture = session.Capture(ticket);

        Assert.False(capture.ToolSucceeded);
        Assert.Equal(1, capture.NonSuccessResults);
        Assert.Equal(payload, File.ReadAllText(capture.ResponseFile));
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
    [InlineData("empty-server")]
    [InlineData("truncated-server")]
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
            "empty-server" => "MCP server '': {\"results\":[]}",
            "truncated-server" => "MCP server 'workiq': {\"results\":[",
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
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "blocked",
            success = fault is not ("empty-server" or "truncated-server"),
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

    [Theory]
    [InlineData("#microsoft.graph.user")]
    [InlineData("#microsoft.graph.agentUser")]
    public void User_subtypes_retain_source_identity_and_their_complete_reporting_subtree(string type)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path, publishing: true);
        var report = User(ChildId, "Example Account");
        report["@odata.type"] = type;
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(report) })));
        Assert.Contains(ExportSession.ReportsPath(ChildId), session.Plan().Pending);
        Ingest(session,
            (ExportSession.ReportsPath(ChildId), Ok(new JsonObject { ["value"] = new JsonArray(User(AltId)) })),
            (ExportSession.ReportsPath(AltId), Empty()));

        var collection = session.Plan();

        Assert.Empty(collection.Pending);
        Assert.Empty(collection.Errors);
        Assert.Empty(collection.Excluded);
        Assert.Equal(3, collection.People.Length);
        var retained = Assert.Single(collection.People, person => person.Id == ChildId);
        Assert.Equal("person2@example.test", retained.Email);
        Assert.Equal(RootId, retained.ManagerId);
        Assert.Equal(type, retained.Directory["@odata.type"]!.GetValue<string>());
        Assert.Equal(ChildId, Assert.Single(collection.People, person => person.Id == AltId).ManagerId);
        foreach (var person in collection.People)
        {
            Ingest(session, (ExportSession.PhotoPath(person.Id), Missing("ImageNotFound")));
            session.Profile(person.Id, ProfileInput(session, person));
        }
        var import = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        var people = JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray();
        Assert.Equal(3, people.Count);
        Assert.Equal("person2@example.test", Assert.Single(people,
            person => person!["email"]!.GetValue<string>() == "alternate@example.test")!["managerEmail"]!.GetValue<string>());
        var warnings = JsonSerializer.Deserialize<string[]>(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(import)!, "review-required.json")))!;
        Assert.Equal(type == "#microsoft.graph.agentUser",
            warnings.Any(warning => warning.Contains("person2@example.test") && warning.Contains("agentUser")));
    }

    [Theory]
    [InlineData("#microsoft.graph.group")]
    [InlineData("#microsoft.graph.orgContact")]
    public void Non_user_directory_objects_remain_explicit_blockers(string type)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var report = User(ChildId);
        report["@odata.type"] = type;
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(report) })));

        var error = Assert.Throws<InvalidOperationException>(() => session.Plan());

        Assert.Contains("Unsupported directory object type: " + type, error.Message);
    }

    [Fact]
    public void Conflicting_reporting_lists_require_a_fresh_exact_Manager_and_preserve_review_evidence()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path, publishing: true);
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId), User(AltId)) })),
            (ExportSession.ReportsPath(AltId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
            (ExportSession.ReportsPath(ChildId), Empty()));
        Assert.Contains(ExportSession.ManagerPath(ChildId), session.Plan().Pending);
        Assert.Throws<InvalidOperationException>(() => session.Publish());

        Ingest(session, (ExportSession.ManagerPath(ChildId), Ok(User(AltId))));
        var collection = session.Plan();

        Assert.Empty(collection.Pending);
        Assert.Empty(collection.Errors);
        Assert.Equal(3, collection.People.Length);
        Assert.Equal(AltId, Assert.Single(collection.People, person => person.Id == ChildId).ManagerId);
        Assert.Equal(6, collection.Receipts.Count);
        foreach (var person in collection.People)
        {
            Ingest(session, (ExportSession.PhotoPath(person.Id), Missing("ImageNotFound")));
            session.Profile(person.Id, ProfileInput(session, person));
        }
        var import = JsonSerializer.SerializeToNode(session.Publish())!["importPath"]!.GetValue<string>();
        var people = JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray();
        Assert.Equal("alternate@example.test", Assert.Single(people,
            person => person!["email"]!.GetValue<string>() == "person2@example.test")!["managerEmail"]!.GetValue<string>());
        var warnings = JsonSerializer.Deserialize<string[]>(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(import)!, "review-required.json")))!;
        Assert.Contains(warnings, warning => warning.Contains("person2@example.test")
            && warning.Contains("conflicting reporting parents") && warning.Contains("Manager"));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("excluded")]
    public void Manager_confirmation_cannot_resolve_a_conflict_with_unrelated_absent_stale_or_excluded_evidence(string fault)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var manager = User(AltId, fault == "excluded" ? "Example (NON EA SC ALT)" : "Example Person");
        if (fault == "stale")
            Ingest(session, (ExportSession.ManagerPath(ChildId), Ok(manager.DeepClone().AsObject())));
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject
                { ["value"] = new JsonArray(User(ChildId), manager.DeepClone()) })),
            (ExportSession.ReportsPath(AltId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
            (ExportSession.ReportsPath(ChildId), Empty()));
        if (fault != "stale")
            Ingest(session, (ExportSession.ManagerPath(ChildId), fault switch
            {
                "missing" => Missing("Request_ResourceNotFound"),
                "unrelated" => Ok(User(AccountId)),
                _ => Ok(manager)
            }));

        if (fault == "stale") Assert.Contains(ExportSession.ManagerPath(ChildId), session.Plan().Pending);
        else Assert.NotEmpty(session.Plan().Errors);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_reporting_pages_allow_an_audited_Manager_refresh_without_replaying_other_successes(bool interrupted)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        const string thirdId = "10000000-0000-0000-0000-000000000004";
        var third = User(thirdId, "Example Third Manager");
        third["mail"] = "third@example.test";
        var continuation = $"/users/{thirdId}/directReports?$skiptoken=fictitious-next-page";
        Ingest(session,
            (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId), User(AltId), third.DeepClone()) })),
            (ExportSession.ReportsPath(AltId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })),
            (ExportSession.ReportsPath(thirdId), Ok(new JsonObject
                { ["value"] = new JsonArray(), ["@odata.nextLink"] = continuation })),
            (ExportSession.ReportsPath(ChildId), Empty()));
        var target = ExportSession.ManagerPath(ChildId);
        if (interrupted)
        {
            var stopped = session.ClaimSource("stopped-manager", "structured", [target]);
            session.InterruptSource(stopped.ClaimId!.Value, "Coordinator confirmed the worker stopped before a complete result was available.");
            session.BeginSourceRecovery();
        }
        Ingest(session, (target, Ok(User(AltId))));
        Assert.DoesNotContain(target, session.Plan().Pending);
        var previous = session.SourceStatus().Reads.Single(read => read.Target == target).Attempts.ToArray();
        var evidence = previous.Where(attempt => attempt.ResultFile is not null).ToDictionary(attempt => attempt.ResultFile!,
            attempt => File.ReadAllBytes(Path.Combine(path, attempt.ResultFile!)));
        Ingest(session, (continuation, Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId)) })));
        var unrelated = session.SourceStatus().Reads.Where(read => read.Target != target)
            .ToDictionary(read => read.Target, read => read.Attempts.ToArray());

        Assert.Contains(target, session.Plan().Pending);
        Assert.Null(session.ClaimSource("replay", "structured", [target]).ClaimId);
        var refreshed = await RunCli("source-refresh-manager", path, ChildId, "Newer conflicting reporting evidence requires fresh confirmation.");
        Assert.Equal(0, refreshed.ExitCode);
        Assert.Empty(refreshed.Error);
        var read = session.SourceStatus().Reads.Single(read => read.Target == target);
        Assert.Empty(read.Attempts);
        var history = JsonSerializer.SerializeToNode(read, ExportSession.Json)!["previousObservations"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(previous, ExportSession.Json), history.Single()!["attempts"]));
        Assert.NotEqual(0, (await RunCli("source-refresh-manager", path, ChildId, "Cannot reset an outstanding refresh.")).ExitCode);
        FinishSource(session, session.ClaimSource("refresh", "structured", [target]),
            new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" } });
        FinishSource(session, session.ClaimSource("refresh-retry", "structured", [target]), Ok(third.DeepClone().AsObject()));
        Ingest(session, (target, Ok(third.DeepClone().AsObject())));

        Assert.Empty(session.Plan().Pending);
        Assert.Empty(session.Plan().Errors);
        Assert.Equal(thirdId, session.Plan().People.Single(person => person.Id == ChildId).ManagerId);
        Assert.Equal(2, session.SourceStatus().Reads.Single(item => item.Target == target).Attempts.Count);
        Assert.NotEqual(0, (await RunCli("source-refresh-manager", path, ChildId, "No newer conflict to refresh.")).ExitCode);
        foreach (var (file, bytes) in evidence) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(path, file)));
        foreach (var (otherTarget, attempts) in unrelated)
            Assert.Equal(attempts, session.SourceStatus().Reads.Single(item => item.Target == otherTarget).Attempts);
        foreach (var person in session.Plan().People)
        {
            Ingest(session, (ExportSession.PhotoPath(person.Id), Missing("ImageNotFound")));
            session.Profile(person.Id, ProfileInput(session, person));
        }
        var published = JsonSerializer.SerializeToNode(session.Publish())!;
        Assert.Equal(4, published["people"]!.GetValue<int>());
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_source_finish_reports_only_its_claim_while_status_retains_all_reads(bool recovery)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        foreach (var id in new[] { RootId, ChildId })
        {
            var previous = session.ClaimSource("photo-worker", "photo", [$"/users/{id}/photo/$value"]);
            FinishSource(session, previous, Ok(new JsonObject()));
        }
        var target = $"/users/{AltId}/photo/$value";
        var claim = session.ClaimSource("photo-worker", "photo", [target]);
        if (recovery)
        {
            FinishSource(session, claim, new JsonObject
            {
                ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" }
            });
            session.BeginSourceRecovery();
            claim = session.ClaimSource("replacement", "photo", [target]);
        }
        var results = Path.Combine(root, "finish-results.json");
        Save(results, new[] { new ExportSession.SourceResult(target, Ok(new JsonObject())) });

        var result = await RunCli("source-finish", path, claim.ClaimId!.Value.ToString(), results);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var output = JsonNode.Parse(result.Output)!;
        var read = Assert.Single(output["reads"]!.AsArray());
        Assert.Equal(target, read!["target"]!.GetValue<string>());
        Assert.Equal("completed", read["state"]!.GetValue<string>());
        Assert.Equal(claim.ClaimId.Value, output["claimId"]!.GetValue<Guid>());
        Assert.Equal(recovery, output["recoveryConsumed"]!.GetValue<bool>());
        Assert.Null(output["stopReason"]);
        var ledger = session.SourceStatus();
        Assert.Equal(3, ledger.Reads.Count);
        Assert.All(ledger.Reads, item => Assert.Equal("completed", item.State));
    }

    [Fact]
    public async Task Cli_per_read_recovery_authorization_preserves_prior_attempts_and_recovers_a_later_failure()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var unavailable = new JsonObject
        {
            ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" }
        };
        var firstTarget = $"/users/{RootId}/photo/$value";
        var first = session.ClaimSource("first", "photo", [firstTarget]);
        FinishSource(session, first, unavailable);
        session.BeginSourceRecovery();
        FinishSource(session, session.ClaimSource("first-retry", "photo", [firstTarget]), Ok(new JsonObject()));
        var laterTarget = $"/users/{ChildId}/photo/$value";
        FinishSource(session, session.ClaimSource("later", "photo", [laterTarget]), unavailable);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());

        const string reason = "User explicitly authorized one retry per transiently failed read for this run.";
        var beforeAuthorization = DateTimeOffset.UtcNow;
        var result = await RunCli("source-authorize-per-read-recovery", path, reason);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var authorization = JsonNode.Parse(result.Output)!["perReadRecovery"]!;
        Assert.Equal(reason, authorization["reason"]!.GetValue<string>());
        Assert.True(authorization["authorizedAt"]!.GetValue<DateTimeOffset>() >= beforeAuthorization);
        session.BeginSourceRecovery();
        var retry = session.ClaimSource("later-retry", "photo", [laterTarget]);
        Assert.Equal([laterTarget], retry.Targets);
        FinishSource(session, retry, Ok(new JsonObject()));
        var ledger = session.SourceStatus();
        Assert.True(ledger.RecoveryConsumed);
        Assert.All(ledger.Reads, read =>
        {
            Assert.Equal("completed", read.State);
            Assert.Equal(2, read.Attempts.Count);
            Assert.All(read.Attempts, attempt => Assert.NotNull(attempt.ResultFile));
        });
        Assert.Null(session.ClaimSource("replay", "photo", [firstTarget]).ClaimId);
    }

    [Fact]
    public async Task Cli_unclassified_recovery_authorizes_only_the_approved_read_without_rewriting_its_failure()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        FinishSource(session, session.ClaimSource("success", "profile", [RootId]), Ok(new JsonObject()));
        var failed = session.ClaimSource("failed", "profile", [ChildId]);
        const string otherId = "30000000-0000-0000-0000-000000000099";
        var other = session.ClaimSource("other", "profile", [otherId]);
        var unclassified = new JsonObject
        {
            ["statusCode"] = 200, ["data"] = new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = "Fictitious indexed profile could not be opened." }
            }
        };
        FinishSource(session, failed, unclassified);
        FinishSource(session, other, unclassified);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        var before = session.SourceStatus();
        var originalAttempt = before.Reads.Single(read => read.Target == ChildId).Attempts.Single();
        var originalReceipt = File.ReadAllBytes(Path.Combine(path, originalAttempt.ResultFile!));

        const string reason = "User explicitly approved one retry of this unclassified profile read.";
        var result = await RunCli("source-authorize-unclassified-recovery", path, "profile", ChildId, reason);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(reason, JsonNode.Parse(result.Output)!["unclassifiedRecovery"]!["reason"]!.GetValue<string>());
        var approved = session.SourceStatus();
        Assert.True(approved.RecoveryConsumed);
        Assert.Null(approved.PerReadRecovery);
        Assert.True(approved.Reads.Single(read => read.Target == ChildId).RecoveryEligible);
        Assert.False(approved.Reads.Single(read => read.Target == otherId).RecoveryEligible);
        Assert.Equal(originalAttempt, approved.Reads.Single(read => read.Target == ChildId).Attempts.Single());
        Assert.Equal(originalReceipt, File.ReadAllBytes(Path.Combine(path, originalAttempt.ResultFile!)));
        var retry = session.ClaimSource("retry", "profile", [RootId, ChildId]);
        Assert.Equal([ChildId], retry.Targets);
        FinishSource(session, retry, Ok(new JsonObject()));
        var after = session.SourceStatus();
        Assert.Single(after.Reads.Single(read => read.Target == RootId).Attempts);
        Assert.Equal(2, after.Reads.Single(read => read.Target == ChildId).Attempts.Count);
        Assert.Equal("failed", after.Reads.Single(read => read.Target == otherId).State);
        Assert.Null(session.ClaimSource("unapproved-retry", "profile", [otherId]).ClaimId);
    }

    [Fact]
    public async Task Cli_matching_error_recovery_records_the_exact_saved_error_and_retains_other_failures()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var matching = session.ClaimSource("matching", "profile", [RootId]);
        var different = session.ClaimSource("different", "profile", [ChildId]);
        FinishSource(session, matching, new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "Fictitious indexed evidence could not be opened." }
        });
        FinishSource(session, different, new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "A different fictitious failure." }
        });
        var original = session.SourceStatus().Reads.Single(read => read.Target == RootId).Attempts.Single();

        const string reason = "User approved one retry per occurrence of this exact error for this run.";
        var result = await RunCli("source-authorize-matching-error-recovery", path, "profile", RootId, reason);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var authorization = JsonNode.Parse(result.Output)!["matchingErrorRecovery"]!;
        Assert.Equal("profile", authorization["kind"]!.GetValue<string>());
        Assert.Equal(original.Error, authorization["error"]!.GetValue<string>());
        Assert.Equal(reason, authorization["reason"]!.GetValue<string>());
        session.BeginSourceRecovery();
        var ledger = session.SourceStatus();
        Assert.True(ledger.Reads.Single(read => read.Target == RootId).RecoveryEligible);
        Assert.False(ledger.Reads.Single(read => read.Target == ChildId).RecoveryEligible);
        Assert.Equal(original, ledger.Reads.Single(read => read.Target == RootId).Attempts.Single());
        FinishSource(session, session.ClaimSource("retry", "profile", [RootId]), Ok(new JsonObject()));
        Assert.Null(session.ClaimSource("unapproved-retry", "profile", [ChildId]).ClaimId);
    }

    [Fact]
    public async Task Cli_not_found_recovery_grants_one_approved_download_retry_without_reclassifying_absence()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        var target = $"/users/{ChildId}/photo/$value";
        FinishSource(session, session.ClaimSource("photo", "photo", [target]), new JsonObject
        {
            ["statusCode"] = 404, ["error"] = new JsonObject { ["code"] = "ErrorNonExistentStorage" }
        });
        var original = session.SourceStatus().Reads.Single().Attempts.Single();
        Assert.False(original.Retryable);
        Assert.Equal("failed", session.SourceStatus().Reads.Single().State);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());

        const string reason = "User explicitly approved one retry of this not-found photo download.";
        var result = await RunCli("source-authorize-not-found-recovery", path, "photo", target, reason);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(reason, JsonNode.Parse(result.Output)!["notFoundRecovery"]!["reason"]!.GetValue<string>());
        Assert.Equal(original, session.SourceStatus().Reads.Single().Attempts.Single());
        Assert.Equal("failed", session.SourceStatus().Reads.Single().State);
        Assert.True(session.SourceStatus().Reads.Single().RecoveryEligible);
        FinishSource(session, session.ClaimSource("retry", "photo", [target]), Ok(new JsonObject()));
        Assert.Equal("completed", session.SourceStatus().Reads.Single().State);
        Assert.Equal(2, session.SourceStatus().Reads.Single().Attempts.Count);
        Assert.Null(session.ClaimSource("replay", "photo", [target]).ClaimId);
    }

    [Fact]
    public async Task Cli_photo_omission_preserves_failed_receipts_and_existing_photo_without_waiving_profiles()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var child = User(ChildId, "Example Child");
        Ingest(session, (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(child) })),
            (ExportSession.UserPath(ChildId), Ok(User(ChildId, "Example Child"))),
            (ExportSession.ManagerPath(ChildId), Ok(User(RootId))),
            (ExportSession.ReportsPath(ChildId), Empty()),
            (ExportSession.PhotoPath(RootId), Metadata("v1")),
            (ExportSession.PhotoPath(ChildId), Missing("ImageNotFound")));
        var rootPerson = session.Plan().People.Single(person => person.Id == RootId);
        session.Profile(RootId, ProfileInput(session, rootPerson));
        var target = $"/users/{RootId}/photo/$value";
        var failure = Missing("ErrorNonExistentStorage");
        FinishSource(session, session.ClaimSource("photo", "photo", [target]), failure);
        session.AuthorizeNotFoundSourceRecovery("photo", target, "Explicit approval for the fictitious retry.");
        FinishSource(session, session.ClaimSource("photo-retry", "photo", [target]), failure);
        var attempts = session.SourceStatus().Reads.Single(read => read.Target == target).Attempts.ToArray();
        var receipts = attempts.Select(attempt => attempt.ResultFile!).Distinct().ToDictionary(file => file,
            file => File.ReadAllBytes(Path.Combine(path, file)));
        Assert.Throws<InvalidOperationException>(() => session.Publish());

        const string reason = "User approved omitting only this exhausted photo while preserving the saved app photo.";
        var omitted = await RunCli("source-omit-photo", path, RootId, reason);

        Assert.Equal(0, omitted.ExitCode);
        Assert.Empty(omitted.Error);
        Assert.Equal(reason, JsonNode.Parse(omitted.Output)!["photoOmission"]!["reason"]!.GetValue<string>());
        var read = session.SourceStatus().Reads.Single(read => read.Target == target);
        Assert.Equal("failed", read.State);
        Assert.Equal(attempts, read.Attempts);
        Assert.Null(session.ClaimSource("no-third-download", "photo", [target]).ClaimId);
        Assert.True(JsonSerializer.SerializeToNode(session.ReusePhoto(RootId))!["omitted"]!.GetValue<bool>());
        Assert.Contains("Source read is not completed", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
        var childPerson = session.Plan().People.Single(person => person.Id == ChildId);
        session.Profile(ChildId, ProfileInput(session, childPerson));
        var published = JsonSerializer.SerializeToNode(session.Publish())!;
        var import = published["importPath"]!.GetValue<string>();
        var destination = Path.GetDirectoryName(import)!;
        var batch = JsonNode.Parse(File.ReadAllText(import))!;
        Assert.Equal(2, batch["people"]!.AsArray().Count);
        Assert.All(batch["people"]!.AsArray(), person => Assert.False(person!.AsObject().ContainsKey("photoPath")));
        Assert.Equal(1, published["omittedPhotos"]!.GetValue<int>());
        Assert.Equal(1, published["unavailablePhotos"]!.GetValue<int>());
        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "coverage.json")))!;
        Assert.Equal(1, coverage["omittedPhotos"]!.GetValue<int>());
        Assert.Equal(1, coverage["unavailablePhotos"]!.GetValue<int>());
        Assert.Contains(reason, File.ReadAllText(Path.Combine(destination, "review-required.json")));
        foreach (var (receipt, bytes) in receipts)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(destination, receipt)));
        var retained = JsonSerializer.Deserialize<ExportSession.SourceLedger>(
            File.ReadAllText(Path.Combine(destination, "source-coordination.json")), ExportSession.Json)!;
        Assert.Equal("failed", retained.Reads.Single(item => item.Target == target).State);
        Assert.Equal(attempts, retained.Reads.Single(item => item.Target == target).Attempts);

        using var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("fictitious-photo-omission"));
        File.WriteAllBytes(Path.Combine(root, "saved.png"), Png);
        var prior = Path.Combine(root, "prior-photo.json");
        Save(prior, new
        {
            schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-01-01T00:00:00Z",
            people = new[] { new { email = "person1@example.test", displayName = "Example Person",
                managerEmail = (string?)null, photoPath = "saved.png" } }
        });
        var priorPreview = app.PreviewImportFile(prior);
        Assert.True(priorPreview.CanApply, string.Join("; ", priorPreview.Errors));
        Assert.True(app.ApplyImport(priorPreview.Id), app.Status);
        Assert.Equal(Png, app.GetPhoto("person1@example.test")!.Bytes.ToArray());
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        Assert.Equal(Png, app.GetPhoto("person1@example.test")!.Bytes.ToArray());
        Assert.Null(app.GetPhoto("person2@example.test"));
    }

    [Fact]
    public void Photo_omission_requires_explicit_immutable_approval_and_an_exhausted_settled_download()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(RootId, " "));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(ChildId, "Outside this population."));
        var target = $"/users/{RootId}/photo/$value";
        var first = session.ClaimSource("photo", "photo", [target]);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(RootId, "Still active."));
        var failure = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" } };
        FinishSource(session, first, failure);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(RootId, "Not exhausted."));
        session.BeginSourceRecovery();
        FinishSource(session, session.ClaimSource("retry", "photo", [target]), failure);
        var approval = session.AuthorizePhotoOmission(RootId, "Explicit approval for this exhausted photo.");
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(RootId, "Replacement approval."));
        var read = session.SourceStatus().Reads.Single(read => read.Target == target);
        Assert.Equal(approval, read.PhotoOmission);
        Assert.True(read.AccountedFor);
        Assert.Equal("failed", read.State);
        Assert.Equal(2, read.Attempts.Count);
    }

    [Fact]
    public void An_exhausted_photo_cannot_be_omitted_without_fresh_successful_metadata()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        var target = $"/users/{RootId}/photo/$value";
        var failure = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" } };
        FinishSource(session, session.ClaimSource("photo", "photo", [target]), failure);
        session.BeginSourceRecovery();
        FinishSource(session, session.ClaimSource("retry", "photo", [target]), failure);
        Assert.Contains("Fresh photo metadata is missing",
            Assert.Throws<InvalidOperationException>(() => session.AuthorizePhotoOmission(RootId, "Approval.")).Message);
        var read = session.SourceStatus().Reads.Single(read => read.Target == target);
        Assert.Null(read.PhotoOmission);
        Assert.False(read.AccountedFor);
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

    [Theory]
    [InlineData("profile")]
    [InlineData("metadata")]
    [InlineData("photo")]
    public async Task Cli_best_effort_unavailable_lookups_publish_verified_directory_fields_and_preserve_saved_values(string kind)
    {
        var path = Seed();
        var enabled = await RunCli("source-authorize-best-effort", path, "Owner-approved best-effort collection.");
        Assert.Equal(0, enabled.ExitCode);
        Assert.Empty(enabled.Error);
        using var session = ExportSession.Open(path);
        var failure = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" } };
        if (kind == "metadata")
        {
            Ingest(session, (ExportSession.PhotoPath(RootId), (JsonObject)failure.DeepClone()));
            Ingest(session, (ExportSession.PhotoPath(RootId), (JsonObject)failure.DeepClone()));
        }
        else Ingest(session, (ExportSession.PhotoPath(RootId), kind == "photo" ? Metadata("v2") : Missing("ImageNotFound")));
        if (kind == "profile")
        {
            FinishSource(session, session.ClaimSource("profile", "profile", [RootId]), failure);
            Assert.Single(JsonSerializer.SerializeToNode(session.Jobs())!["jobFiles"]!.AsArray());
            FinishSource(session, session.ClaimSource("profile-retry", "profile", [RootId]), failure);
        }
        else RecordProfile(session);
        if (kind == "photo")
        {
            var target = $"/users/{RootId}/photo/$value";
            FinishSource(session, session.ClaimSource("photo", "photo", [target]), Missing("ImageNotFound"));
            FinishSource(session, session.ClaimSource("photo-retry", "photo", [target]), Missing("ImageNotFound"));
        }
        if (kind != "profile")
            Assert.True(JsonSerializer.SerializeToNode(session.ReusePhoto(RootId))!["lookupUnavailable"]!.GetValue<bool>());

        var published = JsonSerializer.SerializeToNode(session.Publish())!;
        var import = published["importPath"]!.GetValue<string>();
        var person = JsonNode.Parse(File.ReadAllText(import))!["people"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Example Engineer", person["jobTitle"]!.GetValue<string>());
        Assert.False(person.ContainsKey("officeState"));
        Assert.False(person.ContainsKey("officeCountry"));
        Assert.False(person.ContainsKey("photoPath"));
        Assert.Equal(kind == "profile" ? 1 : 0, published["unavailableProfileLookups"]!.GetValue<int>());
        Assert.Equal(kind == "profile" ? 0 : 1, published["unavailablePhotoLookups"]!.GetValue<int>());
        Assert.Equal(kind == "profile" ? 1 : 0, published["unavailablePhotos"]!.GetValue<int>());
        var folder = Path.GetDirectoryName(import)!;
        var coverage = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "coverage.json")))!;
        Assert.Equal("degraded", coverage["enrichmentStatus"]!.GetValue<string>());
        Assert.Contains(kind == "photo" ? "ImageNotFound" : "CircuitOpen",
            File.ReadAllText(Path.Combine(folder, "unavailable-enrichment.json")));

        using var app = ApplicationWorkflow.CreateInMemory();
        Assert.True(app.OpenWorkspace("best-effort-preservation"));
        File.WriteAllBytes(Path.Combine(root, "prior.png"), Png);
        var prior = Path.Combine(root, "prior.json");
        Save(prior, new
        {
            schemaVersion = 1, batchId = Guid.NewGuid(), observedAt = "2026-01-01T00:00:00Z",
            people = new[] { new { email = "person1@example.test", displayName = "Prior name",
                managerEmail = (string?)null, officeState = "Prior state", officeCountry = "Prior country", photoPath = "prior.png" } }
        });
        Assert.True(app.ApplyImport(app.PreviewImportFile(prior).Id), app.Status);
        Assert.True(app.ApplyImport(app.PreviewImportFile(import).Id), app.Status);
        var saved = app.People.Single(person => person.Email == "person1@example.test");
        Assert.Equal("Prior state", saved.OfficeState);
        Assert.Equal("Prior country", saved.OfficeCountry);
        Assert.Equal(Png, app.GetPhoto("person1@example.test")!.Bytes.ToArray());
    }

    [Fact]
    public void Unavailable_downloads_cannot_substitute_for_an_unattempted_photo_metadata_lookup()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        RecordProfile(session);
        var target = $"/users/{RootId}/photo/$value";
        var failure = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" } };
        FinishSource(session, session.ClaimSource("photo", "photo", [target]), failure);
        FinishSource(session, session.ClaimSource("retry", "photo", [target]), failure);
        Assert.True(session.SourceStatus().Reads.Single(read => read.Kind == "photo").AccountedFor);

        Assert.Throws<InvalidOperationException>(() => session.Publish());

        Ingest(session, (ExportSession.PhotoPath(RootId), Metadata("v1")));
        var published = JsonSerializer.SerializeToNode(session.Publish())!;
        Assert.Equal(1, published["unavailablePhotoLookups"]!.GetValue<int>());
        Assert.Equal(0, published["unavailablePhotos"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("MCP error -32603: ", 403, false, false)]
    [InlineData("MCP server 'workiq': ", 403, false, false)]
    [InlineData("MCP error -32603: ", 429, false, false)]
    [InlineData("MCP server 'workiq': ", 429, false, false)]
    [InlineData("MCP error -32603: ", 429, true, false)]
    [InlineData("MCP server 'workiq': ", 429, true, false)]
    [InlineData("MCP error -32603: ", 403, false, true)]
    [InlineData("MCP server 'workiq': ", 403, false, true)]
    [InlineData("MCP error -32603: ", 429, false, true)]
    [InlineData("MCP server 'workiq': ", 429, false, true)]
    [InlineData("MCP error -32603: ", 429, true, true)]
    [InlineData("MCP server 'workiq': ", 429, true, true)]
    public void Rejected_prefixed_native_captures_preserve_numeric_denial_and_retry_delay_diagnostics(
        string prefix, int status, bool invalidDelay, bool incomplete)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        string[] targets = incomplete ? [ExportSession.UserPath(RootId), ExportSession.UserPath(ChildId)] : [RootId];
        var kind = incomplete ? "structured" : "profile";
        var toolName = incomplete ? "workiq-fetch" : "workiq-retrieve";
        var claim = session.ClaimSource("worker", kind, targets);
        var events = Path.Combine(root, "prefixed-diagnostics.jsonl");
        File.WriteAllText(events, "");
        object arguments = incomplete ? new { entityUrls = targets } : new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "prefixed-diagnostics-request.json");
        Save(request, new { toolName, arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "prefixed", toolName, arguments });
        var failure = new { statusCode = status, retryAfterSeconds = invalidDelay ? -1 : 17 };
        var payload = incomplete ? JsonSerializer.Serialize(new { results = new[] { failure } }) : JsonSerializer.Serialize(new
        {
            isError = true, content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(failure) },
                new { type = "text", text = "Unsupported multipart transport." }
            }
        });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "prefixed", success = false,
            error = new { message = prefix + payload } });

        session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed without a supported complete capture.");

        var ledger = session.SourceStatus();
        var read = ledger.Reads.Single(read => read.Target == targets[0]);
        Assert.Null(read.Attempts.Single().ResultFile);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "response.json")));
        Assert.Equal(status == 403 || invalidDelay, ledger.StopReason is not null);
        Assert.False(read.AccountedFor);
        Assert.Null(session.ClaimSource("unrelated", "profile", [ChildId]).ClaimId);
        var retry = session.ClaimSource("retry", kind, targets);
        Assert.Null(retry.ClaimId);
        if (status == 429 && !invalidDelay)
        {
            Assert.True(read.RecoveryEligible);
            Assert.NotNull(read.Attempts.Single().RetryAt);
            Assert.Equal(read.Attempts.Single().RetryAt, retry.RetryAt);
        }
        else Assert.False(read.RecoveryEligible);
    }

    [Fact]
    public void Best_effort_capture_failures_are_bound_to_claims_and_never_ingest_unverified_payloads()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        var saved = Path.Combine(root, "unbound-payload.json");
        Save(saved, new { officeState = "Unverified state" });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var claim = session.ClaimSource("profile", "profile", [RootId]);
            var events = Path.Combine(root, $"failed-capture-{attempt}.jsonl");
            File.WriteAllText(events, "");
            var arguments = new { query = "Fictitious exact work profile " + RootId };
            var request = Path.Combine(root, $"capture-request-{attempt}.json");
            Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
            var ticket = session.BeginCapture(events, request);
            AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = "workiq-retrieve", arguments });
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = true,
                result = new { content = "Output too large to display.\nFull output saved to: " + saved } });
            Assert.Throws<InvalidOperationException>(() => session.Capture(ticket));
            Assert.Throws<InvalidOperationException>(() => session.FailCapture(Guid.NewGuid(), ticket, "Wrong claim."));

            session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed; bounded recapture did not recover it.");

            var read = session.SourceStatus().Reads.Single(read => read.Kind == "profile");
            Assert.Equal(attempt + 1, read.Attempts.Count);
            Assert.True(File.Exists(Path.Combine(path, read.Attempts[^1].CaptureFailureFile!)));
            Assert.Null(read.Attempts[^1].ResultFile);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ticket)!, "response.json")));
        }
        Assert.True(session.SourceStatus().Reads.Single(read => read.Kind == "profile").AccountedFor);
        Assert.Null(session.ClaimSource("third", "profile", [RootId]).ClaimId);
        var published = JsonSerializer.SerializeToNode(session.Publish())!;
        var import = published["importPath"]!.GetValue<string>();
        Assert.DoesNotContain("Unverified state", File.ReadAllText(import));
        Assert.Equal(1, published["unavailableProfileLookups"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_failure_settles_its_claim_when_unrelated_capacity_deferred_reads_have_no_attempts(bool bestEffort)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        if (bestEffort) session.AuthorizeBestEffortEnrichment("Standing policy.");
        var failed = session.ClaimSource("failed", "profile", [RootId]);
        var other = session.ClaimSource("other", "profile", [ChildId]);
        Assert.Null(session.ClaimSource("deferred", "profile", [AltId]).ClaimId);
        var events = Path.Combine(root, "deferred-capture.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "deferred-capture-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = failed.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "failed", toolName = "workiq-retrieve", arguments });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "failed", success = false,
            error = new { message = "Unsupported incomplete source response." } });

        var result = session.FailCapture(failed.ClaimId!.Value, ticket, "Original invocation completed without a usable result.");

        Assert.Equal(failed.ClaimId, result.ClaimId);
        Assert.Equal(RootId, Assert.Single(result.Reads).Target);
        using var reopened = ExportSession.Open(path);
        var ledger = reopened.SourceStatus();
        var settled = ledger.Reads.Single(read => read.Target == RootId);
        Assert.Equal("failed", settled.State);
        Assert.Equal(bestEffort, settled.RecoveryEligible);
        Assert.NotNull(settled.Attempts.Single().FinishedAt);
        Assert.True(File.Exists(Path.Combine(path, settled.Attempts.Single().CaptureFailureFile!)));
        var unchanged = ledger.Reads.Single(read => read.Target == ChildId);
        Assert.Equal("active", unchanged.State);
        Assert.Equal(other.ClaimId, unchanged.Attempts.Single().ClaimId);
        var deferred = ledger.Reads.Single(read => read.Target == AltId);
        Assert.Equal("deferred", deferred.State);
        Assert.Empty(deferred.Attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Capture_uses_complete_JSONL_records_without_parsing_an_uncommitted_shared_tail(bool crlf, bool bom)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "uncommitted-tail.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "uncommitted-tail-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        if (bom) File.WriteAllBytes(events, [0xEF, 0xBB, 0xBF]);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = "workiq-retrieve", arguments });
        var payload = JsonSerializer.Serialize(new { citations = Array.Empty<string>(), padding = new string('x', 100000) });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = true,
            result = new { content = payload } });
        if (crlf) File.WriteAllText(events, File.ReadAllText(events).Replace("\n", "\r\n", StringComparison.Ordinal));
        File.AppendAllText(events, "{\"type\":\"unrelated.partial\",\"data\":");

        var capture = session.Capture(ticket);

        Assert.Equal("profile", capture.ToolCallId);
        Assert.Equal(payload, File.ReadAllText(capture.ResponseFile));
        File.AppendAllText(events, "{}}\n");
        Assert.Equal(capture.ToolCallId, session.Capture(ticket).ToolCallId);
        FinishSource(session, claim, Ok(new JsonObject()));
        Assert.Single(session.SourceStatus().Reads.Single().Attempts);
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(429, true)]
    public void Capture_failure_retains_correlated_denial_and_delays_before_an_uncommitted_JSONL_tail(int status, bool invalidDelay)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "diagnostic-tail.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "diagnostic-tail-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = "workiq-retrieve", arguments });
        var content = JsonSerializer.Serialize(new
        {
            isError = true, content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(new { statusCode = status, retryAfterSeconds = invalidDelay ? -1 : 17 }) },
                new { type = "text", text = "Unsupported multipart transport." }
            }
        });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = false, result = new { content } });
        File.AppendAllText(events, "{\"type\":\"unrelated.partial\",\"data\":");

        session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed without a usable multipart response.");

        var ledger = session.SourceStatus();
        var read = ledger.Reads.Single();
        Assert.Equal(status == 403 || invalidDelay, ledger.StopReason is not null);
        Assert.Null(session.ClaimSource("unrelated", "profile", [ChildId]).ClaimId);
        Assert.Null(session.ClaimSource("retry", "profile", [RootId]).ClaimId);
        if (status == 429 && !invalidDelay)
        {
            Assert.True(read.RecoveryEligible);
            Assert.NotNull(read.Attempts.Single().RetryAt);
        }
        else Assert.False(read.RecoveryEligible);
        Assert.Null(read.Attempts.Single().ResultFile);
    }

    [Fact]
    public void Capture_failure_does_not_authorize_replacement_when_correlated_diagnostics_cannot_be_inspected_safely()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "uninspectable-history.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "uninspectable-history-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = "workiq-retrieve", arguments });
        File.AppendAllText(events, "{not-valid-json}\n");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = false,
            result = new { content = "{\"statusCode\":403}" } });

        Assert.Throws<InvalidOperationException>(() =>
            session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed but its history cannot be inspected safely."));

        var read = session.SourceStatus().Reads.Single();
        Assert.Equal("active", read.State);
        Assert.False(read.RecoveryEligible);
        Assert.Null(read.Attempts.Single().FinishedAt);
        Assert.Null(read.Attempts.Single().CaptureFailureFile);
        Assert.Null(session.ClaimSource("replacement", "profile", [RootId]).ClaimId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Photo_not_found_recovery_uses_the_successful_metadata_attempt_after_metadata_recovery(bool captureFailure)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var metadataPath = ExportSession.PhotoPath(RootId);
        var first = session.ClaimSource("metadata", "structured", [metadataPath]);
        if (captureFailure)
        {
            var events = Path.Combine(root, "metadata-capture.jsonl");
            File.WriteAllText(events, "");
            var arguments = new { entityUrls = new[] { metadataPath } };
            var request = Path.Combine(root, "metadata-capture-request.json");
            Save(request, new { toolName = "workiq-fetch", arguments, claimId = first.ClaimId });
            var ticket = session.BeginCapture(events, request);
            AppendEvent(events, "tool.execution_start", new { toolCallId = "metadata", toolName = "workiq-fetch", arguments });
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "metadata", success = true,
                result = new { content = "{\"results\":[]}" } });
            session.FailCapture(first.ClaimId!.Value, ticket, "Original metadata invocation returned an incomplete batch.");
        }
        else FinishSource(session, first,
            new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" } });
        var original = session.SourceStatus().Reads.Single().Attempts.Single();
        FinishSource(session, session.ClaimSource("metadata-retry", "structured", [metadataPath]), Metadata("v1"));
        var photoPath = $"/users/{RootId}/photo/$value";

        FinishSource(session, session.ClaimSource("photo", "photo", [photoPath]), Missing("ImageNotFound"));

        var photo = session.SourceStatus().Reads.Single(read => read.Kind == "photo");
        Assert.Equal("failed", photo.State);
        Assert.True(photo.RecoveryEligible);
        Assert.False(photo.AccountedFor);
        Assert.Equal(original, session.SourceStatus().Reads.Single(read => read.Target == metadataPath).Attempts[0]);
        FinishSource(session, session.ClaimSource("photo-retry", "photo", [photoPath]), Missing("ImageNotFound"));
        Assert.True(session.SourceStatus().Reads.Single(read => read.Kind == "photo").AccountedFor);
        Assert.Equal(2, session.SourceStatus().Reads.Single(read => read.Kind == "photo").Attempts.Count);
    }

    [Fact]
    public void A_late_verified_capture_must_be_used_instead_of_discarded_for_a_retry()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "late.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "late-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "late", toolName = "workiq-retrieve", arguments });
        Assert.Throws<InvalidOperationException>(() => session.Capture(ticket));
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "late", success = true,
            result = new { content = "{\"citations\":[]}" } });

        Assert.Contains("complete", Assert.Throws<InvalidOperationException>(() =>
            session.FailCapture(claim.ClaimId!.Value, ticket, "Completion arrived during diagnosis.")).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active", session.SourceStatus().Reads.Single().State);
        Assert.Null(session.SourceStatus().Reads.Single().Attempts.Single().CaptureFailureFile);
        FinishSource(session, claim, Ok(new JsonObject()));
        Assert.Null(session.ClaimSource("replay", "profile", [RootId]).ClaimId);
    }

    [Fact]
    public void Best_effort_still_requires_profile_coverage_and_rejects_unresolved_local_observation_errors()
    {
        var path = Seed();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        Ingest(session, (ExportSession.PhotoPath(RootId), Missing("ImageNotFound")));
        Assert.Contains("Source read is not completed", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
        var person = session.Plan().People.Single();
        var input = ProfileInput(session, person);
        var observation = JsonSerializer.Deserialize<ExportSession.Observation>(File.ReadAllText(input), ExportSession.Json)!;
        Save(input, observation with { Email = "wrong@example.test" });
        Assert.Throws<InvalidOperationException>(() => session.Profile(RootId, input));
        Assert.Contains("Unresolved profile inputs", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
        Save(input, observation);
        session.Profile(RootId, input);
        Assert.NotNull(JsonSerializer.SerializeToNode(session.Publish())!["importPath"]);
    }

    [Fact]
    public void Unsupported_capture_retains_an_actual_retry_delay_without_trusting_its_payload()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "delayed-capture.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "delayed-capture-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "delayed", toolName = "workiq-retrieve", arguments });
        var content = JsonSerializer.Serialize(new
        {
            isError = true, content = new[]
            {
                new { type = "text", text = "{\"statusCode\":429,\"retryAfterSeconds\":17,\"error\":{\"code\":\"TooManyRequests\"}}" },
                new { type = "text", text = "Unsupported multipart transport." }
            }
        });
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "delayed", success = false, result = new { content } });

        session.FailCapture(claim.ClaimId!.Value, ticket, "Invocation completed with an unsupported multipart response.");

        var read = session.SourceStatus().Reads.Single();
        Assert.NotNull(read.Attempts.Single().RetryAt);
        Assert.Null(read.Attempts.Single().ResultFile);
        var unrelated = session.ClaimSource("unrelated", "profile", [ChildId]);
        Assert.Null(unrelated.ClaimId);
        Assert.Equal(read.Attempts.Single().RetryAt, unrelated.RetryAt);
        Assert.Contains("retry delay", unrelated.Blocker);
    }

    [Fact]
    public void Exhausted_required_capture_keeps_reporting_unverified_while_unrelated_reads_can_continue()
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var target = ExportSession.UserPath(RootId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var claim = session.ClaimSource("directory", "structured", [target]);
            var events = Path.Combine(root, $"required-capture-{attempt}.jsonl");
            File.WriteAllText(events, "");
            var arguments = new { entityUrls = new[] { target } };
            var request = Path.Combine(root, $"required-request-{attempt}.json");
            Save(request, new { toolName = "workiq-fetch", arguments, claimId = claim.ClaimId });
            var ticket = session.BeginCapture(events, request);
            AppendEvent(events, "tool.execution_start", new { toolCallId = "directory", toolName = "workiq-fetch", arguments });
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "directory", success = true,
                result = new { content = "{\"results\":[]}" } });
            session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed without a complete result.");
        }

        var required = session.SourceStatus().Reads.Single();
        Assert.Equal(2, required.Attempts.Count);
        Assert.False(required.AccountedFor);
        Assert.Null(required.Unavailable);
        var unrelated = session.ClaimSource("profile", "profile", [ChildId]);
        Assert.NotNull(unrelated.ClaimId);
        FinishSource(session, unrelated, Ok(new JsonObject()));
        Assert.Contains("Source collection is incomplete", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_failure_diagnostics_preserve_an_observed_denial_instead_of_retrying_or_omitting(bool multipart)
    {
        var path = NewRun();
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var claim = session.ClaimSource("profile", "profile", [RootId]);
        var events = Path.Combine(root, "denied-capture.jsonl");
        File.WriteAllText(events, "");
        var arguments = new { query = "Fictitious work profile " + RootId };
        var request = Path.Combine(root, "denied-capture-request.json");
        Save(request, new { toolName = "workiq-retrieve", arguments, claimId = claim.ClaimId });
        var ticket = session.BeginCapture(events, request);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "denied", toolName = "workiq-retrieve", arguments });
        var content = multipart ? JsonSerializer.Serialize(new
        {
            isError = true, content = new[] { new { type = "text", text = "HTTP 403" }, new { type = "text", text = "Forbidden" } }
        }) : "Incomplete response";
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "denied", success = false,
            error = new { message = multipart ? "Tool failed." : "HTTP 403 Forbidden" }, result = new { content } });

        session.FailCapture(claim.ClaimId!.Value, ticket, "Original invocation completed with an unsupported response.");

        var ledger = session.SourceStatus();
        Assert.Contains("denial", ledger.StopReason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(ledger.Reads.Single().AccountedFor);
        Assert.Null(session.ClaimSource("retry", "profile", [RootId]).ClaimId);
        Assert.Null(session.ClaimSource("other", "profile", [ChildId]).ClaimId);
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
