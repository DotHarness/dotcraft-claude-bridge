using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;
var mcpConfig = Array.IndexOf(args, "--mcp-config") is var at and >= 0 ? args[at + 1] : null;
var root = Directory.GetParent(mcpConfig is null ? Environment.CurrentDirectory : Path.GetDirectoryName(mcpConfig)!)!.FullName;
var pid = Environment.ProcessId;
var accountPath = Path.Combine(root, "fake-account.json");
await File.WriteAllTextAsync(Path.Combine(root, $"started-{pid}.json"), JsonSerializer.Serialize(new { pid }));
if (args.FirstOrDefault() == "auth")
{
    var signedIn = "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"test@example.com\",\"subscriptionType\":\"max\"}";
    if (args.Contains("logout"))
    {
        await File.WriteAllTextAsync(accountPath, "{\"loggedIn\":false,\"authMethod\":\"none\"}");
        return 0;
    }
    if (args.Contains("login"))
    {
        await File.WriteAllTextAsync(Path.Combine(root, $"login-{pid}.json"), JsonSerializer.Serialize(new { browser = Environment.GetEnvironmentVariable("BROWSER") }));
        Console.WriteLine("Opening browser to sign in…");
        Console.WriteLine("If the browser didn't open, visit: https://claude.example.test/oauth/authorize?code=true");
        while (true)
        {
            Console.Write("Paste code here if prompted > ");
            Console.Out.Flush();
            var code = await Console.In.ReadLineAsync();
            if (code is null) return 1;
            if (code == "good-code") break;
            Console.WriteLine("Invalid code. Please make sure the full code was copied.");
        }
        Console.WriteLine("Login successful.");
        await File.WriteAllTextAsync(accountPath, signedIn);
        return 0;
    }
    Console.WriteLine(File.Exists(accountPath) ? await File.ReadAllTextAsync(accountPath) :
        "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"test@example.com\",\"subscriptionType\":\"max\"}");
    return 0;
}
var line = await Console.In.ReadLineAsync();
if (line is null) return 0;
var input = JsonNode.Parse(line)!;
var resume = Array.IndexOf(args, "--resume") is var resumeAt and >= 0 ? args[resumeAt + 1] : null;
if (resume is not null)
{
    if (File.Exists(Path.Combine(root, "fake-resume-fails"))) return 1;
    Emit(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject
    {
        ["subtype"] = "success", ["request_id"] = input["request_id"]!.GetValue<string>(), ["response"] = new JsonObject()
    } });
    line = await Console.In.ReadLineAsync();
    if (line is null) return 0;
    input = JsonNode.Parse(line)!;
}
var session = resume is null || args.Contains("--fork-session") ? $"session-{pid}" : resume;
var sessions = Path.Combine(root, "claude-config", "projects",
    new string(Environment.CurrentDirectory.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));
if (input["type"]!.GetValue<string>() == "control_request")
{
    var usagePath = Path.Combine(root, "fake-usage.json");
    for (; input is not null; input = await Console.In.ReadLineAsync() is { } next ? JsonNode.Parse(next) : null)
    {
        JsonNode? response = input["request"]!["subtype"]!.GetValue<string>() == "get_usage"
            ? JsonNode.Parse(File.Exists(usagePath) ? await File.ReadAllTextAsync(usagePath) : "{\"subscription_type\":\"max\",\"rate_limits\":null}")
            : JsonSerializer.SerializeToNode(new { models = new object[]
            {
                new { value = "default", displayName = "Default (recommended)", supportedEffortLevels = new[] { "low", "high" } },
                new { value = "claude-fake", displayName = "Fake model", supportedEffortLevels = new[] { "low", "medium", "high", "xhigh", "max" } },
                new { value = "claude-fake-lite", displayName = "Fake lite" }
            } });
        Emit(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject
        {
            ["subtype"] = "success", ["request_id"] = input["request_id"]!.GetValue<string>(), ["response"] = response
        } });
    }
    return 0;
}
string Argument(string name) => args[Array.IndexOf(args, name) + 1];
var config = JsonNode.Parse(await File.ReadAllTextAsync(mcpConfig!))!;
var server = config["mcpServers"]!["dotcraft"]!;
var prompt = await File.ReadAllTextAsync(Argument("--append-system-prompt-file"));
await File.WriteAllTextAsync(Path.Combine(root, $"capture-{pid}.json"), JsonSerializer.Serialize(new { pid, args, prompt, input, cwd = Environment.CurrentDirectory, thinking = Environment.GetEnvironmentVariable("CLAUDE_CODE_DISABLE_THINKING") }));
var address = server["url"]!.GetValue<string>();
await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri(address), TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = server["headers"]!["Authorization"]!.GetValue<string>() }
}));
var listed = await mcp.ListToolsAsync();
var listResult = JsonSerializer.SerializeToNode(new ListToolsResult { Tools = listed.Select(tool => tool.ProtocolTool).ToList() });
await File.WriteAllTextAsync(Path.Combine(root, $"tools-{pid}.json"), new JsonObject { ["result"] = listResult }.ToJsonString());
while (true)
{
    Directory.CreateDirectory(sessions);
    await File.AppendAllLinesAsync(Path.Combine(sessions, session + ".jsonl"), [line]);
    Emit(new { type = "system", subtype = "init", session_id = session });
    if (line.Contains("exit-early")) return 0;
    if (line.Contains("extra-usage"))
        Emit(new { type = "rate_limit_event", rate_limit_info = new { status = "rejected", rateLimitType = "five_hour", resetsAt = 4_102_444_800, isUsingOverage = true } });
    if (line.Contains("usage-warning"))
        Emit(new { type = "rate_limit_event", rate_limit_info = new { status = "allowed_warning", rateLimitType = "five_hour", utilization = 0.85, resetsAt = 4_102_444_800,
            unifiedWindows = new { seven_day = new { utilization = 0.4, resetsAt = 4_102_444_800 } } } });
    if (line.Contains("failure"))
    {
        Emit(new { type = "result", subtype = "error_during_execution", is_error = true });
        return 0;
    }
    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bhang\b"))
    {
        Start("hang-message");
        Text(0, "ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
    if (line.Contains("web-search"))
    {
        Start("search-message");
        Event(new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "search-1", name = "WebSearch", input = new { } } });
        Event(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{\"query\":\"dotcraft\"}" } });
        Event(new { type = "content_block_stop", index = 0 });
        Stop("tool_use");
        Emit(new { type = "user", message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "search-1", content = "results" } } } });
        Start("final-message");
        Text(0, "Found it.");
        Stop("end_turn");
    }
    else if (line.Contains("parallel") && !line.Contains("Result"))
    {
        Start("tool-message");
        Thinking(0, "Plan.");
        Text(1, "Checking.");
        var first = listed[0].Name;
        var second = listed[1].Name;
        var a = Call("call-a", first, 2, "first");
        var b = Call("call-b", second, 3, "second");
        Stop("tool_use");
        if (line.Contains("exit-after-tools")) return 0;
        var results = await Task.WhenAll(a, b);
        var reply = string.Join("|", results.Select(r => r.Content.OfType<TextContentBlock>().First().Text));
        if (line.Contains("two-rounds"))
        {
            Start("second-tool-message");
            var next = Call("call-c", first, 0, "third");
            Stop("tool_use");
            reply += "|" + (await next).Content.OfType<TextContentBlock>().First().Text;
        }
        Start("final-message");
        Text(0, reply);
        Stop("end_turn");
    }
    else
    {
        Start("final-message");
        Text(0, "完成 ✓");
        Stop("end_turn");
    }
    Emit(new { type = "result", subtype = "success", is_error = false, result = "duplicate final text" });
    line = await Console.In.ReadLineAsync();
    if (line is null) return 0;
    await File.WriteAllTextAsync(Path.Combine(root, $"turn-{pid}-{Guid.NewGuid():N}.json"), line);
}

Task<CallToolResult> Call(string id, string name, int index, string value)
{
    var wire = "mcp__dotcraft__" + name;
    Event(new { type = "content_block_start", index, content_block = new { type = "tool_use", id, name = wire, input = new { } } });
    var json = JsonSerializer.Serialize(new { value });
    Event(new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = json[..5] } });
    Event(new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = json[5..] } });
    Emit(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id, name = wire, input = new { value } } } } });
    Event(new { type = "content_block_stop", index });
    return mcp.CallToolAsync(name, new Dictionary<string, object?> { ["value"] = value },
        options: new RequestOptions { Meta = new JsonObject { ["claudecode/toolUseId"] = id } }).AsTask();
}
void Start(string id) => Event(new { type = "message_start", message = new
{
    id, model = "claude-fake", usage = new { input_tokens = 10, cache_read_input_tokens = 5, cache_creation_input_tokens = 2, output_tokens = 0 }
} });
void Text(int index, string text)
{
    Event(new { type = "content_block_start", index, content_block = new { type = "text", text = "" } });
    Event(new { type = "content_block_delta", index, delta = new { type = "text_delta", text } });
    Event(new { type = "content_block_stop", index });
}
void Thinking(int index, string thinking)
{
    Event(new { type = "content_block_start", index, content_block = new { type = "thinking", thinking = "" } });
    Event(new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking } });
    Event(new { type = "content_block_stop", index });
}
void Stop(string reason)
{
    Event(new { type = "message_delta", delta = new { stop_reason = reason }, usage = new { output_tokens = 3 } });
    Event(new { type = "message_stop" });
}
static void Event(object value) => Emit(new { type = "stream_event", @event = value });
static void Emit(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
