using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrgExplorer.OrgExport;

public sealed partial class ExportSession
{
    public sealed record CaptureRequest(string ToolName, JsonObject Arguments,
        string? AgentId = null, string? ParentToolCallId = null);
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
        return ticketFile;
    }

    public CapturedResult Capture(string ticketFile)
    {
        ticketFile = System.IO.Path.GetFullPath(ticketFile);
        Require(ticketFile.StartsWith(path + "\\captures\\", StringComparison.OrdinalIgnoreCase),
            "Use this run's capture ticket.");
        var ticket = Read<CaptureTicket>(ticketFile);
        Require(ticket.BatchId == run.BatchId, "Capture ticket belongs to another run.");
        var directory = System.IO.Path.GetDirectoryName(ticketFile)!;
        using var captureLock = new FileStream(System.IO.Path.Combine(directory, ".lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        using var events = OpenEvents(ticket.EventsFile);
        Require(events.Length >= ticket.Offset && PrefixHash(events, ticket.Offset) == ticket.PrefixSha256,
            "The original session event stream changed; capture cannot establish request identity.");
        events.Position = ticket.Offset;
        using var reader = new StreamReader(events, new UTF8Encoding(false, true));
        var starts = new List<(string Id, string Line, long Position)>();
        var completions = new Dictionary<string, List<(string Line, long Position)>>(StringComparer.Ordinal);
        long position = 0;
        while (reader.ReadLine() is { } line)
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
        {
            text = outcome.TryGetProperty("error", out var error) ? OptionalString(error, "message") ?? "" : "";
            if (text.StartsWith("MCP error ", StringComparison.Ordinal))
            {
                var separator = text.IndexOf(": ", StringComparison.Ordinal);
                Require(separator > 10 && int.TryParse(text[10..separator], out _), "Unsupported MCP error wrapper.");
                text = text[(separator + 2)..];
            }
        }
        return UnwrapPayload(text);
    }

    private static string UnwrapPayload(string text, int depth = 0)
    {
        Require(depth < 8, "Unsupported deeply wrapped tool result.");
        const string savedPrefix = "Output too large to display.\nFull output saved to: ";
        Require(!text.Replace("\r\n", "\n", StringComparison.Ordinal).StartsWith(savedPrefix, StringComparison.Ordinal),
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
