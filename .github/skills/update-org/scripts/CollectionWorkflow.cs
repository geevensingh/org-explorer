using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    public sealed record CollectionJob(string JobId, string JobFile, string JobSha256, string[] DirectoryIds,
        string State, string? Worker = null, DateTimeOffset? AssignedAt = null, DateTimeOffset? CompletedAt = null)
    {
        public JobHandoff[] Handoffs { get; init; } = [];
    }
    public sealed record JobHandoff(DateTimeOffset RecordedAt, string PreviousWorker, string Worker, string Reason);
    public sealed record JobLease(string JobId, string JobFile, string WorkerFolder, int People,
        string Worker, DateTimeOffset AssignedAt);
    public sealed record JobDispatch(JobLease? Job, string? Blocker, DateTimeOffset? RetryAt = null);
    public sealed record PreparedSource(SourceClaim Claim, CaptureRequest? Request, string? DescriptorFile, string? TicketFile)
    {
        public string? CaptureFile => TicketFile is null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TicketFile)!, "capture.json");
    }
    public sealed record ProfileDraft(Dictionary<string, Field> Fields, string[]? Warnings = null, string[]? EvidenceFiles = null);

    public JobDispatch NextJob(string worker)
    {
        Require(!string.IsNullOrWhiteSpace(worker), "A fresh worker name is required.");
        var collection = Complete();
        return WithSources<JobDispatch>(ledger =>
        {
            var retryAt = ledger.Reads.SelectMany(SourceAttempts).Max(attempt => attempt.RetryAt);
            if (ledger.StopReason is not null) return new(null, ledger.StopReason, retryAt);
            if (retryAt > sourceClock.GetUtcNow()) return new(null, "Wait for the recorded source-wide retry deadline.", retryAt);
            Require(collection.People.All(person => ledger.Reads.Any(read => read.Kind == "structured"
                && read.Target == PhotoPath(person.Id) && read.AccountedFor)),
                "Finish or account for every retained Person's photo metadata before dispatching enrichment jobs.");
            EnsureCollectionJobs(ledger, collection);
            if (ledger.CollectionJobs.Any(job => job.State == "assigned" && job.Worker == worker))
                return new(null, "This worker already owns a job; resume it rather than launching a duplicate.");
            if (ledger.CollectionJobs.Count(job => job.State == "assigned") >= run.WorkerLimit)
                return new(null, "The configured worker slots are occupied; await a completion or an explicit stopped-worker handoff.");
            var pending = ledger.CollectionJobs.FirstOrDefault(job => job.State == "pending");
            if (pending is null) return new(null, "No undispatched jobs remain; settle assigned jobs, then publish.");
            var assigned = pending with { State = "assigned", Worker = worker, AssignedAt = sourceClock.GetUtcNow() };
            ledger.CollectionJobs[ledger.CollectionJobs.IndexOf(pending)] = assigned;
            return new(Lease(assigned), null);
        });
    }

    private void EnsureCollectionJobs(SourceLedger ledger, Collection collection)
    {
        var planned = collection.People.Chunk(16).Select(group =>
        {
            var relative = "workers\\" + group[0].Id + ".json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(group, Json) + "\n");
            Immutable(Within(relative), bytes);
            return new CollectionJob(group[0].Id, relative, Hash(bytes), group.Select(person => person.Id).ToArray(), "pending");
        }).ToArray();
        if (ledger.CollectionJobs.Count == 0) ledger.CollectionJobs.AddRange(planned);
        Require(ledger.CollectionJobs.Count == planned.Length && planned.All(expected =>
            ledger.CollectionJobs.Any(job => job.JobId == expected.JobId && job.JobFile == expected.JobFile
                && job.JobSha256 == expected.JobSha256 && job.DirectoryIds.SequenceEqual(expected.DirectoryIds))),
            "The verified population changed after dispatch; preserve original assignments rather than retargeting them.");
    }

    private JobLease Lease(CollectionJob job)
    {
        Require(Hash(File.ReadAllBytes(Within(job.JobFile))) == job.JobSha256, "The assigned job file changed.");
        var folder = Within("workers\\job-" + job.JobId);
        Directory.CreateDirectory(folder);
        return new(job.JobId, Within(job.JobFile), folder, job.DirectoryIds.Length,
            job.Worker ?? throw new InvalidOperationException("The job has no owner."),
            job.AssignedAt ?? throw new InvalidOperationException("The assignment time is missing."));
    }

    public JobLease ResumeJob(string jobId, string worker, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(worker) && !string.IsNullOrWhiteSpace(reason),
            "Record the replacement worker and proof that the original worker stopped.");
        return WithSources(ledger =>
        {
            var job = ledger.CollectionJobs.SingleOrDefault(job => job.JobId == jobId)
                ?? throw new InvalidOperationException("The assigned job is not in this run.");
            Require(job.State == "assigned" && job.Worker is not null && job.Worker != worker,
                "Reassign only an unfinished job to a distinct fresh worker.");
            Require(!ledger.CollectionJobs.Any(other => other.State == "assigned" && other.Worker == worker),
                "The replacement worker already owns a job.");
            Require(!ledger.Reads.Any(read => read.State == "active" && AffectsJob(read, job)),
                "Capture/finish or explicitly interrupt stopped source work before reassigning its job.");
            var resumed = job with
            {
                Worker = worker,
                Handoffs = [.. job.Handoffs, new(sourceClock.GetUtcNow(), job.Worker!, worker, reason)]
            };
            ledger.CollectionJobs[ledger.CollectionJobs.IndexOf(job)] = resumed;
            return Lease(resumed);
        });
    }

    private static bool AffectsJob(SourceRead read, CollectionJob job)
        => read.Kind == "profile" ? job.DirectoryIds.Contains(read.Target)
            : job.DirectoryIds.Any(id => read.Target.StartsWith("/users/" + id + "/", StringComparison.Ordinal)
                || read.Target.StartsWith("/users/" + id + "?", StringComparison.Ordinal));

    public object FinishJob(string worker, string jobId)
    {
        var collection = Complete();
        return WithSources(ledger =>
        {
            var job = ledger.CollectionJobs.SingleOrDefault(job => job.JobId == jobId)
                ?? throw new InvalidOperationException("The assigned job is not in this run.");
            Require(job.State == "assigned" && job.Worker == worker, "Only the current job owner can finish it; stale handoffs cannot complete.");
            Require(!ledger.Reads.Any(read => read.State == "active" && AffectsJob(read, job)), "Settle all source calls affecting this job first.");
            Require(Hash(File.ReadAllBytes(Within(job.JobFile))) == job.JobSha256, "The assigned job file changed.");
            foreach (var id in job.DirectoryIds)
            {
                var person = collection.People.Single(person => person.Id == id);
                var profile = ledger.Reads.SingleOrDefault(read => read.Kind == "profile" && read.Target == id);
                Require(profile is not null && profile.AccountedFor, "Profile lookup is not accounted for: " + id);
                if (profile!.State == "completed")
                {
                    var observation = Read<Observation>(Within("profiles\\" + id + ".json"));
                    CheckObservation(observation, person);
                    Require(observation.Error is null, "A failed observation cannot complete this job.");
                    foreach (var evidence in observation.EvidenceFiles) Require(File.Exists(Within(evidence)), "Profile evidence is missing.");
                }
                RequireJobPhoto(ledger, id);
            }
            var errors = Within("profile-errors");
            if (Directory.Exists(errors))
                Require(!Directory.GetFiles(errors, "*.json").Select(Read<ProfileFailure>)
                    .Any(failure => failure.DirectoryId is null || job.DirectoryIds.Contains(failure.DirectoryId)),
                    "Correct unresolved observation inputs before completing this job.");
            ledger.CollectionJobs[ledger.CollectionJobs.IndexOf(job)] = job with { State = "complete", CompletedAt = sourceClock.GetUtcNow() };
            return new { jobId, people = job.DirectoryIds.Length, completedJobs = ledger.CollectionJobs.Count(job => job.State == "complete") };
        });
    }

    private void RequireJobPhoto(SourceLedger ledger, string id)
    {
        var metadata = ledger.Reads.SingleOrDefault(read => read.Kind == "structured" && read.Target == PhotoPath(id));
        Require(metadata is not null && metadata.AccountedFor, "Photo metadata is not accounted for: " + id);
        if (metadata is not null && IsEnrichmentUnavailable(metadata)) return;
        var response = PhotoMetadata(id);
        if (NoPhoto(response)) return;
        Success(response, "Photo metadata");
        var binary = ledger.Reads.SingleOrDefault(read => read.Kind == "photo" && read.Target == $"/users/{id}/photo/$value");
        if (binary is not null && (IsEnrichmentUnavailable(binary) || IsPhotoOmitted(binary))) return;
        var photo = Read<PhotoEvidence>(Within("photos\\" + id + ".json"));
        Require(photo.DirectoryId == id && photo.Etag == Text(response["data"]!.AsObject(), "@odata.mediaEtag"), "Photo metadata changed.");
        if (!photo.Reused) RequireCompletedSource(ledger, "photo", $"/users/{id}/photo/$value");
        var bytes = File.ReadAllBytes(Within(photo.PhotoPath));
        Require(bytes.Length == photo.SizeBytes && Hash(bytes) == photo.Sha256, "Photo changed after collection.");
    }

    public PreparedSource PrepareSource(string worker, string kind, string targetsFile, string toolName,
        string eventsFile, string? agentId = null)
    {
        Require(!string.IsNullOrWhiteSpace(toolName), "Supply the exact tool name from the current configured WorkIQ definitions.");
        Require(agentId is null || !string.IsNullOrWhiteSpace(agentId), "Use the actual native worker identity, not an empty scope.");
        var requested = Read<string[]>(targetsFile);
        Require(requested.All(target => !string.IsNullOrWhiteSpace(target)), "Supply an array of nonempty source targets.");
        var targets = requested.Select(target => SourceTarget(kind, target)).ToArray();
        var maximum = kind switch { "structured" => StructuredBatchLimit, "profile" => 4, "photo" => 1,
            _ => throw new ArgumentException("Expected structured, profile or photo source reads.") };
        Require(targets.Length is > 0 && targets.Length <= maximum, $"This host uses at most {maximum} {kind} targets per request.");
        Local(eventsFile);
        using (var stream = OpenEvents(eventsFile))
        {
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                Require(stream.ReadByte() == '\n', "Wait for the final event record before preparing a source request.");
            }
        }
        var sources = SourceStatus();
        var assigned = sources.CollectionJobs.SingleOrDefault(job => job.State == "assigned" && job.Worker == worker);
        if (kind is "profile" or "photo")
        {
            Require(assigned is not null, "Obtain this worker's disjoint job before preparing enrichment reads.");
            Require(targets.All(target => assigned!.DirectoryIds.Contains(kind == "profile" ? target : target.Split('/')[2])),
                "Enrichment requests must stay within the worker's assigned job.");
            Require(agentId is not null || run.WorkerLimit == 1 || worker == "coordinator",
                "Supply the native worker's runtime agentId; a missing descriptor scope is not proof worker capture is unavailable.");
        }
        else Require(assigned is null, "Only the coordinator collects structured directory and metadata reads.");
        var claim = ClaimSource(worker, kind, targets);
        if (claim.ClaimId is null) return new(claim, null, null, null);
        JsonObject arguments;
        if (kind == "structured") arguments = new() { ["entityUrls"] = JsonSerializer.SerializeToNode(claim.Targets, Json) };
        else if (kind == "photo") arguments = new() { ["path"] = claim.Targets.Single() };
        else
        {
            var people = Complete().People.ToDictionary(person => person.Id);
            var identities = string.Join("\n", claim.Targets.Select(id => $"directoryId={id}; email={people[id].Email}"));
            arguments = new()
            {
                ["query"] = "Find indexed M365 People work-profile evidence for these exact identities:\n" + identities
                    + "\nReturn each identity's current_position.company.office_location and company.address state/country, actual citations and uncertainty. Do not infer geography from a Manager or remote status.",
                ["strategy"] = "grounding", ["capabilities"] = new JsonArray(new JsonObject { ["name"] = "People" })
            };
        }
        var request = new CaptureRequest(toolName, arguments, agentId, ClaimId: claim.ClaimId);
        var directory = Within("requests");
        Directory.CreateDirectory(directory);
        var descriptor = System.IO.Path.Combine(directory, claim.ClaimId + ".json");
        Immutable(descriptor, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n"));
        var ticket = BeginCapture(eventsFile, descriptor);
        return new(claim, request, descriptor, ticket);
    }

    public PreparedSource PrepareNextSource(string worker, string phase, string toolName, string eventsFile, string? agentId = null)
    {
        Require(!string.IsNullOrWhiteSpace(worker), "A coordinator or worker name is required.");
        Require(!string.IsNullOrWhiteSpace(toolName), "Supply the exact currently configured WorkIQ tool name.");
        var sources = SourceStatus();
        var collection = phase == "directory" ? Plan() : Complete();
        var kind = phase switch
        {
            "directory" or "metadata" => "structured", "profile" => "profile", "photo" => "photo",
            _ => throw new ArgumentException("Expected directory, metadata, profile or photo phase.")
        };
        bool Pending(string target) => !sources.Reads.Any(read => read.Kind == kind && read.Target == target
            && (read.AccountedFor || read.State == "active"));
        string[] targets;
        if (phase == "directory") targets = collection.Pending.Where(Pending).Take(StructuredBatchLimit).ToArray();
        else if (phase == "metadata") targets = collection.People.Select(person => PhotoPath(person.Id))
            .Where(Pending).Take(StructuredBatchLimit).ToArray();
        else
        {
            var job = sources.CollectionJobs.SingleOrDefault(job => job.State == "assigned" && job.Worker == worker)
                ?? throw new InvalidOperationException("Obtain this worker's disjoint job before preparing enrichment.");
            if (phase == "profile") targets = job.DirectoryIds.Where(Pending).Take(4).ToArray();
            else
            {
                foreach (var id in job.DirectoryIds) ReusePhoto(id, sources);
                targets = job.DirectoryIds.Where(id =>
                    !File.Exists(Within("photos\\" + id + ".json"))
                    && !sources.Reads.Any(read => read.Kind == "structured" && read.Target == PhotoPath(id)
                        && IsEnrichmentUnavailable(read))
                    && !NoPhoto(PhotoMetadata(id)))
                    .Select(id => $"/users/{id}/photo/$value").Where(Pending).Take(1).ToArray();
            }
        }
        if (targets.Length == 0)
            return new(new(null, kind, worker, [], sources.StopReason
                ?? "No lookup remains in this phase; materialize retained results and finish the job rather than repeating source reads."),
                null, null, null);
        var directory = Within("requests");
        Directory.CreateDirectory(directory);
        var file = System.IO.Path.Combine(directory, Hash(Encoding.UTF8.GetBytes(worker + "\n" + phase)) + ".targets.json");
        Save(file, targets);
        return PrepareSource(worker, kind, file, toolName, eventsFile, agentId);
    }

    public object FinishCapturedSource(string ticketFile)
    {
        var captured = Capture(ticketFile);
        var ticket = Read<CaptureTicket>(ticketFile);
        var claimId = ticket.Request.ClaimId
            ?? throw new InvalidOperationException("Use a prepared claim-bound capture for automatic source completion.");
        var active = SourceStatus().Reads.Where(read => read.State == "active" && read.Attempts[^1].ClaimId == claimId).ToArray();
        Require(active.Length > 0 && active[0].Kind is "structured" or "photo",
            "Profile retrieval needs explicit per-identity outcomes; use capture and source-finish for that semantic association.");
        var response = Read<JsonObject>(captured.ResponseFile);
        SourceResult[] results;
        if (active[0].Kind == "structured")
        {
            var targets = Read<string[]>(captured.RequestsFile!);
            var responses = response["results"]!.AsArray();
            results = targets.Select((target, index) => new SourceResult(target, responses[index]!.DeepClone().AsObject())).ToArray();
        }
        else results = [new(active.Single().Target, response)];
        var resultFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ticketFile)!, "source-results.json");
        Immutable(resultFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(results, Json) + "\n"));
        var completion = FinishSource(claimId, resultFile);
        object? materialized = null;
        if (active[0].Kind == "structured") Ingest(captured.RequestsFile!, captured.ResponseFile);
        else if (completion.Reads.Single().State == "completed")
            materialized = Photo(active.Single().Target.Split('/')[2], captured.ResponseFile);
        return new { capture = captured, source = completion, materialized };
    }

    public object RecordProfileDraft(string id, string captureFile, string draftFile)
    {
        id = SourceTarget("profile", id);
        captureFile = System.IO.Path.GetFullPath(captureFile);
        Require(captureFile.StartsWith(path + "\\captures\\", StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetFileName(captureFile) == "capture.json", "Use this run's completed capture.json.");
        var captured = Read<CapturedResult>(captureFile);
        var ticketFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(captureFile)!, "ticket.json");
        var ticket = Read<CaptureTicket>(ticketFile);
        RequireCaptureBinding(ticketFile, ticket, active: false);
        var source = SourceStatus().Reads.SingleOrDefault(read => read.Kind == "profile" && read.Target == id);
        Require(source is not null && source.State == "completed" && source.Attempts[^1].ClaimId == ticket.Request.ClaimId,
            "Record an observation only from that identity's latest completed profile lookup.");
        Require(captured.ToolSucceeded && ticket.Request.Arguments["strategy"]?.GetValue<string>() == "grounding",
            "A failed lookup cannot become an unknown observation.");
        var person = Complete().People.Single(person => person.Id == id);
        var started = Read<JsonObject>(captured.StartFile);
        var observedAt = DateTimeOffset.Parse(Text(started, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None);
        var draft = Read<ProfileDraft>(draftFile);
        var observation = new Observation(id, person.Email, draft.Fields,
            [captured.ResponseFile, .. draft.EvidenceFiles ?? []], draft.Warnings ?? [], null, observedAt);
        var directory = Within("workers\\observations");
        Directory.CreateDirectory(directory);
        var input = System.IO.Path.Combine(directory, id + ".json");
        Save(input, observation);
        return Profile(id, input);
    }

    public object Status()
    {
        var collection = Plan();
        var sources = SourceStatus();
        var now = sourceClock.GetUtcNow();
        var retryAt = sources.Reads.SelectMany(SourceAttempts).Max(attempt => attempt.RetryAt);
        var active = sources.Reads.Where(read => read.State == "active").ToArray();
        var completed = sources.CollectionJobs.Where(job => job.State == "complete").ToArray();
        var sample = completed.OrderByDescending(job => job.CompletedAt).Take(8).ToArray();
        var elapsed = sample.Length == 0 ? 0 : (sample.Max(job => job.CompletedAt)!.Value - sample.Min(job => job.AssignedAt)!.Value).TotalHours;
        var rate = elapsed > 0 ? sample.Sum(job => job.DirectoryIds.Length) / elapsed : (double?)null;
        var pendingMetadata = collection.People.Where(person => !sources.Reads.Any(read => read.Kind == "structured"
            && read.Target == PhotoPath(person.Id) && read.AccountedFor)).Select(person => PhotoPath(person.Id)).ToArray();
        var failures = sources.Reads.Where(read => !read.AccountedFor && read.State == "failed").ToArray();
        var inputErrors = Directory.Exists(Within("profile-errors")) ? Directory.GetFiles(Within("profile-errors"), "*.json").Length : 0;
        var phase = sources.StopReason is not null ? "stopped" : retryAt > now ? "wait"
            : collection.Pending.Length > 0 || collection.Errors.Length > 0 ? "directory"
            : pendingMetadata.Length > 0 ? "photo-metadata"
            : sources.CollectionJobs.Count == 0 || completed.Length != sources.CollectionJobs.Count ? "enrichment"
            : active.Length > 0 ? "settle" : failures.Length > 0 || inputErrors > 0 ? "blocked" : "publish";
        return new
        {
            stateVersion = CollectorStateVersion, runPath = path,
            policy = sources.BestEffortEnrichment is null ? "strict" : "best-effort", phase,
            people = collection.People.Length, workerLimit = run.WorkerLimit,
            jobs = new { total = sources.CollectionJobs.Count, completed = completed.Length,
                assigned = sources.CollectionJobs.Count(job => job.State == "assigned"),
                pending = sources.CollectionJobs.Count(job => job.State == "pending") },
            profiles = new
            {
                completed = sources.Reads.Count(read => read.Kind == "profile" && read.State == "completed"),
                unavailable = sources.Reads.Count(read => read.Kind == "profile" && IsEnrichmentUnavailable(read)),
                observations = Directory.GetFiles(Within("profiles"), "*.json").Length, inputErrors
            },
            photos = new
            {
                materialized = Directory.GetFiles(Within("photos"), "*.json").Length,
                confirmedAbsent = collection.Receipts.Values.Count(receipt => receipt.Path.Split('?')[0].EndsWith("/photo", StringComparison.Ordinal)
                    && NoPhoto(receipt.Response)),
                unavailable = sources.Reads.Where(read => read.Kind != "profile" && IsEnrichmentUnavailable(read))
                    .Select(read => read.Target.Split('/')[2]).Distinct().Count(), pendingMetadata = pendingMetadata.Length
            },
            directory = new { pending = collection.Pending.Length, errors = collection.Errors.Length },
            activeClaims = active.Select(read => read.Attempts[^1].ClaimId).Distinct().Count(),
            retryAt = retryAt > now ? retryAt : null, sourceFailures = failures.Length, sources.StopReason,
            directoryWork = collection.Pending.Take(StructuredBatchLimit).ToArray(),
            metadataWork = pendingMetadata.Take(StructuredBatchLimit).ToArray(),
            observedPeoplePerHour = rate,
            estimatedRemainingMinutes = rate is > 0 ? (collection.People.Length - completed.Sum(job => job.DirectoryIds.Length)) / rate * 60 : null,
            estimateBasis = sample.Length == 0 ? "Await the first completed sample; no completion-time promise."
                : "Approximation from recent assigned-to-completed job windows; assumes the observed rate continues.",
            createdWith = run.Collector, currentCollector = CurrentCollector()
        };
    }
}
