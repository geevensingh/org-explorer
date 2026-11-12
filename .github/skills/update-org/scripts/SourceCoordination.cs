using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    public sealed record SourceAttempt(Guid ClaimId, string Worker, DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt, string? Error, bool Retryable, DateTimeOffset? RetryAt, string? ResultFile = null);
    public sealed record SourceRead(string Kind, string Target, string State, List<SourceAttempt> Attempts,
        bool RecoveryEligible = false);
    public sealed record SourceLedger(Guid BatchId, List<SourceRead> Reads)
    {
        public bool RecoveryConsumed { get; set; }
        public string? StopReason { get; set; }
    }
    public sealed record SourceClaim(Guid? ClaimId, string Kind, string Worker, string[] Targets, string? Blocker,
        DateTimeOffset? RetryAt = null);
    public sealed record SourceResult(string Target, JsonObject Response);
    public sealed record SourceCompletion(Guid BatchId, Guid ClaimId, SourceRead[] Reads,
        bool RecoveryConsumed, string? StopReason);

    public SourceLedger SourceStatus() => WithSources(ledger => ledger);

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
            foreach (var target in targets)
                if (!ledger.Reads.Any(read => read.Kind == kind && read.Target == target))
                    ledger.Reads.Add(new(kind, target, "deferred", []));
            var pending = ledger.Reads.Where(read => read.Kind == kind && targets.Contains(read.Target)
                && read.State != "completed").ToArray();
            var now = sourceClock.GetUtcNow();
            var retryAt = ledger.Reads.SelectMany(read => read.Attempts).Max(attempt => attempt.RetryAt);
            SourceClaim Blocked(string reason) => new(null, kind, worker, pending.Select(read => read.Target).ToArray(),
                reason, retryAt > now ? retryAt : null);
            if (ledger.StopReason is not null) return Blocked(ledger.StopReason);
            if (pending.Length == 0) return Blocked("These source reads are already completed.");
            var active = ledger.Reads.Where(read => read.State == "active").ToArray();
            if (active.Any(read => read.Attempts[^1].Worker == worker) || pending.Any(read => read.State == "active"))
                return Blocked("A requested read or this worker already has an active claim.");
            if (ledger.Reads.Any(read => read.State == "failed" || read.RecoveryEligible && read.State != "completed"))
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
                read.Attempts[^1] = read.Attempts[^1] with
                {
                    FinishedAt = sourceClock.GetUtcNow(), Error = outcome.Error,
                    Retryable = outcome.Retryable, RetryAt = outcome.RetryAt, ResultFile = resultFile
                };
                ledger.Reads[ledger.Reads.IndexOf(read)] = read with { State = outcome.Error is null ? "completed" : "failed" };
            }
            return new SourceCompletion(ledger.BatchId, claimId,
                ledger.Reads.Where(read => read.Attempts.LastOrDefault()?.ClaimId == claimId).ToArray(),
                ledger.RecoveryConsumed, ledger.StopReason);
        });
    }

    public SourceLedger BeginSourceRecovery() => WithSources(ledger =>
    {
        Require(ledger.StopReason is null, ledger.StopReason ?? "");
        Require(!ledger.RecoveryConsumed, "The invocation-wide failed-read recovery pass is already consumed.");
        Require(ledger.Reads.All(read => read.State != "active"), "Stop dispatch and settle or interrupt all active workers first.");
        var eligible = ledger.Reads.Where(read => read.State == "failed"
            && read.Attempts.Count == 1 && read.Attempts[^1].Retryable).ToArray();
        Require(eligible.Length > 0, "There are no eligible failed source reads to recover.");
        ledger.RecoveryConsumed = true;
        foreach (var read in eligible)
            ledger.Reads[ledger.Reads.IndexOf(read)] = read with { RecoveryEligible = true };
        return ledger;
    });

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

    private (string? Error, bool Retryable, DateTimeOffset? RetryAt, bool Denied) SourceOutcome(string kind, string target, JsonObject response)
    {
        var objects = SourceObjects(response).ToArray();
        var codes = objects.Select(obj => SourceText(obj["code"])).Where(code => code.Length > 0).ToArray();
        var statuses = objects.SelectMany(SourceStatuses).ToArray();
        var denied = statuses.Any(status => status is 401 or 403) || codes.Any(code =>
            new[] { "Forbidden", "Unauthorized", "AccessDenied", "ErrorAccessDenied", "Authorization_RequestDenied",
                "InvalidAuthenticationToken", "AuthenticationRequired", "AuthenticationFailed", "PolicyDenied",
                "BlockedByPolicy", "PolicyViolation", "InsufficientPrivileges", "insufficient_claims" }
                .Contains(code, StringComparer.OrdinalIgnoreCase)
            || code.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || code.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase));
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
            return (null, false, null, false);
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
        return (error, transient && delayError is null, retryAt, denied);
    }

    private SourceLedger RequireSourcesComplete()
    {
        var ledger = SourceStatus();
        var unresolved = ledger.Reads.Where(read => read.State != "completed").ToArray();
        Require(ledger.StopReason is null && unresolved.Length == 0, "Source collection is incomplete: " + ledger.StopReason + string.Join("; ",
            unresolved.Select(read => $"{read.Kind} {read.Target}: {read.State}; {read.Attempts.LastOrDefault()?.Error}")));
        return ledger;
    }

    private static void RequireCompletedSource(SourceLedger ledger, string kind, string target)
        => Require(ledger.Reads.Any(read => read.Kind == kind && read.Target == SourceTarget(kind, target) && read.State == "completed"),
            $"Source read is not completed in the shared invocation record: {kind} {target}.");

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
        => new[] { "statusCode", "httpStatusCode", "status" }.Select(name => SourceStatusCode(obj[name]))
            .Where(status => status is not null);
    private static string SourceText(JsonNode? value)
        => value is JsonValue json ? json.ToString() : "";

    private T WithSources<T>(Func<SourceLedger, T> action)
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
            var ledger = Read<SourceLedger>(file);
            Require(ledger.BatchId == run.BatchId, "Source coordination belongs to another invocation.");
            var result = action(ledger);
            Save(file, ledger);
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
