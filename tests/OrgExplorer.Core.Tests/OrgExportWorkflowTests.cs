using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.OrgExport;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed partial class OrgExportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Atomic_start_enables_standing_recovery_before_the_first_failed_read(int workers)
    {
        var started = ExportSession.Start("Fictitious root", RootId, AccountId, workers, root);
        using var session = ExportSession.Open(started.RunPath);
        Assert.NotNull(session.SourceStatus().BestEffortEnrichment);
        FinishSource(session, session.ClaimSource("first", "profile", [RootId]), new JsonObject { ["statusCode"] = 503 });
        var retry = session.ClaimSource("retry", "profile", [RootId]);
        Assert.NotNull(retry.ClaimId);
        FinishSource(session, retry, new JsonObject { ["statusCode"] = 503 });
        Assert.Equal("unavailable", session.SourceStatus().Reads.Single().State);
        Assert.NotNull(session.ClaimSource("unrelated", "profile", [ChildId]).ClaimId);
        var state = JsonSerializer.Deserialize<ExportSession.Run>(
            File.ReadAllText(Path.Combine(started.RunPath, "state.json")), ExportSession.Json)!;
        Assert.Equal(workers, state.WorkerLimit);
        Assert.Equal(started.Collector, state.Collector);
        Assert.Equal(64, started.Collector.AssemblySha256.Length);
        Assert.Equal(64, started.Collector.ProtocolSha256.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_upgrade_preserves_unversioned_policy_attempts_and_original_bytes(bool bestEffort)
    {
        var path = bestEffort ? ExportSession.Start("Fictitious root", RootId, AccountId, importsRoot: root).RunPath : NewRun();
        using (var session = ExportSession.Open(path))
        {
            FinishSource(session, session.ClaimSource("first", "profile", [RootId]), new JsonObject { ["statusCode"] = 503 });
            if (!bestEffort) session.BeginSourceRecovery();
            FinishSource(session, session.ClaimSource("retry", "profile", [RootId]), new JsonObject { ["statusCode"] = 503 });
        }
        var originals = MakeUnversioned(path);
        var before = JsonNode.Parse(originals.Sources)!.AsObject();
        Assert.Contains("upgrade-run", Assert.Throws<InvalidOperationException>(() => ExportSession.Open(path)).Message);

        ExportSession.UpgradeRun(path, "Resume this stopped run with explicit versioned state.");

        Assert.Equal(originals.Run, File.ReadAllBytes(Path.Combine(path, "state-upgrade", "state.original.json")));
        Assert.Equal(originals.Sources, File.ReadAllBytes(Path.Combine(path, "state-upgrade", "sources.original.json")));
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "source-coordination.json")))!;
        Assert.True(JsonNode.DeepEquals(before["reads"], after["reads"]));
        Assert.True(JsonNode.DeepEquals(before["bestEffortEnrichment"], after["bestEffortEnrichment"]));
        Assert.True(JsonNode.DeepEquals(before["recoveryConsumed"], after["recoveryConsumed"]));
        using var resumed = ExportSession.Open(path);
        Assert.Equal(2, resumed.SourceStatus().Reads.Single().Attempts.Count);
        Assert.Null(resumed.ClaimSource("third-attempt", "profile", [RootId]).ClaimId);
        Assert.Equal(bestEffort, resumed.SourceStatus().BestEffortEnrichment is not null);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("legacy-extension")]
    [InlineData("active")]
    [InlineData("sealed")]
    public void Upgrade_refuses_unsafe_or_unknown_states_without_overwriting_them(string fault)
    {
        var path = NewRun();
        if (fault == "active")
            using (var session = ExportSession.Open(path))
                Assert.NotNull(session.ClaimSource("unfinished", "profile", [RootId]).ClaimId);
        MakeUnversioned(path);
        var stateFile = Path.Combine(path, "state.json");
        var sourcesFile = Path.Combine(path, "source-coordination.json");
        if (fault == "future")
        {
            var state = JsonNode.Parse(File.ReadAllText(stateFile))!;
            state["stateVersion"] = ExportSession.CollectorStateVersion + 1;
            state["futureProperty"] = true;
            Save(stateFile, state);
        }
        if (fault == "legacy-extension")
        {
            var sources = JsonNode.Parse(File.ReadAllText(sourcesFile))!;
            sources["profileDeferrals"] = new JsonArray();
            Save(sourcesFile, sources);
        }
        if (fault == "sealed") File.WriteAllText(Path.Combine(path, ".sealed"), "{}");
        var originalState = File.ReadAllBytes(stateFile);
        var originalSources = File.ReadAllBytes(sourcesFile);

        Assert.NotNull(Record.Exception(() => ExportSession.UpgradeRun(path, "Explicit upgrade.")));

        Assert.Equal(originalState, File.ReadAllBytes(stateFile));
        Assert.Equal(originalSources, File.ReadAllBytes(sourcesFile));
        Assert.False(Directory.Exists(Path.Combine(path, "state-upgrade")));
    }

    [Fact]
    public void Upgrade_resumes_an_interrupted_replacement_from_its_recorded_targets()
    {
        var path = NewRun();
        using (var session = ExportSession.Open(path))
            FinishSource(session, session.ClaimSource("completed", "profile", [RootId]), Ok(new JsonObject()));
        var originals = MakeUnversioned(path);
        ExportSession.UpgradeRun(path, "Original upgrade authorization.");
        var journalFile = Path.Combine(path, "state-upgrade", "upgrade.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalFile))!;
        journal["completedAt"] = null;
        Save(journalFile, journal);
        File.WriteAllBytes(Path.Combine(path, "state.json"), originals.Run);

        ExportSession.UpgradeRun(path, "Resume the original journal without replacing its approval.");

        Assert.NotNull(JsonNode.Parse(File.ReadAllText(journalFile))!["completedAt"]);
        Assert.Equal("Original upgrade authorization.", JsonNode.Parse(File.ReadAllText(journalFile))!["reason"]!.GetValue<string>());
        using var sessionAfter = ExportSession.Open(path);
        Assert.Single(sessionAfter.SourceStatus().Reads.Single().Attempts);
        Assert.Null(sessionAfter.ClaimSource("replay", "profile", [RootId]).ClaimId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Persistent_job_leases_enforce_serial_or_two_worker_limits_without_duplicate_assignments(int workers)
    {
        var path = SeedWorkflow(33, workers);
        var dispatched = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            using var session = ExportSession.Open(path);
            return session.NextJob("worker-" + index);
        })));
        var granted = dispatched.Where(result => result.Job is not null).Select(result => result.Job!).ToArray();
        Assert.Equal(workers, granted.Length);
        Assert.Equal(workers, granted.Select(job => job.JobId).Distinct().Count());
        using var resumed = ExportSession.Open(path);
        foreach (var lease in granted) Assert.Null(resumed.NextJob(lease.Worker).Job);
        Assert.Equal(workers, resumed.SourceStatus().CollectionJobs.Count(job => job.State == "assigned"));
        Assert.Equal(33, resumed.SourceStatus().CollectionJobs.Sum(job => job.DirectoryIds.Length));
    }

    [Fact]
    public void Job_handoff_preserves_completed_source_work_and_rejects_active_or_late_owners()
    {
        var path = SeedWorkflow();
        using var session = ExportSession.Open(path);
        var original = session.NextJob("original").Job!;
        var person = JsonSerializer.Deserialize<ExportSession.Person[]>(File.ReadAllText(original.JobFile), ExportSession.Json)![0];
        var active = session.ClaimSource("original", "profile", [person.Id]);
        Assert.Throws<InvalidOperationException>(() => session.ResumeJob(original.JobId, "replacement", "Original worker stopped."));
        FinishSource(session, active, Ok(new JsonObject()));
        var originalAttempts = JsonSerializer.Serialize(session.SourceStatus().Reads.Single(read => read.Kind == "profile"
            && read.Target == person.Id).Attempts, ExportSession.Json);

        var replacement = session.ResumeJob(original.JobId, "replacement", "Original worker confirmed stopped after retaining its source result.");

        Assert.Equal(original.JobFile, replacement.JobFile);
        Assert.Equal(originalAttempts, JsonSerializer.Serialize(session.SourceStatus().Reads.Single(read => read.Kind == "profile"
            && read.Target == person.Id).Attempts, ExportSession.Json));
        Assert.Throws<InvalidOperationException>(() => session.FinishJob("original", original.JobId));
        CompleteFixtureJob(session, replacement);
        session.FinishJob("replacement", replacement.JobId);
        Assert.Single(session.SourceStatus().CollectionJobs.Single(job => job.JobId == original.JobId).Handoffs);
    }

    [Fact]
    public void Preparation_builds_one_exact_claim_bound_request_and_observation_uses_original_lookup_time()
    {
        var path = SeedWorkflow();
        using var session = ExportSession.Open(path);
        var lease = session.NextJob("worker").Job!;
        var people = JsonSerializer.Deserialize<ExportSession.Person[]>(File.ReadAllText(lease.JobFile), ExportSession.Json)!;
        var targets = Path.Combine(root, "workflow-profile-targets.json");
        Save(targets, new[] { people[0].Id });
        var events = Path.Combine(root, "workflow-native.jsonl");
        File.WriteAllText(events, "");
        Assert.Throws<InvalidOperationException>(() => session.PrepareSource("worker", "profile", targets, "workiq-retrieve", events));
        Assert.DoesNotContain(session.SourceStatus().Reads, read => read.State == "active");
        var prepared = session.PrepareSource("worker", "profile", targets, "workiq-retrieve", events, "native-worker");
        var request = prepared.Request!;
        Assert.Equal(request.Arguments.ToJsonString(), JsonNode.Parse(File.ReadAllText(prepared.DescriptorFile!))!["arguments"]!.ToJsonString());
        Assert.Contains(people[0].Id, request.Arguments["query"]!.GetValue<string>());
        Assert.Contains(people[0].Email, request.Arguments["query"]!.GetValue<string>());
        Assert.Equal("grounding", request.Arguments["strategy"]!.GetValue<string>());
        Assert.Equal("People", request.Arguments["capabilities"]![0]!["name"]!.GetValue<string>());
        AppendEvent(events, "tool.execution_start", new { toolCallId = "profile", toolName = request.ToolName,
            arguments = request.Arguments }, "native-worker");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "profile", success = true,
            result = new { content = "{\"hits\":[]}" } }, "native-worker");
        var captured = session.Capture(prepared.TicketFile!);
        FinishSource(session, prepared.Claim, Ok(JsonNode.Parse(File.ReadAllText(captured.ResponseFile))!.AsObject()));
        var draft = Path.Combine(root, "workflow-fields.json");
        Save(draft, new ExportSession.ProfileDraft(UnknownFields()));

        session.RecordProfileDraft(people[0].Id, Path.Combine(Path.GetDirectoryName(prepared.TicketFile!)!, "capture.json"), draft);

        var observation = JsonSerializer.Deserialize<ExportSession.Observation>(
            File.ReadAllText(Path.Combine(path, "profiles", people[0].Id + ".json")), ExportSession.Json)!;
        Assert.Single(observation.EvidenceFiles);
        var actual = JsonNode.Parse(File.ReadAllText(captured.StartFile))!["timestamp"]!.GetValue<DateTimeOffset>();
        Assert.Equal(actual, observation.ObservedAt);
        Assert.Throws<InvalidOperationException>(() => session.RecordProfileDraft(people[1].Id,
            Path.Combine(Path.GetDirectoryName(prepared.TicketFile!)!, "capture.json"), draft));
        Assert.Throws<InvalidOperationException>(() => session.FinishJob("worker", lease.JobId));
    }

    [Fact]
    public void Preparation_enforces_the_host_batch_limit_before_spending_a_source_attempt()
    {
        var started = ExportSession.Start("Fictitious root", RootId, AccountId, importsRoot: root);
        using var session = ExportSession.Open(started.RunPath);
        var targets = Path.Combine(root, "too-many-workflow-targets.json");
        Save(targets, Enumerable.Range(1, 26).Select(index =>
            ExportSession.UserPath($"40000000-0000-0000-0000-{index:000000000000}")).ToArray());
        var events = Path.Combine(root, "too-many-events.jsonl");
        File.WriteAllText(events, "");
        Assert.Throws<InvalidOperationException>(() => session.PrepareSource("coordinator", "structured", targets, "workiq-fetch", events));
        Assert.Empty(session.SourceStatus().Reads);
    }

    [Fact]
    public void End_to_end_workflow_finishes_degraded_without_extra_approval_and_reports_sample_based_estimates()
    {
        var path = SeedWorkflow(unavailableMetadata: true);
        var clock = new CollectionClock(DateTimeOffset.UtcNow);
        using (var session = ExportSession.Open(path, sourceClock: clock))
        {
            var first = session.NextJob("first").Job!;
            var second = session.NextJob("second").Job!;
            Assert.Null(session.NextJob("third").Job);
            Assert.Null(JsonSerializer.SerializeToNode(session.Status())!["estimatedRemainingMinutes"]);
            CompleteFixtureJob(session, first);
            clock.Now = clock.Now.AddMinutes(2);
            session.FinishJob("first", first.JobId);
            var status = JsonSerializer.SerializeToNode(session.Status())!;
            Assert.True(status["estimatedRemainingMinutes"]!.GetValue<double>() > 0);
            Assert.Equal(1, status["photos"]!["unavailable"]!.GetValue<int>());
            CompleteFixtureJob(session, second, unavailableProfile: true);
            clock.Now = clock.Now.AddMinutes(1);
            session.FinishJob("second", second.JobId);
            Assert.Equal("publish", JsonSerializer.SerializeToNode(session.Status())!["phase"]!.GetValue<string>());
        }
        using var publisher = ExportSession.Open(path, publishing: true);
        var result = JsonSerializer.SerializeToNode(publisher.Publish())!;
        Assert.Equal(17, result["people"]!.GetValue<int>());
        Assert.Equal("degraded", result["enrichmentStatus"]!.GetValue<string>());
        Assert.Equal(1, result["unavailableProfileLookups"]!.GetValue<int>());
        Assert.Equal(1, result["unavailablePhotoLookups"]!.GetValue<int>());
        var people = JsonNode.Parse(File.ReadAllText(result["importPath"]!.GetValue<string>()))!["people"]!.AsArray();
        Assert.Equal(17, people.Count);
        Assert.All(people, person => Assert.False(person!.AsObject().ContainsKey("photoPath")));
    }

    [Fact]
    public async Task Cli_workflow_dispatches_prepares_records_hands_off_and_finishes_without_private_scripts()
    {
        var path = SeedWorkflow(people: 1);
        var dispatched = await RunCli("next-job", path, "cli-worker");
        Assert.True(dispatched.ExitCode == 0, dispatched.Error);
        var lease = JsonSerializer.Deserialize<ExportSession.JobDispatch>(dispatched.Output, ExportSession.Json)!.Job!;
        var events = Path.Combine(root, "cli-workflow.jsonl");
        File.WriteAllText(events, "");
        var preparedResult = await RunCli("prepare-next", path, "cli-worker", "profile", "workiq-retrieve", events, "native-cli-worker");
        Assert.True(preparedResult.ExitCode == 0, preparedResult.Error);
        var prepared = JsonSerializer.Deserialize<ExportSession.PreparedSource>(preparedResult.Output, ExportSession.Json)!;
        AppendEvent(events, "tool.execution_start", new { toolCallId = "cli-profile", toolName = prepared.Request!.ToolName,
            arguments = prepared.Request.Arguments }, "native-cli-worker");
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "cli-profile", success = true,
            result = new { content = "{\"hits\":[]}" } }, "native-cli-worker");
        using (var session = ExportSession.Open(path))
        {
            var captured = session.Capture(prepared.TicketFile!);
            FinishSource(session, prepared.Claim, Ok(JsonNode.Parse(File.ReadAllText(captured.ResponseFile))!.AsObject()));
        }
        var draft = Path.Combine(root, "cli-workflow-fields.json");
        Save(draft, new ExportSession.ProfileDraft(UnknownFields()));
        var recorded = await RunCli("record-profile", path, RootId,
            Path.Combine(Path.GetDirectoryName(prepared.TicketFile!)!, "capture.json"), draft);
        Assert.True(recorded.ExitCode == 0, recorded.Error);
        var resumed = await RunCli("resume-job", path, lease.JobId, "coordinator", "The original worker confirmed stopped after persisting its observation.");
        Assert.True(resumed.ExitCode == 0, resumed.Error);
        var stale = await RunCli("finish-job", path, "cli-worker", lease.JobId);
        Assert.Equal(1, stale.ExitCode);
        var finished = await RunCli("finish-job", path, "coordinator", lease.JobId);
        Assert.True(finished.ExitCode == 0, finished.Error);
        var status = await RunCli("status", path);
        Assert.True(status.ExitCode == 0, status.Error);
        var summary = JsonNode.Parse(status.Output)!;
        Assert.Equal("publish", summary["phase"]!.GetValue<string>());
        Assert.Equal(1, summary["jobs"]!["completed"]!.GetValue<int>());
        Assert.False(summary.AsObject().ContainsKey("reads"));
        using var check = ExportSession.Open(path);
        Assert.Single(check.SourceStatus().Reads.Single(read => read.Kind == "profile").Attempts);
    }

    [Fact]
    public async Task Cli_finishes_captured_structured_batches_without_rebuilding_response_payloads()
    {
        var path = NewRun();
        var events = Path.Combine(root, "finish-capture.jsonl");
        File.WriteAllText(events, "");
        var targets = Path.Combine(root, "finish-capture-targets.json");
        Save(targets, new[] { ExportSession.UserPath(RootId), ExportSession.ReportsPath(RootId) });
        ExportSession.PreparedSource prepared;
        using (var session = ExportSession.Open(path))
            prepared = session.PrepareSource("coordinator", "structured", targets, "workiq-fetch", events);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "batch", toolName = prepared.Request!.ToolName,
            arguments = prepared.Request.Arguments });
        var response = new JsonObject
        {
            ["results"] = new JsonArray(Ok(User(RootId)), new JsonObject { ["statusCode"] = 503 })
        }.ToJsonString();
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "batch", success = false,
            result = new { content = "Output too large to display.\nFull output saved to: unverified.json" },
            error = new { message = "MCP server 'workiq': " + response } });

        var result = await RunCli("finish-capture", path, prepared.TicketFile!);

        Assert.True(result.ExitCode == 0, result.Error);
        using var check = ExportSession.Open(path);
        Assert.Equal("completed", check.SourceStatus().Reads.Single(read => read.Target == ExportSession.UserPath(RootId)).State);
        Assert.Equal("failed", check.SourceStatus().Reads.Single(read => read.Target == ExportSession.ReportsPath(RootId)).State);
        Assert.Equal(503, check.Plan().Receipts[ExportSession.ReportsPath(RootId)].Response["statusCode"]!.GetValue<int>());
        Assert.All(check.SourceStatus().Reads, read => Assert.Single(read.Attempts));
    }

    [Fact]
    public void Directory_preparation_retains_the_eligible_failed_path_after_mixed_batch_ingestion()
    {
        var path = ExportSession.Start("Fictitious root", RootId, AccountId, importsRoot: root).RunPath;
        using var session = ExportSession.Open(path);
        var events = Path.Combine(root, "directory-retry.jsonl");
        File.WriteAllText(events, "");
        var targets = Path.Combine(root, "directory-retry-targets.json");
        Save(targets, new[] { ExportSession.UserPath(RootId), ExportSession.ReportsPath(RootId), ExportSession.ManagerPath(RootId) });
        var first = session.PrepareSource("coordinator", "structured", targets, "workiq-fetch", events);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "directory", toolName = first.Request!.ToolName,
            arguments = first.Request.Arguments });
        var responses = new JsonObject
        {
            ["results"] = new JsonArray(Ok(User(RootId)), new JsonObject { ["statusCode"] = 503 }, Missing("Request_ResourceNotFound"))
        }.ToJsonString();
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "directory", success = false,
            result = new { content = responses } });
        session.FinishCapturedSource(first.TicketFile!);
        Assert.Empty(session.Plan().Pending);
        Assert.NotEmpty(session.Plan().Errors);
        var summary = JsonSerializer.SerializeToNode(session.Status())!;
        Assert.Contains(summary["directoryWork"]!.AsArray(), target => target!.GetValue<string>() == ExportSession.ReportsPath(RootId));

        var retry = session.PrepareNextSource("coordinator", "directory", "workiq-fetch", events);

        Assert.NotNull(retry.Claim.ClaimId);
        Assert.Equal([ExportSession.ReportsPath(RootId)], retry.Claim.Targets);
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Target == ExportSession.UserPath(RootId)).Attempts);
        Assert.Equal(2, session.SourceStatus().Reads.Single(read => read.Target == ExportSession.ReportsPath(RootId)).Attempts.Count);
        var ledger = File.ReadAllBytes(Path.Combine(path, "source-coordination.json"));
        Assert.Throws<InvalidOperationException>(() => session.FinishCapturedSource(first.TicketFile!));
        Assert.Equal(ledger, File.ReadAllBytes(Path.Combine(path, "source-coordination.json")));
    }

    [Theory]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    [InlineData("photo", false)]
    [InlineData("photo", true)]
    public void Source_claims_reject_wrong_job_scope_and_stale_owners_without_spending_attempts(string kind, bool handedOff)
    {
        var path = SeedWorkflow(rootPhoto: true);
        using var session = ExportSession.Open(path);
        var original = session.NextJob("original").Job!;
        var other = session.NextJob("other").Job!;
        string Target(string id) => kind == "profile" ? id : $"/users/{id}/photo/$value";
        var originalTarget = Target(original.JobId);
        if (handedOff) session.ResumeJob(original.JobId, "replacement", "The original worker stopped before its source request.");
        var originalLedger = File.ReadAllBytes(Path.Combine(path, "source-coordination.json"));
        string[] targets = handedOff ? [originalTarget]
            : kind == "profile" ? [originalTarget, Target(other.JobId)] : [Target(other.JobId)];

        Assert.Throws<InvalidOperationException>(() => session.ClaimSource("original", kind, targets));
        Assert.Equal(originalLedger, File.ReadAllBytes(Path.Combine(path, "source-coordination.json")));
        var granted = session.ClaimSource(handedOff ? "replacement" : "original", kind, [originalTarget]);
        Assert.NotNull(granted.ClaimId);
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Kind == kind && read.Target == originalTarget).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finish_capture_resumes_local_materialization_after_source_completion_without_another_attempt(bool photo)
    {
        var path = photo ? SeedWorkflow(people: 1, rootPhoto: true) : NewRun();
        var events = Path.Combine(root, "resume-materialization.jsonl");
        File.WriteAllText(events, "");
        var targets = Path.Combine(root, "resume-materialization-targets.json");
        Save(targets, new[] { photo ? $"/users/{RootId}/photo/$value" : ExportSession.UserPath(RootId) });
        var blocker = photo ? Path.Combine(path, "photos", RootId + ".png") : Path.Combine(path, "receipts");
        ExportSession.PreparedSource prepared;
        using (var session = ExportSession.Open(path))
        {
            if (photo) session.NextJob("coordinator");
            prepared = session.PrepareSource("coordinator", photo ? "photo" : "structured", targets, "workiq-fetch", events);
            AppendEvent(events, "tool.execution_start", new { toolCallId = "materialize", toolName = prepared.Request!.ToolName,
                arguments = prepared.Request.Arguments });
            var response = photo ? File.ReadAllText(Blob())
                : new JsonObject { ["results"] = new JsonArray(Ok(User(RootId))) }.ToJsonString();
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "materialize", success = true,
                result = new { content = response } });
            if (photo) Directory.CreateDirectory(blocker);
            else
            {
                Directory.Move(blocker, blocker + ".retained");
                File.WriteAllText(blocker, "Fictitious local materialization failure.");
            }
            if (photo) Assert.Throws<UnauthorizedAccessException>(() => session.FinishCapturedSource(prepared.TicketFile!));
            else Assert.ThrowsAny<IOException>(() => session.FinishCapturedSource(prepared.TicketFile!));
            Assert.All(session.SourceStatus().Reads.Where(read => read.Attempts[^1].ClaimId == prepared.Claim.ClaimId),
                read => Assert.Equal("completed", read.State));
        }
        if (photo) Directory.Delete(blocker);
        else
        {
            File.Delete(blocker);
            Directory.Move(blocker + ".retained", blocker);
        }
        using var resumed = ExportSession.Open(path);
        var before = JsonSerializer.Serialize(resumed.SourceStatus().Reads, ExportSession.Json);

        resumed.FinishCapturedSource(prepared.TicketFile!);

        Assert.Equal(before, JsonSerializer.Serialize(resumed.SourceStatus().Reads, ExportSession.Json));
        var files = Directory.GetFiles(Path.Combine(path, photo ? "photos" : "receipts")).Order().ToArray();
        var bytes = files.Select(File.ReadAllBytes).ToArray();
        if (photo) Assert.Equal(Png, File.ReadAllBytes(blocker));
        else Assert.Equal(RootId, resumed.Plan().Receipts[ExportSession.UserPath(RootId)].Response["data"]!["id"]!.GetValue<string>());
        resumed.FinishCapturedSource(prepared.TicketFile!);
        Assert.Equal(files, Directory.GetFiles(Path.Combine(path, photo ? "photos" : "receipts")).Order().ToArray());
        for (var index = 0; index < files.Length; index++) Assert.Equal(bytes[index], File.ReadAllBytes(files[index]));
        Assert.Equal(before, JsonSerializer.Serialize(resumed.SourceStatus().Reads, ExportSession.Json));
    }

    [Fact]
    public void Mixed_profile_capture_records_only_completed_identities_despite_aggregate_tool_failure()
    {
        var path = SeedWorkflow(people: 2);
        using var session = ExportSession.Open(path);
        var job = session.NextJob("coordinator").Job!;
        var people = JsonSerializer.Deserialize<ExportSession.Person[]>(File.ReadAllText(job.JobFile), ExportSession.Json)!;
        var events = Path.Combine(root, "mixed-profile-workflow.jsonl");
        File.WriteAllText(events, "");
        var prepared = session.PrepareNextSource("coordinator", "profile", "workiq-retrieve", events);
        AppendEvent(events, "tool.execution_start", new { toolCallId = "mixed", toolName = prepared.Request!.ToolName,
            arguments = prepared.Request.Arguments });
        var success = Ok(new JsonObject { ["directoryId"] = people[0].Id, ["hits"] = new JsonArray() });
        var failure = new JsonObject { ["statusCode"] = 503 };
        AppendEvent(events, "tool.execution_complete", new { toolCallId = "mixed", success = false,
            result = new { content = new JsonObject { ["results"] = new JsonArray(success.DeepClone(), failure.DeepClone()) }.ToJsonString() } });
        session.Capture(prepared.TicketFile!);
        var results = Path.Combine(root, "mixed-profile-outcomes.json");
        Save(results, new[] { new ExportSession.SourceResult(people[0].Id, success), new ExportSession.SourceResult(people[1].Id, failure) });
        session.FinishSource(prepared.Claim.ClaimId!.Value, results);
        var draft = Path.Combine(root, "mixed-profile-draft.json");
        Save(draft, new ExportSession.ProfileDraft(UnknownFields()));

        session.RecordProfileDraft(people[0].Id, prepared.CaptureFile!, draft);

        Assert.True(File.Exists(Path.Combine(path, "profiles", people[0].Id + ".json")));
        Assert.Throws<InvalidOperationException>(() => session.RecordProfileDraft(people[1].Id, prepared.CaptureFile!, draft));
        FinishSource(session, session.ClaimSource("coordinator", "profile", [people[1].Id]), failure.DeepClone().AsObject());
        session.FinishJob("coordinator", job.JobId);
        Assert.Equal("complete", session.SourceStatus().CollectionJobs.Single().State);
    }

    [Fact]
    public void Directory_preparation_skips_exhausted_uncaptured_paths_but_retains_their_publication_blockers()
    {
        var path = ExportSession.Start("Fictitious root", RootId, AccountId, importsRoot: root).RunPath;
        using var session = ExportSession.Open(path);
        Ingest(session, (ExportSession.UserPath(RootId), Ok(User(RootId))),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(User(ChildId), User(AltId)) })));
        var events = Path.Combine(root, "exhausted-directory.jsonl");
        File.WriteAllText(events, "");
        var targets = Path.Combine(root, "exhausted-directory-targets.json");
        Save(targets, new[] { ExportSession.ReportsPath(ChildId) });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prepared = session.PrepareSource("coordinator", "structured", targets, "workiq-fetch", events);
            AppendEvent(events, "tool.execution_start", new { toolCallId = "uncaptured-" + attempt,
                toolName = prepared.Request!.ToolName, arguments = prepared.Request.Arguments });
            AppendEvent(events, "tool.execution_complete", new { toolCallId = "uncaptured-" + attempt, success = false,
                result = new { content = "{\"results\":[]}" } });
            session.FailCapture(prepared.Claim.ClaimId!.Value, prepared.TicketFile!, "The original stopped call returned an incomplete batch.");
        }

        var unrelated = session.PrepareNextSource("coordinator", "directory", "workiq-fetch", events);

        Assert.NotNull(unrelated.Claim.ClaimId);
        Assert.Equal([ExportSession.ReportsPath(AltId)], unrelated.Claim.Targets);
        Assert.Contains(ExportSession.ReportsPath(ChildId), session.Plan().Pending);
        var exhausted = session.SourceStatus().Reads.Single(read => read.Target == ExportSession.ReportsPath(ChildId));
        Assert.Equal(2, exhausted.Attempts.Count);
        Assert.False(exhausted.RecoveryEligible);
        Assert.False(exhausted.AccountedFor);
        Assert.DoesNotContain(JsonSerializer.SerializeToNode(session.Status())!["directoryWork"]!.AsArray(),
            target => target!.GetValue<string>() == ExportSession.ReportsPath(ChildId));
    }

    [Fact]
    public void Photo_preparation_skips_future_cache_runs_but_explicit_open_and_upgrade_still_refuse_them()
    {
        var path = SeedWorkflow(people: 1, rootPhoto: true);
        var future = NewRun("20000000-0000-0000-0000-000000000002");
        var stateFile = Path.Combine(future, "state.json");
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!;
        state["stateVersion"] = ExportSession.CollectorStateVersion + 1;
        state["futureProperty"] = true;
        Save(stateFile, state);
        var original = File.ReadAllBytes(stateFile);
        using var session = ExportSession.Open(path);
        session.NextJob("coordinator");
        var events = Path.Combine(root, "future-cache.jsonl");
        File.WriteAllText(events, "");

        var prepared = session.PrepareNextSource("coordinator", "photo", "workiq-fetch_blob", events);

        Assert.NotNull(prepared.Claim.ClaimId);
        Assert.Equal($"/users/{RootId}/photo/$value", prepared.Request!.Arguments["path"]!.GetValue<string>());
        Assert.Throws<NotSupportedException>(() => ExportSession.Open(future));
        Assert.Throws<NotSupportedException>(() => ExportSession.UpgradeRun(future, "Unsupported future version."));
        Assert.Equal(original, File.ReadAllBytes(stateFile));
    }

    [Fact]
    public async Task Cli_upgrade_requires_explicit_reason_and_preserves_a_legacy_strict_run()
    {
        var path = NewRun();
        MakeUnversioned(path);
        var rejected = await RunCli("upgrade-run", path, "");
        Assert.Equal(1, rejected.ExitCode);
        var upgraded = await RunCli("upgrade-run", path, "Explicitly upgrade the stopped legacy run.");
        Assert.True(upgraded.ExitCode == 0, upgraded.Error);
        using var session = ExportSession.Open(path);
        Assert.Null(session.SourceStatus().BestEffortEnrichment);
    }

    private string SeedWorkflow(int people = 17, int workers = 2, bool unavailableMetadata = false, bool rootPhoto = false)
    {
        var path = ExportSession.Start("Fictitious root", RootId, AccountId, workers, root).RunPath;
        using var session = ExportSession.Open(path);
        var users = Enumerable.Range(0, people).Select(index =>
        {
            var id = index == 0 ? RootId : $"40000000-0000-0000-0000-{index:000000000000}";
            var user = User(id, "Fictitious Person " + index);
            user["mail"] = "workflow" + index + "@example.test";
            return user;
        }).ToArray();
        var directory = new List<(string Path, JsonObject Response)>
        {
            (ExportSession.UserPath(RootId), Ok(users[0].DeepClone().AsObject())),
            (ExportSession.ManagerPath(RootId), Missing("Request_ResourceNotFound")),
            (ExportSession.ReportsPath(RootId), Ok(new JsonObject { ["value"] = new JsonArray(users.Skip(1)
                .Select(user => (JsonNode?)user.DeepClone()).ToArray()) }))
        };
        foreach (var user in users.Skip(1))
        {
            var id = user["id"]!.GetValue<string>();
            directory.Add((ExportSession.UserPath(id), Ok(user.DeepClone().AsObject())));
            directory.Add((ExportSession.ReportsPath(id), Empty()));
        }
        foreach (var batch in directory.Chunk(25)) Ingest(session, batch);
        foreach (var user in users)
        {
            var id = user["id"]!.GetValue<string>();
            Ingest(session, (ExportSession.PhotoPath(id), unavailableMetadata && id == RootId
                ? new JsonObject { ["statusCode"] = 503 } : rootPhoto && id == RootId ? Metadata("v1") : Missing("ImageNotFound")));
        }
        if (unavailableMetadata) Ingest(session, (ExportSession.PhotoPath(RootId), new JsonObject { ["statusCode"] = 503 }));
        return path;
    }

    private void CompleteFixtureJob(ExportSession session, ExportSession.JobLease lease, bool unavailableProfile = false)
    {
        var people = JsonSerializer.Deserialize<ExportSession.Person[]>(File.ReadAllText(lease.JobFile), ExportSession.Json)!;
        foreach (var person in people)
        {
            if (unavailableProfile && person.Id == people[^1].Id)
            {
                FinishSource(session, session.ClaimSource(lease.Worker, "profile", [person.Id]), new JsonObject { ["statusCode"] = 503 });
                FinishSource(session, session.ClaimSource(lease.Worker, "profile", [person.Id]), new JsonObject { ["statusCode"] = 503 });
            }
            else
            {
                if (!session.SourceStatus().Reads.Any(read => read.Kind == "profile" && read.Target == person.Id && read.State == "completed"))
                    FinishSource(session, session.ClaimSource(lease.Worker, "profile", [person.Id]), Ok(new JsonObject()));
                session.Profile(person.Id, ProfileInput(session, person, sourceCompleted: false));
            }
        }
    }

    private sealed class CollectionClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (byte[] Run, byte[] Sources) MakeUnversioned(string path)
    {
        var stateFile = Path.Combine(path, "state.json");
        var sourcesFile = Path.Combine(path, "source-coordination.json");
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        state.Remove("stateVersion");
        state.Remove("collector");
        state.Remove("workerLimit");
        var sources = JsonNode.Parse(File.ReadAllText(sourcesFile))!.AsObject();
        sources.Remove("stateVersion");
        Save(stateFile, state);
        Save(sourcesFile, sources);
        return (File.ReadAllBytes(stateFile), File.ReadAllBytes(sourcesFile));
    }
}
