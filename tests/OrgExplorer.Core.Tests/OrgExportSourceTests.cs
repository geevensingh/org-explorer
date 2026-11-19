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

    [Theory]
    [InlineData("Open '' Failure: Unable to open, Exception Caught.")]
    [InlineData("Open '' Failure: Unable to open, Exception: Status code is .")]
    [InlineData("Profile details couldn't be retrieved for this person right now.")]
    public void Best_effort_recognizes_exact_profile_snippets_without_relaxing_status_or_denial_checks(string diagnostic)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var response = new JsonObject { ["error"] = "- **Fictitious Person**\r\n  - **snippet:** " + diagnostic };
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]), response);
        Assert.True(session.SourceStatus().Reads.Single().RecoveryEligible);
        Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
        var denied = new JsonObject
        {
            ["statusCode"] = 403,
            ["error"] = "- **snippet:** " + diagnostic
        };
        Finish(session, session.ClaimSource("denied", "profile", [Identity(2)]), denied);
        Assert.NotNull(session.SourceStatus().StopReason);
        Assert.Null(session.ClaimSource("unrelated", "profile", [Identity(3)]).ClaimId);
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

    [Fact]
    public void Authorized_per_read_recovery_keeps_delays_successes_and_the_two_attempt_limit()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        var authorization = session.AuthorizePerReadSourceRecovery("Explicit user approval for this fictitious run.");
        var first = session.ClaimSource("first", "profile", [Identity(1)]);
        var unavailable = new JsonObject
        {
            ["statusCode"] = 503, ["retryAfterSeconds"] = 5,
            ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" }
        };
        Finish(session, first, unavailable);
        session.BeginSourceRecovery();
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        var waiting = session.ClaimSource("retry", "profile", [Identity(1)]);
        Assert.Null(waiting.ClaimId);
        Assert.Equal(clock.Now.AddSeconds(5), waiting.RetryAt);
        clock.Now = clock.Now.AddSeconds(5);
        Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
        var later = session.ClaimSource("later", "profile", [Identity(2), Identity(3)]);
        Finish(session, later, Ok(), unavailable);
        session.BeginSourceRecovery();
        Assert.Null(session.ClaimSource("queued", "profile", [Identity(4)]).ClaimId);
        clock.Now = clock.Now.AddSeconds(5);
        var retry = session.ClaimSource("later-retry", "profile", [Identity(2), Identity(3)]);
        Assert.Equal([Identity(3)], retry.Targets);
        Finish(session, retry, unavailable);
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        Assert.Null(session.ClaimSource("third-attempt", "profile", [Identity(3)]).ClaimId);
        var ledger = session.SourceStatus();
        Assert.Equal(authorization, ledger.PerReadRecovery);
        Assert.Equal(2, ledger.Reads.Single(read => read.Target == Identity(1)).Attempts.Count);
        Assert.Single(ledger.Reads.Single(read => read.Target == Identity(2)).Attempts);
        Assert.Equal(2, ledger.Reads.Single(read => read.Target == Identity(3)).Attempts.Count);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Fact]
    public void Per_read_recovery_requires_a_reason_and_settled_claims_and_cannot_replace_its_authorization()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePerReadSourceRecovery(" "));
        var active = session.ClaimSource("worker", "profile", [Identity(1)]);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePerReadSourceRecovery("Explicit approval."));
        Finish(session, active, Ok());
        var authorization = session.AuthorizePerReadSourceRecovery("Explicit approval.");
        Assert.Throws<InvalidOperationException>(() => session.AuthorizePerReadSourceRecovery("Replacement approval."));
        Assert.Equal(authorization, session.SourceStatus().PerReadRecovery);
    }

    [Fact]
    public void Approved_unclassified_read_keeps_its_original_delay_and_failure_after_the_global_pass()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" }
        });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
        var unclassified = new JsonObject
        {
            ["statusCode"] = 200, ["retryAfterSeconds"] = 7,
            ["error"] = new JsonObject { ["message"] = "Fictitious indexed evidence could not be opened." }
        };
        Finish(session, session.ClaimSource("later", "profile", [Identity(2), Identity(3)]), unclassified, Ok());
        var original = session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts.Single();
        Assert.False(original.Retryable);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        clock.Now = clock.Now.AddSeconds(2);
        var approval = session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(2), "User approved only this read.");
        var waiting = session.ClaimSource("later-retry", "profile", [Identity(2), Identity(3)]);
        Assert.Null(waiting.ClaimId);
        Assert.Equal(original.RetryAt, waiting.RetryAt);
        clock.Now = clock.Now.AddSeconds(5);
        var retry = session.ClaimSource("later-retry", "profile", [Identity(2), Identity(3)]);
        Assert.Equal([Identity(2)], retry.Targets);
        Finish(session, retry, unclassified);
        clock.Now = clock.Now.AddSeconds(7);
        Assert.Null(session.ClaimSource("third-attempt", "profile", [Identity(2)]).ClaimId);
        Assert.Throws<InvalidOperationException>(() =>
            session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(2), "Cannot approve a third attempt."));
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        var failed = session.SourceStatus().Reads.Single(read => read.Target == Identity(2));
        Assert.Equal(approval, failed.UnclassifiedRecovery);
        Assert.Equal(original, failed.Attempts[0]);
        Assert.Equal(2, failed.Attempts.Count);
        Assert.Single(session.SourceStatus().Reads.Single(read => read.Target == Identity(3)).Attempts);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Fact]
    public void Unclassified_recovery_requires_a_reason_settled_claims_and_one_immutable_approval()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var failed = session.ClaimSource("failed", "profile", [Identity(1)]);
        var active = session.ClaimSource("active", "profile", [Identity(2)]);
        Finish(session, failed, new JsonObject { ["error"] = new JsonObject { ["message"] = "Fictitious failure." } });
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(1), " "));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(1), "Approval."));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(1), "Approval."));
        Finish(session, active, Ok());
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("photo", Identity(1), "Wrong kind."));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(3), "Unrecorded."));
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(2), "Already succeeded."));
        var approval = session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(1), "Explicit approval.");
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(1), "Replacement."));
        Assert.Equal(approval, session.SourceStatus().Reads.Single(read => read.Target == Identity(1)).UnclassifiedRecovery);
    }

    [Fact]
    public void Matching_error_approval_recovers_later_occurrences_without_renewing_the_two_attempt_limit()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" }
        });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
        var failure = new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "Fictitious indexed evidence could not be opened." }
        };
        Finish(session, session.ClaimSource("later", "profile", [Identity(2)]), failure);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(2), " "));
        var approval = session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(2), "Explicit approval for this exact error.");
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(2), "Replacement."));
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("later-retry", "profile", [Identity(2)]), Ok());
        Finish(session, session.ClaimSource("recurrence", "profile", [Identity(3)]), failure);
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("recurrence-retry", "profile", [Identity(3)]), failure);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        Assert.Null(session.ClaimSource("third-attempt", "profile", [Identity(3)]).ClaimId);
        var ledger = session.SourceStatus();
        Assert.Equal(approval, ledger.MatchingErrorRecovery);
        Assert.Null(ledger.PerReadRecovery);
        Assert.All(ledger.Reads, read => Assert.Equal(2, read.Attempts.Count));
        Assert.Equal("failed", ledger.Reads.Single(read => read.Target == Identity(3)).State);
    }

    [Fact]
    public void Matching_error_approval_excludes_other_kinds_errors_classified_failures_and_later_transients()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var transient = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" } };
        Finish(session, session.ClaimSource("first", "profile", [Identity(9)]), transient);
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(9)]), Ok());
        var profiles = session.ClaimSource("profiles", "profile", [Identity(1), Identity(2), Identity(3), Identity(4)]);
        var photo = session.ClaimSource("photo", "photo", [$"/users/{RootId}/photo/$value"]);
        var failure = new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "Fictitious indexed evidence could not be opened." }
        };
        var classified = (JsonObject)failure.DeepClone();
        classified["code"] = "KnownFailure";
        Finish(session, profiles, failure, new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "A different fictitious error." }
        }, classified, transient);
        Finish(session, photo, failure);
        var before = session.SourceStatus();
        Assert.Equal(before.Reads.Single(read => read.Target == Identity(1)).Attempts.Single().Error,
            before.Reads.Single(read => read.Target == Identity(3)).Attempts.Single().Error);
        session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(1), "Explicit approval for this exact profile error.");
        session.BeginSourceRecovery();
        Assert.Equal(Identity(1), Assert.Single(session.SourceStatus().Reads, read => read.RecoveryEligible
            && read.State == "failed").Target);
    }

    [Fact]
    public void Matching_error_recovery_accepts_the_same_message_in_string_and_object_wrappers()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        const string message = "Fictitious indexed evidence could not be opened.";
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = message }
        });
        var approval = session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(1), "Approval for this exact error.");
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
        var matching = session.ClaimSource("matching", "profile", [Identity(2)]);
        var different = session.ClaimSource("different", "profile", [Identity(3)]);
        Finish(session, matching, new JsonObject { ["statusCode"] = 200, ["error"] = message });
        Finish(session, different, new JsonObject { ["statusCode"] = 200, ["error"] = message + " Different failure." });
        var original = session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts.Single();

        session.BeginSourceRecovery();

        var ledger = session.SourceStatus();
        Assert.True(ledger.Reads.Single(read => read.Target == Identity(2)).RecoveryEligible);
        Assert.False(ledger.Reads.Single(read => read.Target == Identity(3)).RecoveryEligible);
        Assert.Equal(original, ledger.Reads.Single(read => read.Target == Identity(2)).Attempts.Single());
        Assert.Equal(approval, ledger.MatchingErrorRecovery);
        Finish(session, session.ClaimSource("matching-retry", "profile", [Identity(2)]), Ok());
        Assert.Null(session.ClaimSource("unapproved-retry", "profile", [Identity(3)]).ClaimId);
    }

    [Fact]
    public void A_second_explicit_error_approval_preserves_the_first_without_authorizing_other_errors()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = "First fictitious indexed evidence failure."
        });
        var first = session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(1), "Explicit first-error approval.");
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("first-retry", "profile", [Identity(1)]), Ok());
        var second = session.ClaimSource("second", "profile", [Identity(2)]);
        var unapproved = session.ClaimSource("unapproved", "profile", [Identity(3)]);
        Finish(session, second, new JsonObject
        {
            ["statusCode"] = 200, ["error"] = "Second fictitious indexed evidence failure."
        });
        Finish(session, unapproved, new JsonObject
        {
            ["statusCode"] = 200, ["error"] = "Unapproved fictitious indexed evidence failure."
        });
        var original = session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts.Single();

        var additional = session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(2), "Explicit second-error approval.");

        Assert.Throws<InvalidOperationException>(() => session.AuthorizeMatchingErrorSourceRecovery(
            "profile", Identity(2), "Cannot replace the second approval."));
        using var reopened = ExportSession.Open(path);
        reopened.BeginSourceRecovery();
        var ledger = reopened.SourceStatus();
        Assert.Equal(first, ledger.MatchingErrorRecovery);
        var approvals = JsonSerializer.SerializeToNode(ledger, ExportSession.Json)!["additionalMatchingErrorRecoveries"]!.AsArray();
        Assert.Equal(additional.Error, Assert.Single(approvals)!["error"]!.GetValue<string>());
        Assert.Equal(additional.Reason, approvals[0]!["reason"]!.GetValue<string>());
        Assert.Equal(original, ledger.Reads.Single(read => read.Target == Identity(2)).Attempts.Single());
        Assert.True(ledger.Reads.Single(read => read.Target == Identity(2)).RecoveryEligible);
        Assert.False(ledger.Reads.Single(read => read.Target == Identity(3)).RecoveryEligible);
        Finish(reopened, reopened.ClaimSource("second-retry", "profile", [Identity(2)]), Ok());
        Assert.Null(reopened.ClaimSource("unapproved-retry", "profile", [Identity(3)]).ClaimId);
    }

    [Theory]
    [InlineData("ServiceUnavailable", true, false, false)]
    [InlineData("ServiceUnavailable", true, false, true)]
    [InlineData("ServiceUnavailable; Status code is Forbidden", false, true, false)]
    [InlineData("ServiceUnavailable; Status code is Forbidden", false, true, true)]
    [InlineData("ServiceUnavailableButUnclassified", false, false, false)]
    [InlineData("ServiceUnavailableButUnclassified", false, false, true)]
    public void Explicit_named_statuses_in_source_error_text_preserve_transient_and_denial_rules(
        string statusText, bool retryable, bool denied, bool messageOnly)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var message = "Status code is " + statusText;
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = messageOnly
                ? new JsonObject { ["message"] = message } : JsonValue.Create(message)
        });
        var ledger = session.SourceStatus();
        Assert.Equal(retryable, ledger.Reads.Single().Attempts.Single().Retryable);
        Assert.Equal(denied, ledger.StopReason is not null);
        if (retryable)
        {
            session.BeginSourceRecovery();
            Assert.NotNull(session.ClaimSource("retry", "profile", [Identity(1)]).ClaimId);
        }
        else Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
    }

    [Fact]
    public void Best_effort_message_only_service_status_gets_its_permitted_retry_instead_of_unavailable()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "Status code is ServiceUnavailable" }
        });
        var read = session.SourceStatus().Reads.Single();
        Assert.True(read.RecoveryEligible);
        Assert.False(read.AccountedFor);
        Assert.True(read.Attempts.Single().Retryable);
        Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
        Assert.Equal("completed", session.SourceStatus().Reads.Single().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_classifies_retained_legacy_status_text_without_rewriting_attempts(bool denied)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizePerReadSourceRecovery("Explicit user approval for this fictitious run.");
        var message = "Status code is ServiceUnavailable" + (denied ? "; Status code is Forbidden" : "");
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = message
        });
        var legacy = session.SourceStatus();
        legacy.StopReason = null;
        var original = legacy.Reads.Single().Attempts[0] with
        {
            Retryable = false, Error = "Source error: " + JsonSerializer.Serialize(message)
        };
        legacy.Reads.Single().Attempts[0] = original;
        File.WriteAllText(Path.Combine(path, "source-coordination.json"), JsonSerializer.Serialize(legacy, ExportSession.Json));
        var evidence = File.ReadAllBytes(Path.Combine(path, original.ResultFile!));

        var recovered = session.BeginSourceRecovery();

        Assert.Equal(original, recovered.Reads.Single().Attempts[0]);
        Assert.Equal(evidence, File.ReadAllBytes(Path.Combine(path, original.ResultFile!)));
        Assert.Equal(denied, recovered.StopReason is not null);
        Assert.Equal(!denied, recovered.Reads.Single().RecoveryEligible);
        if (denied) Assert.Null(session.ClaimSource("blocked-retry", "profile", [Identity(1)]).ClaimId);
        else Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
    }

    [Fact]
    public void A_later_denial_still_stops_a_run_with_matching_error_approval()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = "Fictitious indexed evidence could not be opened." }
        });
        session.AuthorizeMatchingErrorSourceRecovery("profile", Identity(1), "Explicit approval for this exact error.");
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
        Finish(session, session.ClaimSource("later", "profile", [Identity(2)]), new JsonObject
        {
            ["statusCode"] = 200, ["error"] = new JsonObject { ["code"] = "PolicyDenied" }
        });
        Assert.NotNull(session.SourceStatus().StopReason);
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        Assert.Null(session.ClaimSource("retry-denial", "profile", [Identity(2)]).ClaimId);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Theory]
    [InlineData(400, "BadRequest", false)]
    [InlineData(404, "Request_ResourceNotFound", false)]
    [InlineData(200, "KnownFailure", false)]
    [InlineData(503, "ServiceUnavailable", false)]
    [InlineData(200, "", true)]
    public void Unclassified_recovery_cannot_override_recognized_failures_or_invalid_delays(int status, string code, bool invalidDelay)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var response = new JsonObject
        {
            ["statusCode"] = status, ["error"] = new JsonObject { ["message"] = "Fictitious failure." }
        };
        if (code.Length > 0) response["error"]!["code"] = code;
        if (invalidDelay) response["retryAfterSeconds"] = "invalid";
        Finish(session, session.ClaimSource("failed", "profile", [Identity(1)]), response);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeUnclassifiedSourceRecovery("profile", Identity(1), "Approval."));
        Assert.False(session.SourceStatus().Reads.Single().RecoveryEligible);
        Assert.Null(session.SourceStatus().Reads.Single().UnclassifiedRecovery);
    }

    [Fact]
    public void Approved_not_found_read_preserves_its_delay_and_failure_and_cannot_receive_a_third_attempt()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        Finish(session, session.ClaimSource("first", "profile", [Identity(1)]), new JsonObject
        {
            ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "ServiceUnavailable" }
        });
        session.BeginSourceRecovery();
        Finish(session, session.ClaimSource("retry", "profile", [Identity(1)]), Ok());
        var target = $"/users/{RootId}/photo/$value";
        var first = session.ClaimSource("photo", "photo", [target]);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeNotFoundSourceRecovery("photo", target, "Approval."));
        var failure = new JsonObject
        {
            ["statusCode"] = 404, ["retryAfterSeconds"] = 7,
            ["error"] = new JsonObject { ["code"] = "ErrorNonExistentStorage" }
        };
        Finish(session, first, failure);
        var original = session.SourceStatus().Reads.Single(read => read.Kind == "photo").Attempts.Single();
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeNotFoundSourceRecovery("photo", target, " "));
        var approval = session.AuthorizeNotFoundSourceRecovery("photo", target, "Explicit approval for this photo read.");
        var waiting = session.ClaimSource("photo-retry", "photo", [target]);
        Assert.Null(waiting.ClaimId);
        Assert.Equal(original.RetryAt, waiting.RetryAt);
        clock.Now = clock.Now.AddSeconds(5);
        Finish(session, session.ClaimSource("photo-retry", "photo", [target]), failure);
        clock.Now = clock.Now.AddSeconds(7);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeNotFoundSourceRecovery("photo", target, "Third attempt."));
        Assert.Null(session.ClaimSource("third-attempt", "photo", [target]).ClaimId);
        var read = session.SourceStatus().Reads.Single(read => read.Kind == "photo");
        Assert.Equal("failed", read.State);
        Assert.Equal(approval, read.NotFoundRecovery);
        Assert.Equal(original, read.Attempts[0]);
        Assert.Equal(2, read.Attempts.Count);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(503, false)]
    [InlineData(404, true)]
    public void Not_found_approval_rejects_other_statuses_and_invalid_delays(int status, bool invalidDelay)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var target = $"/users/{RootId}/photo/$value";
        var response = new JsonObject { ["statusCode"] = status, ["error"] = new JsonObject { ["code"] = "FictitiousFailure" } };
        if (invalidDelay) response["retryAfterSeconds"] = "invalid";
        Finish(session, session.ClaimSource("photo", "photo", [target]), response);
        Assert.Throws<InvalidOperationException>(() => session.AuthorizeNotFoundSourceRecovery("photo", target, "Approval."));
        Assert.Null(session.SourceStatus().Reads.Single().NotFoundRecovery);
        Assert.False(session.SourceStatus().Reads.Single().RecoveryEligible);
    }

    [Theory]
    [InlineData(401, "InvalidAuthenticationToken", false)]
    [InlineData(403, "Forbidden", false)]
    [InlineData(200, "Authorization_RequestDenied", false)]
    [InlineData(200, "PolicyDenied", false)]
    [InlineData(401, "InvalidAuthenticationToken", true)]
    [InlineData(403, "Forbidden", true)]
    [InlineData(200, "Authorization_RequestDenied", true)]
    [InlineData(200, "PolicyDenied", true)]
    public void Nested_denial_stops_active_and_queued_dispatch_even_when_wrapped_as_a_transient_error(
        int status, string code, bool perReadRecovery)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        Guid deniedClaim;
        using (var worker = ExportSession.Open(path))
        {
            if (perReadRecovery) worker.AuthorizePerReadSourceRecovery("Explicit user approval for this fictitious run.");
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
        Assert.Throws<InvalidOperationException>(() => coordinator.AuthorizePerReadSourceRecovery("Cannot override a denial."));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.AuthorizeUnclassifiedSourceRecovery("profile", Identity(2), "Cannot override a denial."));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.AuthorizeMatchingErrorSourceRecovery("profile", Identity(2), "Cannot override a denial."));
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.AuthorizeNotFoundSourceRecovery("profile", Identity(2), "Cannot override a denial."));
        Assert.Throws<InvalidOperationException>(() => coordinator.AuthorizePhotoOmission(RootId, "Cannot waive a denial."));
        Assert.Throws<InvalidOperationException>(() => coordinator.InterruptSource(deniedClaim, "Cannot undo a completed denial."));
        Assert.Contains("source", Assert.Throws<InvalidOperationException>(() => coordinator.Publish()).Message,
            StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Best_effort_stopped_worker_handoffs_each_get_one_retry_without_resetting_other_reads()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        for (var index = 1; index <= 2; index++)
        {
            var stopped = session.ClaimSource("stopped-" + index, "profile", [Identity(index)]);
            session.InterruptSource(stopped.ClaimId!.Value, "Coordinator confirmed the worker stopped.");
            session.BeginSourceRecovery();
            Finish(session, session.ClaimSource("replacement-" + index, "profile", [Identity(index)]), Ok());
        }
        Assert.All(session.SourceStatus().Reads, read =>
        {
            Assert.Equal("completed", read.State);
            Assert.Equal(2, read.Attempts.Count);
        });
        var exhausted = session.ClaimSource("stopped-third", "profile", [Identity(3)]);
        session.InterruptSource(exhausted.ClaimId!.Value, "Coordinator confirmed the next worker stopped.");
        session.BeginSourceRecovery();
        var retry = session.ClaimSource("replacement-third", "profile", [Identity(3)]);
        session.InterruptSource(retry.ClaimId!.Value, "The replacement also stopped.");
        Assert.Throws<InvalidOperationException>(() => session.BeginSourceRecovery());
        Assert.Null(session.ClaimSource("forbidden-third-attempt", "profile", [Identity(3)]).ClaimId);
        Assert.Equal(2, session.SourceStatus().Reads.Single(read => read.Target == Identity(3)).Attempts.Count);
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

    [Fact]
    public void Best_effort_recovery_is_per_target_and_preserves_successes_without_renewing_attempts()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        var approval = session.AuthorizeBestEffortEnrichment("Standing policy approved for this fictitious run.");
        var first = session.ClaimSource("first", "profile", [Identity(1), Identity(2)]);
        var other = session.ClaimSource("other", "profile", [Identity(3)]);
        var failure = new JsonObject { ["statusCode"] = 503, ["error"] = new JsonObject { ["code"] = "CircuitOpen" } };
        Finish(session, first, Ok(), failure);
        var original = session.SourceStatus().Reads.Single(read => read.Target == Identity(2)).Attempts.Single();

        var unrelated = session.ClaimSource("unrelated", "profile", [Identity(4)]);
        Assert.NotNull(unrelated.ClaimId);
        Finish(session, other, Ok());
        Finish(session, unrelated, Ok());
        var retry = session.ClaimSource("retry", "profile", [Identity(1), Identity(2)]);
        Assert.Equal([Identity(2)], retry.Targets);
        Finish(session, retry, failure);

        using var reopened = ExportSession.Open(path);
        var ledger = reopened.SourceStatus();
        Assert.Equal(approval, ledger.BestEffortEnrichment);
        var unavailable = ledger.Reads.Single(read => read.Target == Identity(2));
        Assert.Equal("unavailable", unavailable.State);
        Assert.True(unavailable.AccountedFor);
        Assert.NotNull(unavailable.Unavailable);
        Assert.Equal(original, unavailable.Attempts[0]);
        Assert.Equal(2, unavailable.Attempts.Count);
        Assert.All(ledger.Reads.Where(read => read.Target != Identity(2)), read => Assert.Single(read.Attempts));
        Assert.Null(reopened.ClaimSource("third-attempt", "profile", [Identity(2)]).ClaimId);
        Assert.Null(reopened.ClaimSource("replay", "profile", [Identity(1)]).ClaimId);
        Assert.NotNull(reopened.ClaimSource("next", "profile", [Identity(5)]).ClaimId);
    }

    [Fact]
    public void Best_effort_keeps_returned_delays_global_and_required_directory_failures_unresolved()
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        var clock = new SourceClock(DateTimeOffset.UtcNow);
        using var session = ExportSession.Open(path, sourceClock: clock);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var target = ExportSession.UserPath(RootId);
        var failure = new JsonObject
        {
            ["statusCode"] = 429, ["retryAfterSeconds"] = 7,
            ["error"] = new JsonObject { ["code"] = "TooManyRequests" }
        };
        Finish(session, session.ClaimSource("directory", "structured", [target]), failure);
        Assert.Null(session.ClaimSource("unrelated", "profile", [Identity(1)]).ClaimId);
        Assert.Null(session.ClaimSource("early-retry", "structured", [target]).ClaimId);
        clock.Now = clock.Now.AddSeconds(7);
        Finish(session, session.ClaimSource("retry", "structured", [target]), failure);
        clock.Now = clock.Now.AddSeconds(7);

        var read = session.SourceStatus().Reads.Single(read => read.Target == target);
        Assert.Equal("failed", read.State);
        Assert.False(read.AccountedFor);
        Assert.Null(read.Unavailable);
        Assert.Null(session.ClaimSource("third-attempt", "structured", [target]).ClaimId);
        Assert.NotNull(session.ClaimSource("unrelated", "profile", [Identity(1)]).ClaimId);
        Assert.Contains("Source collection is incomplete", Assert.Throws<InvalidOperationException>(() => session.Publish()).Message);
    }

    [Theory]
    [InlineData("Open '' Failure: Unable to open, Exception Caught.", true)]
    [InlineData("An unfamiliar fictitious profile failure.", false)]
    public void Best_effort_retries_known_errors_but_reports_nonretryable_enrichment_without_probing(string message, bool retry)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]),
            new JsonObject { ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = message } });
        var read = session.SourceStatus().Reads.Single();
        Assert.Equal(retry, read.RecoveryEligible);
        Assert.Equal(!retry, read.AccountedFor);
        Assert.Equal(retry ? "failed" : "unavailable", read.State);
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(path, read.Attempts.Single().ResultFile!)))!.AsArray().Single()!;
        Assert.Equal(message, original["response"]!["error"]!["message"]!.GetValue<string>());
        Assert.NotNull(session.ClaimSource("unrelated", "profile", [Identity(2)]).ClaimId);
    }

    [Theory]
    [InlineData(403, "Forbidden", false)]
    [InlineData(503, "CircuitOpen", true)]
    public void Best_effort_cannot_account_away_denials_or_invalid_returned_delays(int status, string code, bool invalidDelay)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        var failure = new JsonObject { ["statusCode"] = status, ["error"] = new JsonObject { ["code"] = code } };
        if (invalidDelay) failure["retryAfterSeconds"] = "not-a-delay";
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]), failure);

        var ledger = session.SourceStatus();
        Assert.NotNull(ledger.StopReason);
        Assert.False(ledger.Reads.Single().AccountedFor);
        Assert.Null(ledger.Reads.Single().Unavailable);
        Assert.Null(session.ClaimSource("unrelated", "profile", [Identity(2)]).ClaimId);
        Assert.Throws<InvalidOperationException>(() => session.Publish());
    }

    [Theory]
    [InlineData("HTTP 403")]
    [InlineData("Access denied")]
    [InlineData("Consent required")]
    public void Best_effort_treats_explicit_diagnostic_denials_as_global_stops_not_unknown_enrichment(string message)
    {
        var path = ExportSession.Create("Fictitious root", RootId, AccountId, root);
        using var session = ExportSession.Open(path);
        session.AuthorizeBestEffortEnrichment("Standing policy.");
        Finish(session, session.ClaimSource("profile", "profile", [Identity(1)]),
            new JsonObject { ["statusCode"] = 200, ["error"] = new JsonObject { ["message"] = message } });
        var ledger = session.SourceStatus();
        Assert.NotNull(ledger.StopReason);
        Assert.False(ledger.Reads.Single().AccountedFor);
        Assert.Null(ledger.Reads.Single().Unavailable);
        Assert.Null(session.ClaimSource("unrelated", "profile", [Identity(2)]).ClaimId);
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
