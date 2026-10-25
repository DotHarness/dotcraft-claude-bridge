using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeRequest
{
    public EffectiveModelRuntime Runtime { get; }
    public ChatMessage[] Messages { get; }
    public ClaudeCodeTools Tools { get; }
    public string SystemPrompt { get; }
    public string Fingerprint { get; }
    public JsonArray History { get; }
    public int? MaxOutputTokens { get; }
    public ProviderReasoningEffort? Effort { get; }

    public ClaudeCodeRequest(EffectiveModelRuntime runtime, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        Runtime = runtime;
        Messages = messages.ToArray();
        Tools = new(options?.ToolMode == ChatToolMode.None ? [] : options?.Tools ?? []);
        SystemPrompt = string.Join("\n\n", new[] { options?.Instructions }
            .Concat(Messages.Where(IsInstruction).Select(m => m.Text))
            .Append(Tools.Count > 0 ? "Tool names in these instructions map to the available MCP tools as follows:\n" + Tools.Mapping : null)
            .Append(ToolChoice(options?.ToolMode))
            .Append(ResponseFormat(options?.ResponseFormat))
            .Where(t => !string.IsNullOrEmpty(t)));
        MaxOutputTokens = options?.MaxOutputTokens ?? runtime.MaxOutputTokens;
        var pipeline = ProviderPipelineOptionsScope.Current;
        Effort = ProviderReasoningOptions.Resolve(ProviderReasoningOptions.ApplyDefaults(options, pipeline))
            ?? (options?.Reasoning is null && pipeline is { ReasoningEnabled: false } ? ProviderReasoningEffort.None : null);
        History = Snapshot(Messages, reasoning: false);
        Fingerprint = new JsonArray(runtime.Model, SystemPrompt, Tools.Schema, MaxOutputTokens, Effort?.ToToken()).ToJsonString();
    }

    private string? ToolChoice(ChatToolMode? mode) => mode switch
    {
        RequiredChatToolMode { RequiredFunctionName: { } name } => $"You must call the {Tools.WireName(name)} tool in your next response.",
        RequiredChatToolMode when Tools.Count > 0 => "You must call one of the available tools in your next response.",
        _ => null
    };

    private static string? ResponseFormat(ChatResponseFormat? format) => format switch
    {
        ChatResponseFormatJson { Schema: { } schema } =>
            "Respond with only a JSON value that matches this JSON schema, without code fences:\n" + schema.GetRawText(),
        ChatResponseFormatJson => "Respond with only a valid JSON value, without code fences.",
        _ => null
    };

    public JsonObject ColdInput()
    {
        var conversation = Messages.Where(m => !IsInstruction(m)).ToArray();
        var lastUser = conversation.LastOrDefault()?.Role == ChatRole.User ? conversation[^1] : null;
        var prior = lastUser is null ? conversation : conversation[..^1];
        var blocks = new JsonArray();
        if (prior.Length > 0)
        {
            blocks.Add(ClaudeCodeJson.Text("Conversation history (role-tagged JSON):\n" + Snapshot(prior).ToJsonString()));
            foreach (var image in prior.SelectMany(m => m.Contents).SelectMany(ClaudeCodeContent.Expand).Where(IsImage))
                blocks.Add(ClaudeCodeContent.ImageBlock(image));
        }
        if (lastUser is not null)
        {
            foreach (var content in lastUser.Contents)
                blocks.Add(content is TextContent text ? ClaudeCodeJson.Text(text.Text) : ClaudeCodeContent.ImageBlock(content));
        }
        else blocks.Add(ClaudeCodeJson.Text("Continue the conversation from the history above."));
        if (blocks.Count == 0) blocks.Add(ClaudeCodeJson.Text("Continue."));
        return new() { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = blocks } };
    }

    public JsonNode?[]? After(JsonArray expected)
    {
        if (History.Count <= expected.Count) return null;
        var prefix = new JsonArray(History.Take(expected.Count).Select(n => n?.DeepClone()).ToArray());
        return JsonNode.DeepEquals(expected, prefix) ? History.Skip(expected.Count).ToArray() : null;
    }

    public static JsonObject? UserInput(IEnumerable<JsonNode?> atoms)
    {
        var blocks = new JsonArray();
        foreach (var atom in atoms)
        {
            if (atom?["role"]?.GetValue<string>() != "user" || atom["content"] is not JsonObject content) return null;
            if (content["type"] is null && content["text"] is JsonValue text) blocks.Add(ClaudeCodeJson.Text(text.GetValue<string>()));
            else if (content["type"]?.GetValue<string>() == "image") blocks.Add(content.DeepClone());
            else return null;
        }
        return blocks.Count == 0 ? null : new() { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = blocks } };
    }

    private static bool IsInstruction(ChatMessage message) => message.Role == ChatRole.System || message.Role.Value == "developer";
    private static bool IsImage(AIContent content) => content is DataContent or UriContent;

    public static JsonArray Snapshot(IEnumerable<ChatMessage> messages, bool reasoning = true)
    {
        var atoms = new JsonArray();
        foreach (var message in messages)
        foreach (var content in message.Contents)
        {
            if (content is UsageContent || (!reasoning && content is TextReasoningContent)) continue;
            JsonNode body = content switch
            {
                TextContent text => new JsonObject { ["text"] = text.Text },
                TextReasoningContent thought => new JsonObject { ["thinking"] = thought.Text },
                FunctionCallContent call => new JsonObject { ["CallId"] = call.CallId, ["Name"] = call.Name, ["Arguments"] = ClaudeCodeJson.Value(call.Arguments) },
                FunctionResultContent result => new JsonObject { ["CallId"] = result.CallId, ["Result"] = ClaudeCodeContent.HistoryResult(result.Result) },
                DataContent or UriContent => ClaudeCodeContent.ImageBlock(content),
                _ => throw new NotSupportedException($"ClaudeCode.ContentType: Unsupported content '{content.GetType().Name}'.")
            };
            var role = message.Role.Value;
            var textKey = content is TextContent ? "text" : content is TextReasoningContent ? "thinking" : null;
            if (textKey is not null && atoms.LastOrDefault() is JsonObject last &&
                last["role"]?.GetValue<string>() == role && last["content"]?[textKey] is JsonValue previous)
                last["content"]![textKey] = previous.GetValue<string>() + body[textKey]!.GetValue<string>();
            else atoms.Add(new JsonObject { ["role"] = role, ["content"] = body });
        }
        return atoms;
    }
}

internal sealed class ClaudeCodeTools
{
    private readonly Dictionary<string, AIFunctionDeclaration> _byWire = new(StringComparer.Ordinal);
    public int Count => _byWire.Count;
    public List<Tool> ProtocolTools => _byWire.Select(pair => new Tool
    {
        Name = pair.Key, Description = pair.Value.Description, InputSchema = pair.Value.JsonSchema
    }).ToList();
    public string[] WireNames => _byWire.Keys.Select(n => "mcp__dotcraft__" + n).ToArray();
    public JsonArray Schema => new(_byWire.Select(pair =>
    {
        var schema = new JsonObject { ["name"] = pair.Key, ["inputSchema"] = JsonNode.Parse(pair.Value.JsonSchema.GetRawText()) };
        if (pair.Value.Description is { } description) schema["description"] = description;
        return (JsonNode)schema;
    }).ToArray());
    public string Mapping => new JsonObject(_byWire.Select(p => new KeyValuePair<string, JsonNode?>(p.Value.Name, JsonValue.Create("mcp__dotcraft__" + p.Key)))).ToJsonString();

    public ClaudeCodeTools(IEnumerable<AITool> tools)
    {
        var originals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration function)
                throw new NotSupportedException("ClaudeCode.ToolType: Only function declarations are supported.");
            if (!originals.Add(function.Name)) throw new ArgumentException("ClaudeCode.DuplicateTool: Tool names must be unique.");
            var safe = new string(function.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(function.Name)))[..12].ToLowerInvariant();
            var wire = safe.Length is > 0 and <= 36 && safe == function.Name ? safe : safe[..Math.Min(safe.Length, 23)] + "_" + hash;
            if (!_byWire.TryAdd(wire, function)) throw new ArgumentException("ClaudeCode.ToolCollision: Tool aliases collide.");
        }
    }

    public bool Contains(string name) => _byWire.ContainsKey(name);
    public bool IsHostTool(string wire) => wire.StartsWith("mcp__dotcraft__", StringComparison.Ordinal);
    public string WireName(string host) => "mcp__dotcraft__" + (_byWire.FirstOrDefault(pair => pair.Value.Name == host).Key
        ?? throw new ArgumentException("ClaudeCode.UnknownTool: The required tool is not declared."));
    public string HostName(string wire)
    {
        const string prefix = "mcp__dotcraft__";
        return wire.StartsWith(prefix, StringComparison.Ordinal) && _byWire.TryGetValue(wire[prefix.Length..], out var tool)
            ? tool.Name : throw new InvalidDataException("ClaudeCode.UnknownTool: The CLI requested an undeclared tool.");
    }
}
