using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    private static readonly string[] SourceDenialCodes =
    [
        "Forbidden", "Unauthorized", "AccessDenied", "ErrorAccessDenied", "Authorization_RequestDenied",
        "InvalidAuthenticationToken", "AuthenticationRequired", "AuthenticationFailed", "PolicyDenied",
        "BlockedByPolicy", "PolicyViolation", "InsufficientPrivileges", "insufficient_claims",
        "ConsentRequired", "PermissionDenied"
    ];
    private static readonly string[] ProfileFailureMessages =
    [
        "Open '' Failure: Unable to open, Exception Caught.",
        "Open '' Failure: Unable to open, Exception: Status code is .",
        "Profile details couldn't be retrieved for this person right now."
    ];
    public sealed record SourceAttempt(Guid ClaimId, string Worker, DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt, string? Error, bool Retryable, DateTimeOffset? RetryAt, string? ResultFile = null,
        string? CaptureTicket = null, string? CaptureTicketSha256 = null, string? CaptureFailureFile = null);
    public sealed record SourceRead(string Kind, string Target, string State, List<SourceAttempt> Attempts,
        bool RecoveryEligible = false, SourceRecoveryAuthorization? UnclassifiedRecovery = null,
        SourceRecoveryAuthorization? NotFoundRecovery = null, SourceRecoveryAuthorization? PhotoOmission = null,
        SourceUnavailability? Unavailable = null)
    {
        public bool AccountedFor => State == "completed" || IsPhotoOmitted(this) || IsEnrichmentUnavailable(this);
        public SourceObservationRefresh[] PreviousObservations { get; init; } = [];
    }
    public sealed record SourceObservationRefresh(DateTimeOffset RecordedAt, DateTimeOffset RequiredAfter, string Reason,
        SourceAttempt[] Attempts, SourceRecoveryAuthorization? UnclassifiedRecovery, SourceRecoveryAuthorization? NotFoundRecovery);
    public sealed record SourceRecoveryAuthorization(DateTimeOffset AuthorizedAt, string Reason);
    public sealed record SourceUnavailability(DateTimeOffset RecordedAt, string Reason);
    public sealed record SourceErrorRecoveryAuthorization(string Kind, string Error, DateTimeOffset AuthorizedAt, string Reason);
    public sealed record SourceLedger(Guid BatchId, List<SourceRead> Reads)
    {
        public int StateVersion { get; init; }
        public bool RecoveryConsumed { get; set; }
        public SourceRecoveryAuthorization? PerReadRecovery { get; set; }
        public SourceRecoveryAuthorization? BestEffortEnrichment { get; set; }
        public SourceErrorRecoveryAuthorization? MatchingErrorRecovery { get; set; }
        public List<SourceErrorRecoveryAuthorization> AdditionalMatchingErrorRecoveries { get; init; } = [];
        public string? StopReason { get; set; }
        public List<CollectionJob> CollectionJobs { get; init; } = [];
    }
    public sealed record SourceClaim(Guid? ClaimId, string Kind, string Worker, string[] Targets, string? Blocker,
        DateTimeOffset? RetryAt = null);
    public sealed record SourceResult(string Target, JsonObject Response);
    public sealed record SourceCompletion(Guid BatchId, Guid ClaimId, SourceRead[] Reads,
        bool RecoveryConsumed, string? StopReason);

    public SourceLedger SourceStatus() => WithSources(ledger => ledger, save: false);

    public SourceClaim ClaimSource(string worker, string kind, string targetsFile)
        => ClaimSource(worker, kind, Read<string[]>(targetsFile));

    public SourceClaim ClaimSource(string worker, string kind, string[] targets)
    {
        Require(!string.IsNullOrWhiteSpace(worker), "A source worker identity is required.");
        var maximum = kind switch
        {
            "profile" => 4,
            "structured" => 50,
            "photo" => 1,
            _ => throw new ArgumentException("Expected structured, profile or photo source reads.")
        };
        Require(targets.Length > 0 && targets.Length <= maximum, $"A {kind} call supports 1-{maximum} targets.");
        targets = targets.Select(target => SourceTarget(kind, target)).ToArray();
        Require(targets.Distinct(StringComparer.Ordinal).Count() == targets.Length, "Duplicate source targets.");
        return WithSources(ledger =>
        {
            if (ledger.CollectionJobs.Count > 0) RequireJobSource(ledger, worker, kind, targets);
            foreach (var target in targets)
                if (!ledger.Reads.Any(read => read.Kind == kind && read.Target == target))
                    ledger.Reads.Add(new(kind, target, "deferred", []));
            var pending = ledger.Reads.Where(read => read.Kind == kind && targets.Contains(read.Target)
                && !read.AccountedFor).ToArray();
            var now = sourceClock.GetUtcNow();
            var retryAt = ledger.Reads.SelectMany(SourceAttempts).Max(attempt => attempt.RetryAt);
            SourceClaim Blocked(string reason) => new(null, kind, worker, pending.Select(read => read.Target).ToArray(),
                reason, retryAt > now ? retryAt : null);
            if (ledger.StopReason is not null) return Blocked(ledger.StopReason);
            if (pending.Length == 0) return Blocked("These source reads are already completed or explicitly accounted for.");
            var active = ledger.Reads.Where(read => read.State == "active").ToArray();
            if (active.Any(read => read.Attempts[^1].Worker == worker) || pending.Any(read => read.State == "active"))
                return Blocked("A requested read or this worker already has an active claim.");
            if (ledger.BestEffortEnrichment is not null)
            {
                if (pending.Any(read => read.Attempts.Count >= 2
                    || read.State == "failed" && !read.RecoveryEligible))
                    return Blocked("A requested read is exhausted or not eligible for recovery; unrelated reads may continue.");
            }
            else if (ledger.Reads.Any(read => !read.AccountedFor && (read.State == "failed" || read.RecoveryEligible)))
            {
                if (!ledger.RecoveryConsumed)
                    return Blocked("Source reads are paused for the coordinator's invocation-wide recovery decision.");
                if (pending.Any(read => read.State != "failed" || !read.RecoveryEligible || read.Attempts.Count != 1))
                    return Blocked("Unresolved source failures block dispatch; the shared recovery allowance cannot reset.");
            }
            if (retryAt > now) return Blocked("Wait for the invocation-wide returned source retry delay.");
            if (active.Select(read => read.Attempts[^1].ClaimId).Distinct().Count() >= 2)
                return Blocked("Both invocation-wide source slots are active; defer these first reads.");
            var claimId = Guid.NewGuid();
            foreach (var read in pending)
            {
                var index = ledger.Reads.IndexOf(read);
                ledger.Reads[index] = read with { State = "active" };
                read.Attempts.Add(new(claimId, worker, now, null, null, false, null));
            }
            return new SourceClaim(claimId, kind, worker, pending.Select(read => read.Target).ToArray(), null);
        });
    }

    public SourceObservationRefresh RefreshManagerConfirmation(string id, string reason)
    {
        Require(Guid.TryParse(id, out _), "Use the exact directory identity requiring fresh Manager confirmation.");
        Require(!string.IsNullOrWhiteSpace(reason), "Record why newer reporting evidence requires Manager recollection.");
        return WithSources(ledger =>
        {
            Require(ledger.StopReason is null, ledger.StopReason ?? "");
            var request = Plan().ManagerRefreshes.SingleOrDefault(request => request.DirectoryId == id)
                ?? throw new InvalidOperationException("No newer conflicting reporting evidence requires this Manager refresh.");
            var read = ledger.Reads.SingleOrDefault(read => read.Kind == "structured" && read.Target == request.Target);
            Require(read is not null && read.State == "completed" && read.Attempts.Count > 0
                && read.Attempts.All(attempt => attempt.FinishedAt is not null)
                && read.Attempts[^1].Error is null && read.Attempts[^1].ResultFile is not null,
                "Refresh only a completed Manager observation; outstanding reads retain their existing attempt budget.");
            var completed = read!;
            Require(completed.PreviousObservations.Length == 0
                || request.RequiredAfter > completed.PreviousObservations[^1].RequiredAfter,
                "A Manager refresh requires strictly newer conflicting reporting evidence.");
            var refresh = new SourceObservationRefresh(sourceClock.GetUtcNow(), request.RequiredAfter, reason,
                completed.Attempts.ToArray(), completed.UnclassifiedRecovery, completed.NotFoundRecovery);
            ledger.Reads[ledger.Reads.IndexOf(completed)] = completed with
            {
                State = "deferred", Attempts = [], RecoveryEligible = false,
                UnclassifiedRecovery = null, NotFoundRecovery = null,
                PreviousObservations = [.. completed.PreviousObservations, refresh]
            };
            complete = null;
            return refresh;
        });
    }

    public SourceCompletion FinishSource(Guid claimId, string resultsFile)
    {
        FreshPayload(resultsFile);
        var bytes = File.ReadAllBytes(resultsFile);
        var results = JsonSerializer.Deserialize<SourceResult[]>(bytes, Json)
            ?? throw new InvalidOperationException("Expected a complete source-result array.");
        return WithSources(ledger =>
        {
            var active = ledger.Reads.Where(read => read.State == "active" && read.Attempts[^1].ClaimId == claimId).ToArray();
            Require(active.Length > 0 && active.Length == results.Length
                && active.All(read => results.Count(result => result.Target == read.Target) == 1),
                "Record exactly one result per active claim target; stale claims cannot complete.");
            var resultFile = "sources\\source-" + Hash(bytes) + ".json";
            Immutable(Within(resultFile), bytes);
            foreach (var read in active)
            {
                var response = results.Single(result => result.Target == read.Target).Response;
                var outcome = SourceOutcome(read.Kind, read.Target, response);
                if (outcome.Denied)
                    ledger.StopReason = "Source authorization/access/policy denial stops this invocation: " + outcome.Error;
                else if (outcome.InvalidDelay && ledger.BestEffortEnrichment is not null)
                    ledger.StopReason = "The returned source retry delay cannot be honored: " + outcome.Error;
                read.Attempts[^1] = read.Attempts[^1] with
                {
                    FinishedAt = sourceClock.GetUtcNow(), Error = outcome.Error,
                    Retryable = outcome.Retryable, RetryAt = outcome.RetryAt, ResultFile = resultFile
                };
                var finished = read with { State = outcome.Error is null ? "completed" : "failed", RecoveryEligible = false };
                var retryable = outcome.Retryable
                    || read.Kind == "profile" && outcome.Unclassified && KnownProfileFailure(response)
                    || read.Kind == "photo" && outcome.NotFound && HasSuccessfulPhotoMetadata(ledger, read.Target);
                ledger.Reads[ledger.Reads.IndexOf(read)] = SettleBestEffortRead(ledger, finished, retryable);
            }
            return new SourceCompletion(ledger.BatchId, claimId,
                ledger.Reads.Where(read => read.Attempts.LastOrDefault()?.ClaimId == claimId).ToArray(),
                ledger.RecoveryConsumed, ledger.StopReason);
        });
    }

    public SourceRecoveryAuthorization AuthorizeBestEffortEnrichment(string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the owner's best-effort enrichment authorization.");
        return WithSources(ledger =>
        {
            Require(ledger.StopReason is null, ledger.StopReason ?? "");
            Require(ledger.Reads.All(read => read.State != "active"), "Settle active claims before changing collection policy.");
            Require(ledger.BestEffortEnrichment is null, "Best-effort enrichment is already authorized for this run.");
            Require(ledger.Reads.All(read => read.State != "failed"),
                "Enable best-effort collection before failed reads; retain the existing recovery path for an earlier strict run.");
            return ledger.BestEffortEnrichment = new(sourceClock.GetUtcNow(), reason);
        });
    }

    private SourceRead SettleBestEffortRead(SourceLedger ledger, SourceRead read, bool retryable)
    {
        if (ledger.BestEffortEnrichment is null || ledger.StopReason is not null || read.State != "failed") return read;
        if (read.Attempts.Count == 1 && retryable) return read with { RecoveryEligible = true };
        if (!IsOptionalSource(read) || read.Attempts.Any(attempt => attempt.FinishedAt is null
            || attempt.Error is null || attempt.ResultFile is null && attempt.CaptureFailureFile is null)) return read;
        return read with
        {
            State = "unavailable", RecoveryEligible = false,
            Unavailable = new(sourceClock.GetUtcNow(), read.Attempts[^1].Error!)
        };
    }

    private static bool IsOptionalSource(SourceRead read)
        => read.Kind is "profile" or "photo" || read.Kind == "structured"
            && read.Target.Split('?')[0].Split('/') is ["", "users", var id, "photo"] && Guid.TryParse(id, out _);

    private static bool IsEnrichmentUnavailable(SourceRead read)
        => IsOptionalSource(read) && read.State == "unavailable" && read.Unavailable is { } unavailable
            && !string.IsNullOrWhiteSpace(unavailable.Reason) && read.Attempts.Count is >= 1 and <= 2
            && read.Attempts.All(attempt => attempt.FinishedAt is not null && attempt.Error is not null
                && (attempt.ResultFile is not null || attempt.CaptureFailureFile is not null
                    && attempt.CaptureTicket is not null && attempt.CaptureTicketSha256 is not null));

    private bool HasSuccessfulPhotoMetadata(SourceLedger ledger, string target)
    {
        var metadata = ledger.Reads.SingleOrDefault(read => read.Kind == "structured"
            && read.Target == PhotoPath(target.Split('/')[2]) && read.State == "completed");
        return metadata is not null && SavedSourceResponse(metadata, metadata.Attempts[^1])["statusCode"]?.GetValue<int>() == 200;
    }

    private static bool KnownProfileFailure(JsonObject response)
    {
        var errors = SourceErrorKeys(response);
        return errors.Length > 0 && errors.All(error => ProfileFailureMessages.Any(message => error == JsonSerializer.Serialize(message)));
    }

    public SourceRecoveryAuthorization AuthorizePerReadSourceRecovery(string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the user's explicit per-read recovery authorization.");
        return WithSources(ledger =>
        {
            Require(ledger.StopReason is null, ledger.StopReason ?? "");
            Require(ledger.Reads.All(read => read.State != "active"), "Settle all active source claims before changing recovery policy.");
            Require(ledger.PerReadRecovery is null, "Per-read source recovery is already authorized for this run.");
            return ledger.PerReadRecovery = new(sourceClock.GetUtcNow(), reason);
        });
    }

    public SourceRecoveryAuthorization AuthorizeUnclassifiedSourceRecovery(string kind, string target, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the user's explicit authorization to retry this unclassified read.");
        target = SourceTarget(kind, target);
        return WithSources(ledger =>
        {
            var read = RequireUnclassifiedSourceRead(ledger, kind, target);
            var authorization = new SourceRecoveryAuthorization(sourceClock.GetUtcNow(), reason);
            ledger.Reads[ledger.Reads.IndexOf(read)] = read with
            {
                RecoveryEligible = true, UnclassifiedRecovery = authorization
            };
            ledger.RecoveryConsumed = true;
            return authorization;
        });
    }

    public SourceErrorRecoveryAuthorization AuthorizeMatchingErrorSourceRecovery(string kind, string target, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the user's explicit authorization to retry matching failures in this run.");
        target = SourceTarget(kind, target);
        return WithSources(ledger =>
        {
            var read = RequireUnclassifiedSourceRead(ledger, kind, target);
            var error = read.Attempts[0].Error
                ?? throw new InvalidOperationException("The original source error is required.");
            Require(!MatchingErrorAuthorizations(ledger).Any(approval => MatchesSourceErrorRecovery(ledger, read, approval)),
                "Matching-error source recovery is already authorized for this error.");
            var authorization = new SourceErrorRecoveryAuthorization(kind, error, sourceClock.GetUtcNow(), reason);
            if (ledger.MatchingErrorRecovery is null) ledger.MatchingErrorRecovery = authorization;
            else ledger.AdditionalMatchingErrorRecoveries.Add(authorization);
            return authorization;
        });
    }

    public SourceRecoveryAuthorization AuthorizeNotFoundSourceRecovery(string kind, string target, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the user's explicit authorization to retry this not-found read.");
        target = SourceTarget(kind, target);
        return WithSources(ledger =>
        {
            var read = RequireFailedSourceRead(ledger, kind, target);
            Require(SourceOutcome(kind, target, SavedSourceResponse(read)).NotFound,
                "Only a failed 404 read can receive this authorization; denial and retry-delay rules still apply.");
            var authorization = new SourceRecoveryAuthorization(sourceClock.GetUtcNow(), reason);
            ledger.Reads[ledger.Reads.IndexOf(read)] = read with
            {
                RecoveryEligible = true, NotFoundRecovery = authorization
            };
            ledger.RecoveryConsumed = true;
            return authorization;
        });
    }

    public SourceRecoveryAuthorization AuthorizePhotoOmission(string id, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record the user's explicit authorization to omit this photo.");
        Require(Guid.TryParse(id, out _), "Use the retained person's exact directory ID.");
        id = Guid.Parse(id).ToString();
        var target = $"/users/{id}/photo/$value";
        return WithSources(ledger =>
        {
            Require(ledger.StopReason is null, ledger.StopReason ?? "");
            Require(ledger.Reads.All(read => read.State != "active"), "Settle all active source claims before omitting a photo.");
            Success(PhotoMetadata(id), "Fresh photo metadata");
            RequireCompletedSource(ledger, "structured", PhotoPath(id));
            var read = ledger.Reads.SingleOrDefault(read => read.Kind == "photo" && read.Target == target)
                ?? throw new InvalidOperationException("The failed photo download is not recorded in this run.");
            Require(read.State == "failed" && read.Attempts.Count == 2 && read.PhotoOmission is null
                && read.Attempts.All(attempt => attempt.FinishedAt is not null && attempt.Error is not null
                    && attempt.ResultFile is not null),
                "Omit only a settled, exhausted photo download with no previous omission authorization.");
            foreach (var attempt in read.Attempts)
            {
                var result = Read<SourceResult[]>(Within(attempt.ResultFile!)).Single(result => result.Target == target);
                var outcome = SourceOutcome("photo", target, result.Response);
                Require(outcome.Error is not null && !outcome.Denied, "A successful or denied source read cannot be omitted.");
            }
            var authorization = new SourceRecoveryAuthorization(sourceClock.GetUtcNow(), reason);
            ledger.Reads[ledger.Reads.IndexOf(read)] = read with { PhotoOmission = authorization };
            return authorization;
        });
    }

    public SourceLedger BeginSourceRecovery() => WithSources(ledger =>
    {
        Require(ledger.StopReason is null, ledger.StopReason ?? "");
        Require(ledger.Reads.All(read => read.State != "active"), "Stop dispatch and settle or interrupt all active workers first.");
        foreach (var read in ledger.Reads.Where(read => read.State == "failed" && read.Attempts.Count > 0
            && read.Attempts[^1].ResultFile is not null))
        {
            var outcome = SourceOutcome(read.Kind, read.Target, SavedSourceResponse(read, read.Attempts.Count - 1));
            if (!outcome.Denied) continue;
            ledger.StopReason = "Source authorization/access/policy denial stops this invocation: " + outcome.Error;
            return ledger;
        }
        var transientRecoveryAllowed = !ledger.RecoveryConsumed || ledger.PerReadRecovery is not null
            || ledger.BestEffortEnrichment is not null;
        var matching = MatchingErrorAuthorizations(ledger).ToArray();
        Require(transientRecoveryAllowed || matching.Length > 0,
            "The invocation-wide failed-read recovery pass is already consumed.");
        var eligible = ledger.Reads.Where(read => read.State == "failed"
            && !read.RecoveryEligible && read.Attempts.Count == 1
            && (transientRecoveryAllowed && CanRetrySourceFailure(read)
                || matching.Any(approval => MatchesSourceErrorRecovery(ledger, read, approval)))).ToArray();
        Require(eligible.Length > 0, "There are no eligible failed source reads to recover.");
        ledger.RecoveryConsumed = true;
        foreach (var read in eligible)
            ledger.Reads[ledger.Reads.IndexOf(read)] = read with { RecoveryEligible = true };
        return ledger;
    });

    private SourceRead RequireUnclassifiedSourceRead(SourceLedger ledger, string kind, string target)
    {
        var read = RequireFailedSourceRead(ledger, kind, target);
        Require(IsUnclassifiedSourceFailure(read),
            "Only a failure without a source status or error code can receive this authorization; denial and retry-delay rules still apply.");
        return read;
    }

    private static SourceRead RequireFailedSourceRead(SourceLedger ledger, string kind, string target)
    {
        Require(ledger.StopReason is null, ledger.StopReason ?? "");
        Require(ledger.Reads.All(read => read.State != "active"), "Settle all active source claims before authorizing recovery.");
        var read = ledger.Reads.SingleOrDefault(read => read.Kind == kind && read.Target == target)
            ?? throw new InvalidOperationException("The requested source read is not recorded in this run.");
        Require(read.State == "failed" && read.Attempts.Count == 1 && !read.RecoveryEligible
            && read.UnclassifiedRecovery is null && read.NotFoundRecovery is null,
            "Authorize only an unapproved failed read's second attempt.");
        return read;
    }

    private bool IsUnclassifiedSourceFailure(SourceRead read)
        => SourceOutcome(read.Kind, read.Target, SavedSourceResponse(read)).Unclassified;

    private bool CanRetrySourceFailure(SourceRead read)
        => read.Attempts[0].ResultFile is null ? read.Attempts[0].Retryable
            : SourceOutcome(read.Kind, read.Target, SavedSourceResponse(read)).Retryable;

    private static IEnumerable<SourceErrorRecoveryAuthorization> MatchingErrorAuthorizations(SourceLedger ledger)
    {
        if (ledger.MatchingErrorRecovery is { } first) yield return first;
        foreach (var additional in ledger.AdditionalMatchingErrorRecoveries) yield return additional;
    }

    private bool MatchesSourceErrorRecovery(SourceLedger ledger, SourceRead read, SourceErrorRecoveryAuthorization approval)
    {
        if (read.Kind != approval.Kind || !IsUnclassifiedSourceFailure(read)) return false;
        if (read.Attempts[0].Error == approval.Error) return true;
        var original = ledger.Reads.Where(candidate => candidate.Kind == approval.Kind)
            .SelectMany(candidate => SourceAttempts(candidate).Select(attempt => (Read: candidate, Attempt: attempt)))
            .FirstOrDefault(candidate => candidate.Attempt.Error == approval.Error);
        var originalRead = original.Read
            ?? throw new InvalidOperationException("The approved matching-error source evidence is missing.");
        var originalResponse = SavedSourceResponse(originalRead, original.Attempt);
        Require(SourceOutcome(originalRead.Kind, originalRead.Target, originalResponse).Unclassified,
            "The approved source result must retain its unclassified failure.");
        var errors = SourceErrorKeys(SavedSourceResponse(read));
        return errors.Length > 0
            && errors.SequenceEqual(SourceErrorKeys(originalResponse), StringComparer.Ordinal);
    }

    private static string[] SourceErrorKeys(JsonObject response)
        => SourceObjects(response).Select(obj => obj["error"]).OfType<JsonNode>()
            .Select(CanonicalSourceError)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string CanonicalSourceError(JsonNode error)
    {
        var value = error is JsonObject { Count: 1 } obj && obj["message"] is JsonValue message ? message : error;
        if (value is not JsonValue json || !json.TryGetValue<string>(out var text)) return error.ToJsonString();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var matching = ProfileFailureMessages.Where(diagnostic => text.Trim() == diagnostic
            || lines.Any(line => line.Trim() == "- **snippet:** " + diagnostic)).ToArray();
        return JsonSerializer.Serialize(matching.Length == 1 ? matching[0] : text);
    }

    private JsonObject SavedSourceResponse(SourceRead read, int attemptIndex = 0)
        => SavedSourceResponse(read, read.Attempts[attemptIndex]);

    private JsonObject SavedSourceResponse(SourceRead read, SourceAttempt attempt)
    {
        var resultFile = attempt.ResultFile
            ?? throw new InvalidOperationException("The original source result is required to classify this failure.");
        var result = Read<SourceResult[]>(Within(resultFile)).Single(result => result.Target == read.Target);
        return result.Response;
    }

    public SourceLedger InterruptSource(Guid claimId, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record why the coordinator knows the source worker stopped.");
        return WithSources(ledger =>
        {
            var active = ledger.Reads.Where(read => read.State == "active" && read.Attempts[^1].ClaimId == claimId).ToArray();
            Require(active.Length > 0, "Only an active source claim can be interrupted.");
            foreach (var read in active)
            {
                read.Attempts[^1] = read.Attempts[^1] with
                {
                    FinishedAt = sourceClock.GetUtcNow(), Error = "Interrupted source read: " + reason, Retryable = true
                };
                ledger.Reads[ledger.Reads.IndexOf(read)] = read with { State = "failed" };
            }
            return ledger;
        });
    }

    private (string? Error, bool Retryable, DateTimeOffset? RetryAt, bool Denied, bool Unclassified, bool NotFound, bool InvalidDelay) SourceOutcome(
        string kind, string target, JsonObject response)
    {
        var objects = SourceObjects(response).ToArray();
        var codes = objects.Select(obj => SourceText(obj["code"])).Where(code => code.Length > 0).ToArray();
        var statuses = objects.SelectMany(SourceStatuses).ToArray();
        var denied = statuses.Any(status => status is 401 or 403) || codes.Any(code =>
            SourceDenialCodes.Contains(code, StringComparer.OrdinalIgnoreCase)
            || code.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || code.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase))
            || objects.Any(obj => obj["error"] is JsonValue error && error.TryGetValue<string>(out var text) && SourceDenialText(text)
                || obj["error"] is JsonObject detail && SourceDenialText(SourceText(detail["message"])));
        var transient = !denied && (statuses.Any(status => status is 408 or 429 or >= 500 and <= 599)
            || codes.Any(code => new[] { "TooManyRequests", "CircuitOpen", "ServiceUnavailable", "Timeout" }
                .Contains(code, StringComparer.OrdinalIgnoreCase)));
        var expectedAbsence = !denied && !transient && SourceStatusCode(response["statusCode"]) == 404
            && (kind == "structured" && target.Split('?')[0].EndsWith("/manager", StringComparison.Ordinal)
                && codes.Length > 0 && codes.All(code => code.Equals("Request_ResourceNotFound", StringComparison.OrdinalIgnoreCase))
                || kind == "structured" && target.Split('?')[0].EndsWith("/photo", StringComparison.Ordinal)
                && codes.Length > 0 && codes.All(code => code.Equals("ImageNotFound", StringComparison.OrdinalIgnoreCase)));
        if (expectedAbsence || !denied && !transient && SourceStatusCode(response["statusCode"]) == 200
            && statuses.All(status => status is >= 200 and < 300)
            && objects.All(obj => obj["error"] is null))
            return (null, false, null, false, false, false, false);
        var now = sourceClock.GetUtcNow();
        DateTimeOffset? retryAt = null;
        string? delayError = null;
        foreach (var obj in objects)
            foreach (var (name, value) in obj)
            {
                DateTimeOffset? candidate = null;
                var text = SourceText(value);
                var header = name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase);
                var milliseconds = name.Equals("retryAfterMilliseconds", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-ms-retry-after-ms", StringComparison.OrdinalIgnoreCase);
                if (!header && !milliseconds && !name.Equals("retryAfterSeconds", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("retryAfter", StringComparison.OrdinalIgnoreCase)) continue;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay)
                    && double.IsFinite(delay) && delay >= 0
                    && delay < (DateTimeOffset.MaxValue - now).TotalMilliseconds / (milliseconds ? 1 : 1000))
                    candidate = now.AddMilliseconds(delay * (milliseconds ? 1 : 1000));
                else if (header && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    candidate = date;
                else delayError = "Invalid returned retry delay: " + name + "=" + text;
                if (candidate is not null && (retryAt is null || candidate > retryAt)) retryAt = candidate;
            }
        var failures = objects.Where(obj => SourceStatuses(obj).Any(status => status is < 200 or >= 300)).ToArray();
        if (failures.Length == 0)
            failures = objects.Where(obj => obj["error"] is not null || obj["code"] is not null).ToArray();
        var error = string.Join("; ", failures.Select(obj =>
        {
            var status = SourceStatuses(obj).FirstOrDefault(status => status is < 200 or >= 300);
            var description = status is null ? "Source error" : $"HTTP {status}";
            return description + ": " + (obj["error"] ?? obj).ToJsonString();
        }));
        if (error.Length == 0) error = "Source read failed without a structured source status or error.";
        if (delayError is not null) error += "; " + delayError;
        var unclassified = !denied && !transient && delayError is null && codes.Length == 0
            && statuses.All(status => status is >= 200 and < 300);
        var notFound = !denied && !transient && delayError is null && statuses.Contains(404)
            && statuses.All(status => status is >= 200 and < 300 or 404);
        return (error, transient && delayError is null, retryAt, denied, unclassified, notFound, delayError is not null);
    }

    private SourceLedger RequireSourcesComplete()
    {
        var ledger = SourceStatus();
        var unresolved = ledger.Reads.Where(read => !read.AccountedFor).ToArray();
        Require(ledger.StopReason is null && unresolved.Length == 0, "Source collection is incomplete: " + ledger.StopReason + string.Join("; ",
            unresolved.Select(read => $"{read.Kind} {read.Target}: {read.State}; {read.Attempts.LastOrDefault()?.Error}")));
        foreach (var attempt in ledger.Reads.SelectMany(read => read.PreviousObservations).SelectMany(history => history.Attempts))
        {
            if (attempt.ResultFile is { } resultFile)
                Require(File.Exists(Within(resultFile)), "Retained Manager source evidence is missing.");
            else if (attempt.CaptureFailureFile is { } failureFile)
                Require(File.Exists(Within(failureFile)), "Retained Manager capture diagnostics are missing.");
            else
                Require(attempt.FinishedAt is not null && !string.IsNullOrWhiteSpace(attempt.Error),
                    "Retained Manager interruption diagnostics are missing.");
        }
        foreach (var read in ledger.Reads.Where(IsPhotoOmitted))
            foreach (var attempt in read.Attempts)
                Require(File.Exists(Within(attempt.ResultFile!)), "Omitted photo source evidence is missing.");
        foreach (var read in ledger.Reads.Where(IsEnrichmentUnavailable))
        {
            Require(ledger.BestEffortEnrichment is not null, "Unavailable enrichment requires recorded collection policy.");
            foreach (var attempt in read.Attempts)
            {
                if (attempt.ResultFile is { } resultFile)
                {
                    var result = Read<SourceResult[]>(Within(resultFile)).Single(result => result.Target == read.Target);
                    var outcome = SourceOutcome(read.Kind, read.Target, result.Response);
                    Require(outcome.Error is not null && !outcome.Denied && !outcome.InvalidDelay,
                        "A successful, denied or invalid-delay source read cannot become unavailable enrichment.");
                }
                else
                {
                    var evidence = Read<JsonObject>(Within(attempt.CaptureFailureFile!));
                    Require(evidence["claimId"]?.GetValue<Guid>() == attempt.ClaimId
                        && evidence["ticket"]?.GetValue<string>() == attempt.CaptureTicket
                        && Hash(File.ReadAllBytes(Within(attempt.CaptureTicket!))) == attempt.CaptureTicketSha256,
                        "Unavailable capture diagnostics no longer match their source claim.");
                    var outcome = CaptureFailureOutcome(read.Kind, read.Target, evidence["completion"] as JsonObject);
                    Require(!outcome.Denied && !outcome.InvalidDelay,
                        "Capture diagnostics containing denial or an invalid retry delay cannot become unavailable enrichment.");
                }
            }
        }
        return ledger;
    }

    private static bool IsPhotoOmitted(SourceRead read)
        => read.Kind == "photo" && read.State == "failed" && read.PhotoOmission is { } approval
            && !string.IsNullOrWhiteSpace(approval.Reason) && read.Attempts.Count == 2
            && read.Attempts.All(attempt => attempt.FinishedAt is not null && attempt.Error is not null
                && !string.IsNullOrWhiteSpace(attempt.ResultFile));

    private static void RequireCompletedSource(SourceLedger ledger, string kind, string target)
        => Require(ledger.Reads.Any(read => read.Kind == kind && read.Target == SourceTarget(kind, target) && read.State == "completed"),
            $"Source read is not completed in the shared invocation record: {kind} {target}.");

    private static IEnumerable<SourceAttempt> SourceAttempts(SourceRead read)
        => read.PreviousObservations.SelectMany(history => history.Attempts).Concat(read.Attempts);

    private static IEnumerable<JsonObject> SourceObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var nested in SourceObjects(child)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>())
                foreach (var nested in SourceObjects(child)) yield return nested;
    }

    private static int? SourceStatusCode(JsonNode? value)
        => value is JsonValue json && int.TryParse(json.ToString(), CultureInfo.InvariantCulture, out var code) ? code : null;
    private static IEnumerable<int?> SourceStatuses(JsonObject obj)
    {
        foreach (var name in new[] { "statusCode", "httpStatusCode", "status" })
            if (SourceStatusCode(obj[name]) is { } status) yield return status;
        var message = obj["error"] is JsonObject error ? SourceText(error["message"]) : SourceText(obj["error"]);
        if (message.Length == 0) yield break;
        const string prefix = "Status code is ";
        foreach (var part in message.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(prefix, StringComparison.Ordinal)
                && Enum.TryParse<HttpStatusCode>(part[prefix.Length..], out var status) && Enum.IsDefined(status)
                && (int)status is >= 400 and <= 599)
                yield return (int)status;
    }
    private static string SourceText(JsonNode? value)
        => value is JsonValue json ? json.ToString() : "";

    private static bool SourceDenialText(string text)
        => SourceDenialCodes.Any(code => text.Contains(code, StringComparison.OrdinalIgnoreCase))
            || System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(?:(?:HTTP(?: status(?: code)?)?|status(?: code)?)\s*(?:is|:|=)?\s*(?:401|403)|(?:access|permission)\s+denied|blocked\s+by\s+policy|authentication\s+(?:required|failed)|consent\s+(?:is\s+)?required|insufficient\s+(?:privileges|scope|claims))\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private T WithSources<T>(Func<SourceLedger, T> action, bool save = true)
    {
        var file = System.IO.Path.Combine(path, "source-coordination.json");
        var lockFile = System.IO.Path.Combine(path, ".source-lock");
        Local(lockFile);
        FileStream gate;
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                gate = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
            }
        }
        using (gate)
        {
            Require(File.Exists(file), "The shared source record is missing. Start a fresh invocation; do not reset its recovery allowance.");
            var document = Read<JsonObject>(file);
            RequireVersion(StoredVersion(document));
            var ledger = document.Deserialize<SourceLedger>(Json) ?? throw new InvalidOperationException("The source ledger is missing.");
            Require(ledger.BatchId == run.BatchId, "Source coordination belongs to another invocation.");
            var result = action(ledger);
            if (save) Save(file, ledger);
            return result;
        }
    }

    private static string SourceTarget(string kind, string target)
    {
        if (kind == "profile")
        {
            Require(Guid.TryParse(target, out _), "Use exact directory IDs for grounded profile reads.");
            return Guid.Parse(target).ToString();
        }
        target = Normalize(target);
        if (kind == "photo")
            Require(target.Split('/') is ["", "users", var id, "photo", "$value"] && Guid.TryParse(id, out _),
                "Use the assigned directory user's exact photo binary path.");
        return target;
    }
}
