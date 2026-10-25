using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeStreamDecoder(ClaudeCodeTools tools, ClaudeCodeMcpServer mcp, ClaudeCodeLimits limits)
{
    private readonly SortedDictionary<int, Block> _blocks = [];
    private UsageDetails _usage = new();
    private string? _id;
    private string? _model;
    private string? _stop;
    public bool ToolBoundary { get; private set; }
    public bool FinalResult { get; private set; }
    public List<AIContent> Contents { get; } = [];
    public IReadOnlyList<string> CallIds => Contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToArray();

    public IEnumerable<ChatResponseUpdate> Read(JsonElement line)
    {
        switch (line.GetProperty("type").GetString())
        {
            case "rate_limit_event":
                limits.Observe(line.GetProperty("rate_limit_info"));
                break;
            case "stream_event":
                foreach (var update in ReadEvent(line.GetProperty("event"))) yield return update;
                break;
            case "assistant":
                var message = line.GetProperty("message");
                if (line.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    var detail = string.Concat(message.GetProperty("content").EnumerateArray()
                        .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
                    throw new InvalidOperationException(error.GetString() switch
                    {
                        "authentication_failed" => $"ClaudeCode.SubscriptionLoginRequired: {detail} {ClaudeCodeAccount.LoginHint}",
                        "rate_limit" => $"ClaudeCode.UsageLimitReached: {detail}",
                        var code => $"ClaudeCode.AssistantError: {code}: {detail}"
                    });
                }
                foreach (var block in message.GetProperty("content").EnumerateArray())
                {
                    if (block.GetProperty("type").GetString() == "tool_use")
                    {
                        var id = block.GetProperty("id").GetString()!;
                        var existing = _blocks.Values.FirstOrDefault(b => b.Id == id);
                        if (existing is not null) existing.CompleteInput = block.GetProperty("input").Clone();
                    }
                }
                break;
            case "result":
                if ((line.TryGetProperty("is_error", out var failed) && failed.GetBoolean()) ||
                    line.GetProperty("subtype").GetString() != "success")
                    throw new InvalidOperationException("ClaudeCode.InferenceFailed: The CLI did not complete successfully.");
                if (Contents.Count == 0 && line.TryGetProperty("result", out var text) && text.GetString() is { Length: > 0 } value)
                {
                    Contents.Add(new TextContent(value));
                    yield return Update([new TextContent(value)]);
                }
                FinalResult = true;
                break;
        }
    }

    private IEnumerable<ChatResponseUpdate> ReadEvent(JsonElement e)
    {
        switch (e.GetProperty("type").GetString())
        {
            case "message_start":
                _blocks.Clear();
                _usage = new();
                var message = e.GetProperty("message");
                _id = message.GetProperty("id").GetString();
                _model = message.GetProperty("model").GetString();
                if (message.TryGetProperty("usage", out var startUsage)) ReadUsage(startUsage);
                break;
            case "content_block_start":
                var content = e.GetProperty("content_block");
                var block = new Block(content.GetProperty("type").GetString()!);
                if (block.Type == "tool_use")
                {
                    block.Id = content.GetProperty("id").GetString()!;
                    block.Name = content.GetProperty("name").GetString()!;
                    block.CompleteInput = content.GetProperty("input").Clone();
                }
                _blocks.Add(e.GetProperty("index").GetInt32(), block);
                break;
            case "content_block_delta":
                var current = _blocks[e.GetProperty("index").GetInt32()];
                var delta = e.GetProperty("delta");
                switch (delta.GetProperty("type").GetString())
                {
                    case "text_delta":
                        var text = delta.GetProperty("text").GetString()!;
                        current.Text.Append(text);
                        yield return Update([new TextContent(text)]);
                        break;
                    case "thinking_delta":
                        var thinking = delta.GetProperty("thinking").GetString()!;
                        current.Text.Append(thinking);
                        yield return Update([new TextReasoningContent(thinking)]);
                        break;
                    case "input_json_delta": current.Json.Append(delta.GetProperty("partial_json").GetString()); break;
                }
                break;
            case "message_delta":
                if (e.GetProperty("delta").TryGetProperty("stop_reason", out var reason)) _stop = reason.GetString();
                if (e.TryGetProperty("usage", out var endUsage)) ReadUsage(endUsage);
                break;
            case "message_stop":
                var calls = new List<AIContent>();
                foreach (var b in _blocks.Values)
                {
                    switch (b.Type)
                    {
                        case "text": Contents.Add(new TextContent(b.Text.ToString())); break;
                        case "thinking": Contents.Add(new TextReasoningContent(b.Text.ToString())); break;
                        case "tool_use" when tools.IsHostTool(b.Name!):
                            var name = tools.HostName(b.Name!);
                            var json = b.CompleteInput;
                            if (b.Json.Length > 0) json = JsonSerializer.Deserialize<JsonElement>(b.Json.ToString());
                            var arguments = json.Deserialize<Dictionary<string, object?>>() ?? [];
                            var call = new FunctionCallContent(b.Id!, name, arguments);
                            mcp.Register(b.Id!, b.Name!);
                            Contents.Add(call);
                            calls.Add(call);
                            break;
                    }
                }
                ToolBoundary = _stop == "tool_use" && calls.Count > 0;
                if (calls.Count > 0 && !ToolBoundary)
                    throw new InvalidDataException("ClaudeCode.ToolBoundary: Tool calls require a tool-use stop reason.");
                _usage.TotalTokenCount = (_usage.InputTokenCount ?? 0) + (_usage.OutputTokenCount ?? 0);
                calls.Add(new UsageContent(_usage));
                yield return Update(calls, ToolBoundary ? ChatFinishReason.ToolCalls : _stop switch
                {
                    "tool_use" => null,
                    "max_tokens" => ChatFinishReason.Length,
                    _ => ChatFinishReason.Stop
                });
                break;
        }
    }

    private ChatResponseUpdate Update(IList<AIContent> contents, ChatFinishReason? finish = null) => new(ChatRole.Assistant, contents)
    {
        ResponseId = _id, MessageId = _id, ModelId = _model, FinishReason = finish
    };

    private void ReadUsage(JsonElement usage)
    {
        if (usage.TryGetProperty("input_tokens", out var input))
        {
            var read = usage.TryGetProperty("cache_read_input_tokens", out var cached) ? cached.GetInt64() : 0;
            var created = usage.TryGetProperty("cache_creation_input_tokens", out var creation) ? creation.GetInt64() : 0;
            _usage.InputTokenCount = input.GetInt64() + read + created;
            _usage.CachedInputTokenCount = read;
        }
        if (usage.TryGetProperty("output_tokens", out var output)) _usage.OutputTokenCount = output.GetInt64();
    }

    private sealed class Block(string type)
    {
        public string Type { get; } = type;
        public string? Id;
        public string? Name;
        public JsonElement CompleteInput;
        public StringBuilder Text { get; } = new();
        public StringBuilder Json { get; } = new();
    }
}
