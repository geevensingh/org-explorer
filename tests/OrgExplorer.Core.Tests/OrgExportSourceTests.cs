using System.Text.Json;
using System.Text.Json.Nodes;
using OrgExplorer.OrgExport;
using Xunit;

namespace OrgExplorer.Core.Tests;

[Trait("Category", "Filesystem")]
public sealed class OrgExportSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OrgExportSourceTests-" + Guid.NewGuid());
    private const string RootId = "10000000-0000-0000-0000-000000000001";
    private const string AccountId = "20000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Concurrent_workers_share_two_slots_and_preserve_deferred_first_reads_across_handoffs()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var start = new Barrier(6);
        var claims = await Task.WhenAll(Enumerable.Range(1, 6).Select(index => Task.Run(() =>
        {
            using var worker = ExportSession.Open(path);
            start.SignalAndWait();
            return worker.ClaimSource("worker-" + index, "profile", [Identity(index)]);
        })));
        Assert.Equal(2, claims.Count(claim => claim.ClaimId is not null));
        using var next = ExportSession.Open(path);
        var status = next.SourceStatus();
        Assert.Equal(2, status.Reads.Count(read => read.State == "active"));
        Assert.Equal(4, status.Reads.Count(read => read.State == "deferred" && read.Attempts.Count == 0));
        var completed = claims.First(claim => claim.ClaimId is not null);
        Finish(next, completed, Ok());
        var savedResult = next.SourceStatus().Reads.Single(read => read.Target == completed.Targets.Single()).Attempts.Single().ResultFile;
        Assert.NotNull(savedResult);
        Assert.True(File.Exists(Path.Combine(path, savedResult)));
        var deferred = claims.First(claim => claim.ClaimId is null);
        var admitted = next.ClaimSource("replacement", "profile", deferred.Targets);
        Assert.NotNull(admitted.ClaimId);
        Assert.Single(next.SourceStatus().Reads.Single(read => read.Target == deferred.Targets.Single()).Attempts);
        Assert.Null(next.ClaimSource("another-phase", "profile", completed.Targets).ClaimId);
        Assert.Equal("completed", next.SourceStatus().Reads.Single(read => read.Target == completed.Targets.Single()).State);
        Assert.Throws<InvalidOperationException>(() => next.ClaimSource("oversized", "profile",
            Enumerable.Range(10, 5).Select(Identity).ToArray()));
    }

    [Fact]
    public void One_recovery_pass_retains_partial_success_honors_delays_and_cannot_reset_in_a_later_phase()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var now = DateTimeOffset.UtcNow;
        var clock = new SourceClock(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)));
        using (var worker = ExportSession.Open(path, sourceClock: clock))
        {
            var profile = worker.ClaimSource("profiles-1", "profile", [Identity(1), Identity(2)]);
            var photo = worker.ClaimSource("photos-1", "photo", [$"/users/{RootId}/photo/$value"]);
            Finish(worker, profile, Ok(), new JsonObject
            {
                ["statusCode"] = 200, ["data"] = new JsonObject
                {
                    ["results"] = new JsonArray(new JsonObject
                    {
                        ["statusCode"] = 429, ["headers"] = new JsonObject { ["Retry-After"] = "17" },
                        ["error"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "TooManyRequests" } }
                    })
                }
            });
            Assert.Throws<InvalidOperationException>(() => worker.BeginSourceRecovery());
            var waiting = worker.ClaimSource("next-batch", "profile", [Identity(3)]);
            Assert.Null(waiting.ClaimId);
            Finish(worker, photo, new JsonObject
            {
                ["statusCode"] = 503, ["headers"] = new JsonObject
                {
                    ["Retry-After"] = clock.Now.AddSeconds(35).ToString("R")
                },
                ["error"] = new JsonObject { ["code"] = "CircuitOpen" }
            });
            worker.BeginSourceRecovery();
        }
        using var nextPhase = ExportSession.Open(path, sourceClock: clock);
        Assert.True(nextPhase.SourceStatus().RecoveryConsumed);
        Assert.Throws<InvalidOperationException>(() => nextPhase.BeginSourceRecovery());
        var tooEarly = nextPhase.ClaimSource("replacement", "profile", [Identity(1), Identity(2)]);
        Assert.Null(tooEarly.ClaimId);
        Assert.Equal(clock.Now.AddSeconds(35), tooEarly.RetryAt);
        Assert.Null(nextPhase.ClaimSource("next-batch", "profile", [Identity(3)]).ClaimId);
        clock.Now = clock.Now.AddSeconds(17);
        Assert.Null(nextPhase.ClaimSource("replacement", "profile", [Identity(1), Identity(2)]).ClaimId);
        clock.Now = clock.Now.AddSeconds(18);
        var retry = nextPhase.ClaimSource("replacement", "profile", [Identity(1), Identity(2)]);
        Assert.Equal([Identity(2)], retry.Targets);
        Assert.Null(nextPhase.ClaimSource("next-batch", "profile", [Identity(3)]).ClaimId);
        Finish(nextPhase, retry, Ok());
        Assert.Single(nextPhase.SourceStatus().Reads.Single(read => read.Target == Identity(1)).Attempts);
        var photoRetry = nextPhase.ClaimSource("new-photo-worker", "photo", [$"/users/{RootId}/photo/$value"]);
        Finish(nextPhase, photoRetry, Ok());
        var firstRead = nextPhase.ClaimSource("next-batch", "profile", [Identity(3)]);
        Assert.NotNull(firstRead.ClaimId);
        Finish(nextPhase, firstRead, new JsonObject
        {
            ["statusCode"] = 429, ["error"] = new JsonObject { ["code"] = "TooManyRequests" }
        });
        Assert.Null(nextPhase.ClaimSource("yet-another-phase", "profile", [Identity(3)]).ClaimId);
        Assert.Single(nextPhase.SourceStatus().Reads.Single(read => read.Target == Identity(3)).Attempts);
        Assert.Contains("source", Assert.Throws<InvalidOperationException>(() => nextPhase.Publish()).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401, "InvalidAuthenticationToken")]
    [InlineData(403, "Forbidden")]
    [InlineData(200, "Authorization_RequestDenied")]
    [InlineData(200, "PolicyDenied")]
    public void Nested_denial_stops_active_and_queued_dispatch_even_when_wrapped_as_a_transient_error(int status, string code)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        Guid deniedClaim;
        using (var worker = ExportSession.Open(path))
        {
            var claim = worker.ClaimSource("first", "profile", [Identity(1), Identity(2)]);
            deniedClaim = claim.ClaimId!.Value;
            var other = worker.ClaimSource("already-running", "profile", [Identity(3)]);
            Assert.Null(worker.ClaimSource("queued", "profile", [Identity(4)]).ClaimId);
            Finish(worker, claim, Ok(), new JsonObject
            {
                ["statusCode"] = 503, ["error"] = new JsonObject
                {
                    ["code"] = "CircuitOpen", ["innerError"] = new JsonObject
                    {
                        ["statusCode"] = status, ["code"] = code
                    }
                }
            });
            Finish(worker, other, Ok());
        }
        using var coordinator = ExportSession.Open(path);
        Assert.NotNull(coordinator.SourceStatus().StopReason);
        Assert.Equal("completed", coordinator.SourceStatus().Reads.Single(read => read.Target == Identity(1)).State);
        Assert.Null(coordinator.ClaimSource("replacement", "profile", [Identity(2)]).ClaimId);
        Assert.Null(coordinator.ClaimSource("queued", "profile", [Identity(4)]).ClaimId);
        Assert.Null(coordinator.ClaimSource("different-phase", "structured", [ExportSession.UserPath(RootId)]).ClaimId);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginSourceRecovery());
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginSource503Recovery("User-approved 503 continuation."));
        Assert.Throws<InvalidOperationException>(() => coordinator.Defer503PhotoMetadata("User requested the rest."));
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginProfileOpenRecovery(Identity(2), "User approved one retry."));
        Assert.Throws<InvalidOperationException>(() => coordinator.InterruptSource(deniedClaim, "Cannot undo a completed denial."));
        Assert.Contains("source", Assert.Throws<InvalidOperationException>(() => coordinator.Publish()).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authorized_later_503_recovery_preserves_delays_and_requires_settled_claims_and_original_evidence()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using (var session = ExportSession.Open(path, sourceClock: clock))
        {
            Assert.Throws<InvalidOperationException>(() => session.BeginSource503Recovery("Explicit authorization."));
            var first = session.ClaimSource("first", "profile", [Identity(1)]);
            Finish(session, first, new JsonObject { ["statusCode"] = 503 });
            session.BeginSourceRecovery();
            Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
            var later = session.ClaimSource("metadata", "structured", [ExportSession.PhotoPath(RootId)]);
            var other = session.ClaimSource("already-running", "profile", [Identity(2)]);
            Finish(session, later, new JsonObject
            {
                ["statusCode"] = 503, ["headers"] = new JsonObject { ["Retry-After"] = "450" }
            });
            Assert.Throws<InvalidOperationException>(() => session.BeginSource503Recovery("Explicit authorization."));
            Finish(session, other, Ok());
            Assert.Throws<InvalidOperationException>(() => session.BeginSource503Recovery(" "));
            var status = session.BeginSource503Recovery("User authorized future delayed HTTP 503 retries.");
            var authorization = Assert.Single(status.ServiceRecoveries);
            Assert.Equal(clock.Now, authorization.AuthorizedAt);
            Assert.Equal(new("structured", ExportSession.PhotoPath(RootId), later.ClaimId!.Value),
                Assert.Single(authorization.Targets));
            Assert.True(status.RecoveryConsumed);
        }
        using var resumed = ExportSession.Open(path, sourceClock: clock);
        var blocked = resumed.ClaimSource("retry", "structured", [ExportSession.PhotoPath(RootId)]);
        Assert.Null(blocked.ClaimId);
        Assert.Equal(clock.Now.AddSeconds(450), blocked.RetryAt);
        clock.Now = clock.Now.AddSeconds(449);
        Assert.Null(resumed.ClaimSource("retry", "structured", [ExportSession.PhotoPath(RootId)]).ClaimId);
        Assert.Null(resumed.ClaimSource("queued", "profile", [Identity(3)]).ClaimId);
        clock.Now = clock.Now.AddSeconds(1);
        var retry = resumed.ClaimSource("retry", "structured", [ExportSession.PhotoPath(RootId)]);
        Assert.NotNull(retry.ClaimId);
        Assert.Null(resumed.ClaimSource("queued", "profile", [Identity(3)]).ClaimId);
        Finish(resumed, retry, Ok());
        Assert.NotNull(resumed.ClaimSource("queued", "profile", [Identity(3)]).ClaimId);
        Assert.Single(resumed.SourceStatus().ServiceRecoveries);
    }

    [Theory]
    [InlineData(429, "TooManyRequests", null)]
    [InlineData(500, "InternalServerError", null)]
    [InlineData(404, "Request_ResourceNotFound", null)]
    [InlineData(403, "Forbidden", null)]
    [InlineData(503, "Authorization_RequestDenied", null)]
    [InlineData(503, "ServiceUnavailable", "invalid")]
    public void Authorized_503_exception_cannot_bypass_other_failures_or_denials(int status, string code, string? delay)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var first = session.ClaimSource("first", "profile", [Identity(1)]);
        Finish(session, first, new JsonObject { ["statusCode"] = 503 });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
        var later = session.ClaimSource("later", "profile", [Identity(2)]);
        var response = new JsonObject
        {
            ["statusCode"] = status, ["error"] = new JsonObject { ["code"] = code }
        };
        if (delay is not null) response["headers"] = new JsonObject { ["Retry-After"] = delay };
        Finish(session, later, response);

        Assert.Throws<InvalidOperationException>(() => session.BeginSource503Recovery("Explicit user authorization."));

        Assert.Empty(session.SourceStatus().ServiceRecoveries);
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts);
        Assert.Null(session.ClaimSource("blocked-retry", "profile", [Identity(2)]).ClaimId);
        Assert.True(session.SourceStatus().RecoveryConsumed);
    }

    [Fact]
    public void Metadata_deferral_preserves_returned_delays_and_requires_explicit_reason_and_settled_calls()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using (var session = ExportSession.Open(path, sourceClock: clock))
        {
            var first = session.ClaimSource("first", "structured", [ExportSession.PhotoPath(RootId)]);
            Assert.Throws<InvalidOperationException>(() => session.Defer503PhotoMetadata("User requested unread entries."));
            Finish(session, first, new JsonObject { ["statusCode"] = 503 });
            Assert.Throws<InvalidOperationException>(() => session.Defer503PhotoMetadata("User requested unread entries."));
            session.BeginSourceRecovery();
            var retry = session.ClaimSource("retry", "structured", [ExportSession.PhotoPath(RootId)]);
            Finish(session, retry, new JsonObject
            {
                ["statusCode"] = 503, ["headers"] = new JsonObject { ["Retry-After"] = "450" }
            });
            Assert.Throws<InvalidOperationException>(() => session.Defer503PhotoMetadata(" "));
            var ledger = session.Defer503PhotoMetadata("User requested unread entries.");
            Assert.Equal(clock.Now, Assert.Single(ledger.MetadataDeferrals).RecordedAt);
        }
        using var resumed = ExportSession.Open(path, sourceClock: clock);
        var blocked = resumed.ClaimSource("unread", "structured", [ExportSession.PhotoPath(Identity(2))]);
        Assert.Null(blocked.ClaimId);
        Assert.Equal(clock.Now.AddSeconds(450), blocked.RetryAt);
        clock.Now = clock.Now.AddSeconds(450);
        Assert.Null(resumed.ClaimSource("third-attempt", "structured", [ExportSession.PhotoPath(RootId)]).ClaimId);
        var unread = resumed.ClaimSource("unread", "structured", [ExportSession.PhotoPath(Identity(2))]);
        Assert.NotNull(unread.ClaimId);
        Finish(resumed, unread, Ok());
        Assert.Equal("failed", resumed.SourceStatus().Reads.Single(read => read.Target == ExportSession.PhotoPath(RootId)).State);
        Assert.Contains("Source collection is incomplete", Assert.Throws<InvalidOperationException>(() => resumed.Publish()).Message);
    }

    [Theory]
    [InlineData("profile", 503)]
    [InlineData("photo", 503)]
    [InlineData("directory", 503)]
    [InlineData("metadata", 500)]
    [InlineData("metadata", 403)]
    public void Metadata_deferral_cannot_skip_other_source_types_or_non503_failures(string kind, int status)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var target = kind switch
        {
            "profile" => Identity(1),
            "photo" => $"/users/{RootId}/photo/$value",
            "directory" => ExportSession.ReportsPath(RootId),
            _ => ExportSession.PhotoPath(RootId)
        };
        var sourceKind = kind is "profile" or "photo" ? kind : "structured";
        Finish(session, session.ClaimSource("first", sourceKind, [target]), new JsonObject { ["statusCode"] = 503 });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("retry", sourceKind, [target]), new JsonObject { ["statusCode"] = status });

        Assert.Throws<InvalidOperationException>(() => session.Defer503PhotoMetadata("User requested unread entries."));

        Assert.Empty(session.SourceStatus().MetadataDeferrals);
        Assert.Null(session.ClaimSource("unread", "profile", [Identity(2)]).ClaimId);
        Assert.Equal(2, session.SourceStatus().Reads.Single(read => read.Target == target).Attempts.Count);
    }

    [Theory]
    [InlineData(403, null, "Open '' Failure: Unable to open, Exception Caught.")]
    [InlineData(404, null, "Open '' Failure: Unable to open, Exception Caught.")]
    [InlineData(503, null, "Open '' Failure: Unable to open, Exception Caught.")]
    [InlineData(null, "PolicyDenied", "Open '' Failure: Unable to open, Exception Caught.")]
    [InlineData(null, null, "Another source failure.")]
    [InlineData(null, null, "Earlier diagnostic: Open '' Failure: Unable to open, Exception Caught.")]
    public void Profile_open_recovery_cannot_reclassify_structured_or_unrelated_failures(int? status, string? code, string message)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        Finish(session, session.ClaimSource("initial", "profile", [Identity(1)]), new JsonObject { ["statusCode"] = 503 });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("initial-retry", "profile", [Identity(1)]), Ok());
        var response = new JsonObject { ["error"] = new JsonObject { ["message"] = message } };
        if (status is not null) response["statusCode"] = status;
        if (code is not null) response["error"]!["code"] = code;
        Finish(session, session.ClaimSource("profile", "profile", [Identity(2)]), response);

        Assert.Throws<InvalidOperationException>(() => session.BeginProfileOpenRecovery(Identity(2), "User approved one retry."));

        Assert.Empty(session.SourceStatus().ServiceRecoveries);
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts);
        Assert.Null(session.ClaimSource("blocked", "profile", [Identity(2)]).ClaimId);
    }

    [Fact]
    public void Profile_open_recovery_requires_settled_calls_and_preserves_returned_delays()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        Finish(session, session.ClaimSource("initial", "profile", [Identity(1)]), new JsonObject { ["statusCode"] = 503 });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("initial-retry", "profile", [Identity(1)]), Ok());
        var profile = session.ClaimSource("profile", "profile", [Identity(2)]);
        var other = session.ClaimSource("other", "profile", [Identity(3)]);
        Finish(session, profile, new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = "Open '' Failure: Unable to open, Exception Caught." },
            ["retryAfterSeconds"] = 450
        });
        Assert.Throws<InvalidOperationException>(() => session.BeginProfileOpenRecovery(Identity(2), "User approved one retry."));
        Finish(session, other, Ok());
        Assert.Throws<InvalidOperationException>(() => session.BeginProfileOpenRecovery(Identity(2), ""));
        session.BeginProfileOpenRecovery(Identity(2), "User approved one retry.");
        var blocked = session.ClaimSource("retry", "profile", [Identity(2)]);
        Assert.Null(blocked.ClaimId);
        Assert.Equal(clock.Now.AddSeconds(450), blocked.RetryAt);
        clock.Now = clock.Now.AddSeconds(450);
        var retry = session.ClaimSource("retry", "profile", [Identity(2)]);
        Assert.NotNull(retry.ClaimId);
        Finish(session, retry, Ok());
        Assert.All(session.SourceStatus().Reads, read => Assert.Equal("completed", read.State));
    }

    [Fact]
    public void Interrupted_workers_release_slots_explicitly_without_resetting_attempts_or_accepting_late_results()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var coordinator = ExportSession.Open(path);
        var interrupted = coordinator.ClaimSource("interrupted-worker", "profile", [Identity(1)]);
        var success = coordinator.ClaimSource("other-worker", "profile", [Identity(2)]);
        Finish(coordinator, success, Ok());
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginSourceRecovery());
        coordinator.InterruptSource(interrupted.ClaimId!.Value, "Coordinator confirmed the worker stopped; its result is unknown.");
        Assert.Throws<InvalidOperationException>(() => Finish(coordinator, interrupted, Ok()));
        coordinator.BeginSourceRecovery();
        var retry = coordinator.ClaimSource("replacement", "profile", [Identity(1), Identity(2)]);
        Assert.Equal([Identity(1)], retry.Targets);
        coordinator.InterruptSource(retry.ClaimId!.Value, "Replacement also stopped.");
        Assert.Null(coordinator.ClaimSource("third-worker", "profile", [Identity(1)]).ClaimId);
        var read = coordinator.SourceStatus().Reads.Single(read => read.Target == Identity(1));
        Assert.Equal("failed", read.State);
        Assert.Equal(2, read.Attempts.Count);
        Assert.Contains("Replacement also stopped", read.Attempts[1].Error);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginSourceRecovery());
        Assert.Throws<InvalidOperationException>(() => coordinator.Publish());
    }

    [Theory]
    [InlineData("retryAfterMilliseconds", 1500, 1.5)]
    [InlineData("x-ms-retry-after-ms", 2500, 2.5)]
    [InlineData("retryAfterSeconds", 3, 3)]
    public void Returned_numeric_retry_delays_gate_the_next_source_call(string name, int value, double seconds)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        var first = session.ClaimSource("first", "profile", [Identity(1)]);
        Finish(session, first, new JsonObject
        {
            ["statusCode"] = 429,
            ["error"] = new JsonObject { ["code"] = "TooManyRequests", [name] = value }
        });
        session.BeginSourceRecovery();
        clock.Now = clock.Now.AddSeconds(seconds).AddTicks(-1);
        Assert.Null(session.ClaimSource("replacement", "profile", [Identity(1)]).ClaimId);
        clock.Now = clock.Now.AddTicks(1);
        Assert.NotNull(session.ClaimSource("replacement", "profile", [Identity(1)]).ClaimId);
        Assert.Null(session.ClaimSource("next-batch", "profile", [Identity(2)]).ClaimId);
    }

    [Fact]
    public void Structured_batches_keep_fifty_paths_and_only_confirmed_metadata_absence_is_complete()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var targets = new[] { ExportSession.ManagerPath(RootId), ExportSession.PhotoPath(RootId) }
            .Concat(Enumerable.Range(1, 48).Select(index => ExportSession.UserPath(Identity(index)))).ToArray();
        var claim = session.ClaimSource("coordinator", "structured", targets);
        Assert.NotNull(claim.ClaimId);
        var responses = Enumerable.Range(0, 50).Select(_ => Ok()).ToArray();
        responses[0] = new JsonObject
        {
            ["statusCode"] = 404, ["error"] = new JsonObject { ["code"] = "Request_ResourceNotFound" }
        };
        responses[1] = new JsonObject
        {
            ["statusCode"] = 404, ["error"] = new JsonObject { ["code"] = "ImageNotFound" }
        };
        Finish(session, claim, responses);
        Assert.All(session.SourceStatus().Reads, read => Assert.Equal("completed", read.State));
        Assert.Throws<InvalidOperationException>(() => session.ClaimSource("coordinator", "structured",
            targets.Append(ExportSession.UserPath(Identity(49))).ToArray()));
        var binary = session.ClaimSource("photo-worker", "photo", [$"/users/{RootId}/photo/$value"]);
        Finish(session, binary, responses[1]);
        Assert.Equal("failed", session.SourceStatus().Reads.Single(read => read.Kind == "photo").State);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
    }

    [Theory]
    [InlineData(429, "TooManyRequests")]
    [InlineData(403, "Forbidden")]
    public void Nested_failure_diagnostics_retain_the_source_status_and_message_instead_of_the_successful_envelope(
        int status, string code)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var claim = session.ClaimSource("profile-worker", "profile", [Identity(1)]);
        const string message = "Fictitious source could not complete this read.";
        Finish(session, claim, new JsonObject
        {
            ["statusCode"] = 200, ["data"] = new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["statusCode"] = status,
                    ["error"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }
                })
            }
        });

        var ledger = session.SourceStatus();
        var error = Assert.Single(Assert.Single(ledger.Reads).Attempts).Error;
        Assert.Contains($"HTTP {status}", error);
        Assert.Contains(message, error);
        Assert.Contains(code, error);
        Assert.DoesNotContain("HTTP 200", error);
        if (status == 403) Assert.Contains(message, ledger.StopReason);
        var blocked = Assert.Throws<InvalidOperationException>(() => session.Publish());
        Assert.Contains($"HTTP {status}", blocked.Message);
        Assert.Contains(message, blocked.Message);
    }

    private void Finish(ExportSession session, ExportSession.SourceClaim claim, params JsonObject[] responses)
    {
        var file = Path.Combine(root, Guid.NewGuid() + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(claim.Targets.Select((target, index) =>
            new { target, response = responses[index] }), ExportSession.Json));
        session.FinishSource(claim.ClaimId!.Value, file);
    }

    private static string Identity(int index) => $"30000000-0000-0000-0000-{index:D12}";
    private static JsonObject Ok() => new() { ["statusCode"] = 200 };

    private sealed class SourceClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
