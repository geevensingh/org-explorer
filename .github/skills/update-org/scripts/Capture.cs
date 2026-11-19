using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    public sealed record CaptureRequest(string ToolName, JsonObject Arguments,
        string? AgentId = null, string? ParentToolCallId = null, Guid? ClaimId = null);
    private sealed record CaptureTicket(Guid BatchId, string EventsFile, long Offset, string PrefixSha256,
        CaptureRequest Request);
    public sealed record CapturedResult(string ToolCallId, bool ToolSucceeded, int ResultCount, int? NonSuccessResults,
        string ResponseFile, string? RequestsFile, string StartFile, string CompletionFile, string[] SavedFiles);

    public string BeginCapture(string eventsFile, string requestFile)
    {
        eventsFile = System.IO.Path.GetFullPath(eventsFile);
        Local(eventsFile);
        var request = Read<CaptureRequest>(requestFile);
        Require(!string.IsNullOrWhiteSpace(request.ToolName), "Use the exact configured WorkIQ tool name.");
        Require(request.AgentId is null || request.ParentToolCallId is null, "Choose one native worker scope.");
        Require(request.ClaimId is not null || SourceStatus().BestEffortEnrichment is null,
            "Best-effort collection requires claimId in every capture descriptor.");
        using var events = OpenEvents(eventsFile);
        var offset = events.Length;
        if (offset > 0)
        {
            events.Position = offset - 1;
            Require(events.ReadByte() == '\n', "The session event stream has an incomplete last event; wait before capture-start.");
        }
        var ticket = new CaptureTicket(run.BatchId, eventsFile, offset, PrefixHash(events, offset), request);
        var directory = Within("captures\\" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var ticketFile = System.IO.Path.Combine(directory, "ticket.json");
        Save(ticketFile, ticket);
        File.WriteAllBytes(System.IO.Path.Combine(directory, ".lock"), []);
        if (request.ClaimId is { } claimId)
            WithSources(ledger =>
            {
                var active = ledger.Reads.Where(read => read.State == "active" && read.Attempts[^1].ClaimId == claimId).ToArray();
                Require(active.Length > 0 && active.All(read => read.Attempts[^1].CaptureTicket is null),
                    "Bind one capture ticket to an active source claim.");
                if (active[0].Kind == "structured")
                    Require(request.Arguments["entityUrls"] is JsonArray urls
                        && urls.Select(url => url!.GetValue<string>()).SequenceEqual(active.Select(read => read.Target)),
                        "Capture requests must match the granted structured targets in order.");
                else if (active[0].Kind == "photo")
                    Require(request.Arguments["path"]?.GetValue<string>() == active.Single().Target,
                        "Capture path must match the granted photo target.");
                var relative = System.IO.Path.GetRelativePath(path, ticketFile);
                var hash = Hash(File.ReadAllBytes(ticketFile));
                foreach (var read in active)
                    read.Attempts[^1] = read.Attempts[^1] with { CaptureTicket = relative, CaptureTicketSha256 = hash };
                return claimId;
            });
        return ticketFile;
    }

    public CapturedResult Capture(string ticketFile)
    {
        ticketFile = System.IO.Path.GetFullPath(ticketFile);
        Require(ticketFile.StartsWith(path + "\\captures\\", StringComparison.OrdinalIgnoreCase),
            "Use this run's capture ticket.");
        var ticket = Read<CaptureTicket>(ticketFile);
        Require(ticket.BatchId == run.BatchId, "Capture ticket belongs to another run.");
        if (ticket.Request.ClaimId is not null) RequireCaptureBinding(ticketFile, ticket, active: false);
        var directory = System.IO.Path.GetDirectoryName(ticketFile)!;
        using var captureLock = new FileStream(System.IO.Path.Combine(directory, ".lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using var events = OpenEvents(ticket.EventsFile);
        Require(events.Length >= ticket.Offset && PrefixHash(events, ticket.Offset) == ticket.PrefixSha256,
            "The original session event stream changed; capture cannot establish request identity.");
        events.Position = ticket.Offset;
        var starts = new List<(string Id, string Line, long Position)>();
        var completions = new Dictionary<string, List<(string Line, long Position)>>(StringComparer.Ordinal);
        long position = 0;
        foreach (var line in CompleteEventLines(events))
        {
            position++;
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            if (!entry.TryGetProperty("type", out var type)
                || !entry.TryGetProperty("data", out var data)) continue;
            if (!CaptureScope(entry, data, ticket.Request)) continue;
            if (type.GetString() == "tool.execution_start"
                && data.GetProperty("toolName").GetString() == ticket.Request.ToolName
                && JsonNode.DeepEquals(JsonNode.Parse(data.GetProperty("arguments").GetRawText()), ticket.Request.Arguments))
            {
                Require(entry.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetDateTimeOffset(out var observed)
                    && observed >= run.StartedAt && observed <= DateTimeOffset.UtcNow.AddMinutes(5),
                    "The matching request is not a fresh event in this run.");
                starts.Add((data.GetProperty("toolCallId").GetString()!, line, position));
            }
            if (type.GetString() == "tool.execution_complete")
            {
                var id = data.GetProperty("toolCallId").GetString()!;
                if (!completions.TryGetValue(id, out var values)) completions[id] = values = [];
                values.Add((line, position));
            }
        }
        Require(starts.Count == 1, "Expected exactly one matching tool request after capture-start; missing or ambiguous invocation.");
        var start = starts[0];
        Require(completions.TryGetValue(start.Id, out var matches) && matches.Count == 1,
            "The matching tool result is missing or duplicated; capture is incomplete.");
        Require(matches![0].Position > start.Position, "Tool completion precedes its request.");
        var completion = matches[0].Line;
        using var completed = JsonDocument.Parse(completion);
        var outcome = completed.RootElement.GetProperty("data");
        var startFile = System.IO.Path.Combine(directory, "start.json");
        var completionFile = System.IO.Path.Combine(directory, "completion.json");
        Immutable(startFile, Encoding.UTF8.GetBytes(start.Line));
        Immutable(completionFile, Encoding.UTF8.GetBytes(completion));
        var response = OriginalPayload(outcome);
        using var payload = JsonDocument.Parse(response);
        string[]? requests = null;
        var count = 1;
        int? nonSuccessResults = null;
        if (ticket.Request.Arguments["entityUrls"] is JsonArray urls)
        {
            requests = urls.Select(url => url!.GetValue<string>()).ToArray();
            Require(payload.RootElement.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array && requests.Length is > 0 and <= 50
                && results.GetArrayLength() == requests.Length,
                "The full structured result must contain one ordered result per request.");
            nonSuccessResults = 0;
            foreach (var item in results.EnumerateArray())
            {
                Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("statusCode", out var status)
                    && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code is >= 100 and <= 599,
                    "A structured result is missing its original source status.");
                if (item.GetProperty("statusCode").GetInt32() != 200
                    || (item.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
                    nonSuccessResults++;
            }
            count = requests.Length;
        }
        else if (payload.RootElement.ValueKind == JsonValueKind.Object
            && payload.RootElement.TryGetProperty("statusCode", out var status) && status.TryGetInt32(out var code))
            nonSuccessResults = code == 200
                && (!payload.RootElement.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null) ? 0 : 1;
        var captured = new CapturedResult(start.Id, outcome.GetProperty("success").GetBoolean(), count, nonSuccessResults,
            System.IO.Path.Combine(directory, "response.json"),
            requests is null ? null : System.IO.Path.Combine(directory, "requests.json"),
            startFile, completionFile, []);
        var claim = Within("captures\\" + Hash(Encoding.UTF8.GetBytes(ticket.EventsFile.ToUpperInvariant() + "\n" + start.Id)) + ".claim");
        Immutable(claim, Encoding.UTF8.GetBytes(ticketFile));
        Immutable(captured.ResponseFile, Encoding.UTF8.GetBytes(response));
        if (captured.RequestsFile is not null)
            Immutable(captured.RequestsFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(requests, Json)));
        Save(System.IO.Path.Combine(directory, "capture.json"), captured);
        return captured;
    }

    public SourceCompletion FailCapture(Guid claimId, string ticketFile, string reason)
    {
        Require(!string.IsNullOrWhiteSpace(reason), "Record why the original invocation is known to have stopped.");
        ticketFile = System.IO.Path.GetFullPath(ticketFile);
        Require(ticketFile.StartsWith(path + "\\captures\\", StringComparison.OrdinalIgnoreCase),
            "Use this run's capture ticket.");
        var ticket = Read<CaptureTicket>(ticketFile);
        Require(ticket.BatchId == run.BatchId && ticket.Request.ClaimId == claimId,
            "Capture failure must identify its original source claim.");
        RequireCaptureBinding(ticketFile, ticket, active: true);
        string diagnostic;
        try { Capture(ticketFile); }
        catch (Exception error) when (error is InvalidOperationException or JsonException or IOException)
        {
            diagnostic = error.Message;
            return WithSources(ledger =>
            {
                var active = ledger.Reads.Where(read => read.State == "active" && read.Attempts[^1].ClaimId == claimId).ToArray();
                Require(active.Length > 0, "The original source claim has already settled.");
                var directory = System.IO.Path.GetDirectoryName(ticketFile)!;
                var completionFile = System.IO.Path.Combine(directory, "completion.json");
                Require(File.Exists(completionFile),
                    "The matching completion diagnostics cannot be inspected safely; keep the claim active and recapture before authorizing a replacement.");
                var outcome = Read<JsonObject>(completionFile)["data"] as JsonObject
                    ?? throw new InvalidOperationException("The correlated capture completion has no diagnostic data.");
                var classified = CaptureFailureOutcome(active[0].Kind, active[0].Target, outcome);
                if (classified.Denied) ledger.StopReason = "Source authorization/access/policy denial in capture diagnostics stops this invocation.";
                else if (classified.InvalidDelay)
                    ledger.StopReason = "The returned source retry delay in capture diagnostics cannot be honored.";
                var failureFile = System.IO.Path.GetRelativePath(path, System.IO.Path.Combine(directory, "failure.json"));
                Immutable(Within(failureFile), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    claimId, ticket = System.IO.Path.GetRelativePath(path, ticketFile), reason, diagnostic,
                    payloadDiagnostics = classified.Diagnostics,
                    recordedAt = sourceClock.GetUtcNow(), denied = classified.Denied, retryAt = classified.RetryAt,
                    completion = outcome
                }, Json)));
                foreach (var read in active)
                {
                    read.Attempts[^1] = read.Attempts[^1] with
                    {
                        FinishedAt = sourceClock.GetUtcNow(), Error = "Unverifiable capture: " + diagnostic,
                        Retryable = !classified.Denied && !classified.InvalidDelay, RetryAt = classified.RetryAt,
                        CaptureFailureFile = failureFile
                    };
                    ledger.Reads[ledger.Reads.IndexOf(read)] = SettleBestEffortRead(ledger,
                        read with { State = "failed", RecoveryEligible = false }, retryable: !classified.Denied && !classified.InvalidDelay);
                }
                return new SourceCompletion(ledger.BatchId, claimId,
                    ledger.Reads.Where(read => read.Attempts.LastOrDefault()?.ClaimId == claimId).ToArray(),
                    ledger.RecoveryConsumed, ledger.StopReason);
            });
        }
        throw new InvalidOperationException("The complete original response is available; finish that capture instead of repeating the source read.");
    }

    private (bool Denied, DateTimeOffset? RetryAt, bool InvalidDelay, string[] Diagnostics) CaptureFailureOutcome(
        string kind, string target, JsonObject? completion)
    {
        var denied = false;
        var invalidDelay = false;
        DateTimeOffset? retryAt = null;
        var diagnostics = new List<string>();
        void Inspect(JsonNode? node, bool errorContext, int depth)
        {
            if (node is null || depth >= 12) return;
            if (node is JsonArray array)
            {
                foreach (var child in array) Inspect(child, errorContext, depth + 1);
                return;
            }
            if (node is not JsonObject obj) return;
            var classified = SourceOutcome(kind, target, obj);
            denied |= classified.Denied;
            invalidDelay |= classified.InvalidDelay;
            if (classified.RetryAt is { } deadline && (retryAt is null || deadline > retryAt)) retryAt = deadline;
            errorContext |= obj["error"] is not null || obj["isError"] is JsonValue errorFlag
                && errorFlag.TryGetValue<bool>(out var isError) && isError
                || obj["success"] is JsonValue successFlag && successFlag.TryGetValue<bool>(out var success) && !success;
            foreach (var (name, value) in obj)
            {
                if (value is not JsonValue json || !json.TryGetValue<string>(out var text))
                {
                    Inspect(value, errorContext, depth + 1);
                    continue;
                }
                if (name is not ("content" or "detailedContent" or "text" or "message" or "error")) continue;
                if (errorContext || name is "message" or "error")
                {
                    denied |= SourceDenialText(text);
                    try { text = StripNativeErrorPrefix(text); }
                    catch (InvalidOperationException error)
                    {
                        diagnostics.Add(error.Message);
                        continue;
                    }
                }
                if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                {
                    try { Inspect(JsonNode.Parse(text), errorContext, depth + 1); }
                    catch (JsonException error) { diagnostics.Add(error.Message); }
                }
            }
        }
        Inspect(completion, false, 0);
        return (denied, retryAt, invalidDelay, diagnostics.ToArray());
    }

    private void RequireCaptureBinding(string ticketFile, CaptureTicket ticket, bool active)
        => WithSources(ledger =>
        {
            var read = ledger.Reads.FirstOrDefault(read => SourceAttempts(read).Any(attempt => attempt.ClaimId == ticket.Request.ClaimId));
            var attempt = read is null ? null : SourceAttempts(read).Single(item => item.ClaimId == ticket.Request.ClaimId);
            Require(attempt is not null && attempt.CaptureTicket == System.IO.Path.GetRelativePath(path, ticketFile)
                && attempt.CaptureTicketSha256 == Hash(File.ReadAllBytes(ticketFile))
                && attempt.CaptureFailureFile is null && (!active || read!.State == "active"
                    && read.Attempts[^1].ClaimId == ticket.Request.ClaimId),
                "The original capture ticket must match its source claim and remain unchanged.");
            return true;
        });

    private static bool CaptureScope(JsonElement entry, JsonElement data, CaptureRequest request)
    {
        var agent = OptionalString(entry, "agentId");
        var parent = OptionalString(data, "parentToolCallId");
        return request.AgentId is not null ? agent == request.AgentId
            : request.ParentToolCallId is not null ? parent == request.ParentToolCallId
            : agent is null && parent is null;
    }

    private static string? OptionalString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string OriginalPayload(JsonElement outcome)
    {
        string text;
        if (outcome.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
                text = structured.GetRawText();
            else if (result.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                text = ContentText(contents);
            else
                text = OptionalString(result, "detailedContent") ?? OptionalString(result, "content")
                    ?? throw new InvalidOperationException("The original tool result has no supported content.");
        }
        else
            text = outcome.TryGetProperty("error", out var error) ? OptionalString(error, "message") ?? "" : "";
        var failed = !outcome.GetProperty("success").GetBoolean();
        if (failed) text = StripNativeErrorPrefix(text);
        try { return UnwrapPayload(text); }
        catch (InvalidOperationException failure) when (failed
            && failure.Message.StartsWith("Unverified host-saved output reference:", StringComparison.Ordinal)
            && outcome.TryGetProperty("error", out var nativeError)
            && OptionalString(nativeError, "message") is { Length: > 0 })
        {
            return UnwrapPayload(StripNativeErrorPrefix(nativeError.GetProperty("message").GetString()!));
        }
    }

    private static string StripNativeErrorPrefix(string text)
    {
        if (text.StartsWith("MCP error ", StringComparison.Ordinal))
        {
            var separator = text.IndexOf(": ", StringComparison.Ordinal);
            Require(separator > 10 && int.TryParse(text[10..separator], out _), "Unsupported MCP error wrapper.");
            return text[(separator + 2)..];
        }
        if (text.StartsWith("MCP server '", StringComparison.Ordinal))
        {
            var separator = text.IndexOf("': ", 12, StringComparison.Ordinal);
            Require(separator > 12 && text.AsSpan(12, separator - 12).IndexOfAny('\r', '\n', '\'') < 0,
                "Unsupported MCP server error wrapper.");
            return text[(separator + 3)..];
        }
        return text;
    }

    private static string UnwrapPayload(string text, int depth = 0)
    {
        Require(depth < 8, "Unsupported deeply wrapped tool result.");
        const string savedPrefix = "Output too large to display.\nFull output saved to: ";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Require(!normalized.StartsWith(savedPrefix, StringComparison.Ordinal)
            && !(normalized.StartsWith("Output too large to read at once (", StringComparison.Ordinal)
                && normalized.Contains("). Saved to: ", StringComparison.Ordinal)),
            "Unverified host-saved output reference: capture requires a supported immutable per-invocation file contract or source-provided digest.");
        using var payload = JsonDocument.Parse(text);
        var body = payload.RootElement;
        Require(body.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
            "Unsupported or incomplete WorkIQ payload.");
        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Null)
                return UnwrapPayload(structured.GetRawText(), depth + 1);
            if (body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                return UnwrapPayload(ContentText(content), depth + 1);
        }
        return text;
    }

    private static string ContentText(JsonElement content)
    {
        Require(content.GetArrayLength() == 1, "Unsupported multipart result; a single complete JSON payload is required.");
        var block = content[0];
        Require(block.ValueKind == JsonValueKind.Object && OptionalString(block, "type") == "text"
            && OptionalString(block, "text") is not null, "Unsupported tool content block; use complete original JSON.");
        return block.GetProperty("text").GetString()!;
    }

    private static FileStream OpenEvents(string file)
    {
        Local(file);
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private static IEnumerable<string> CompleteEventLines(FileStream events)
    {
        var remaining = events.Length - events.Position;
        var buffer = new byte[81920];
        var encoding = new UTF8Encoding(false, true);
        var first = true;
        using var line = new MemoryStream();
        while (remaining > 0)
        {
            var count = events.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Require(count > 0, "Session events were truncated during capture.");
            remaining -= count;
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n') continue;
                line.Write(buffer, start, index - start);
                var bytes = line.GetBuffer();
                var length = checked((int)line.Length);
                if (length > 0 && bytes[length - 1] == '\r') length--;
                var preamble = first && length >= 3 && bytes[0] == 0xEF
                    && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                first = false;
                yield return encoding.GetString(bytes, preamble, length - preamble);
                line.SetLength(0);
                line.Position = 0;
                start = index + 1;
            }
            line.Write(buffer, start, count - start);
        }
    }

    private static string PrefixHash(FileStream stream, long length)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (length > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            Require(count > 0, "Session events were truncated during capture.");
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
