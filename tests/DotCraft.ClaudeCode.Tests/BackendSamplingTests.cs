using System.Text.Json;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

public sealed class BackendSamplingTests
{
    [Fact]
    public async Task ParallelToolsReturnToHostAndResumeTheSameCliProcess()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var invoked = 0;
        var options = Options(() => invoked++);
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel") };

        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ChatFinishReason.ToolCalls, first.FinishReason);
        var message = Assert.Single(first.Messages);
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(new[] { "Echo.One", "Echo-Two" }, calls.Select(c => c.Name));
        Assert.Equal("first", Assert.IsType<JsonElement>(calls[0].Arguments!["value"]).GetString());
        Assert.Equal("Plan.", Assert.Single(message.Contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("Checking.", first.Text);
        Assert.Equal(17, first.Usage!.InputTokenCount);
        Assert.Equal(5, first.Usage.CachedInputTokenCount);
        Assert.Equal(3, first.Usage.OutputTokenCount);
        Assert.Equal(0, invoked);
        var pid = Assert.Single(fixture.ChatPids);
        Assert.True(BackendFixture.IsAlive(pid));
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool,
        [
            new FunctionResultContent("call-b", "B-result"),
            new FunctionResultContent("call-a", "A-result")
        ]));

        var final = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("A-result|B-result", final.Text);
        Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
        Assert.Equal(17, final.Usage!.InputTokenCount);
        Assert.Equal(0, invoked);
        Assert.Single(fixture.ChatPids);
        Assert.True(BackendFixture.IsAlive(pid));
        history.AddRange(final.Messages.Select(m => new ChatMessage(m.Role, m.Contents.Where(c => c is not TextReasoningContent).ToList())));
        history.Add(new(ChatRole.User, [new TextContent("next question"), new DataContent(new byte[] { 7 }, "image/png")]));

        var next = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", next.Text);
        Assert.Equal(pid, Assert.Single(fixture.ChatPids));
        var turn = JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Root, $"turn-{pid}-*.json"))))!;
        var content = turn["message"]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("next question", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("Bw==", content[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task IsolatedRequestsDoNotKeepTheCliAfterTheFinalResult()
    {
        await using var fixture = new BackendFixture();

        Assert.Equal("完成 ✓", (await fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")])).Text);

        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
        Assert.Empty(Directory.GetFiles(fixture.Sessions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredConversationResumesItsSavedSessionOrRebuilds(bool resumeFails)
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var history = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var first = await fixture.Client.GetResponseAsync(history);
        var firstPid = Assert.Single(fixture.ChatPids);
        var session = Path.Combine(fixture.Sessions, $"session-{firstPid}.jsonl");

        await fixture.Backend.RetireIdleNowAsync();

        Assert.False(BackendFixture.IsAlive(firstPid));
        Assert.True(File.Exists(session));
        if (resumeFails) await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-resume-fails"), "");
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "next question"));

        Assert.Equal("完成 ✓", (await fixture.Client.GetResponseAsync(history)).Text);

        var latest = fixture.Files("capture-*.json").Single(c => c.GetProperty("pid").GetInt32() != firstPid);
        var args = latest.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        var input = latest.GetProperty("input").GetRawText();
        if (resumeFails)
        {
            Assert.DoesNotContain("--resume", args);
            Assert.Contains("Conversation history", input);
            Assert.False(File.Exists(session));
        }
        else
        {
            Assert.Equal($"session-{firstPid}", args[Array.IndexOf(args, "--resume") + 1]);
            Assert.Contains("next question", input);
            Assert.DoesNotContain("Conversation history", input);
        }
    }

    [Theory]
    [InlineData("live")]
    [InlineData("retired")]
    [InlineData("changed")]
    public async Task ForkedConversationBranchesFromItsSourceSession(string source)
    {
        await using var fixture = new BackendFixture();
        var history = new List<ChatMessage> { new(ChatRole.User, "hello") };
        ChatResponse first;
        using (BackendFixture.Conversation()) first = await fixture.Client.GetResponseAsync(history);
        var sourcePid = Assert.Single(fixture.ChatPids);
        var sourceSession = Path.Combine(fixture.Sessions, $"session-{sourcePid}.jsonl");
        var recorded = await File.ReadAllTextAsync(sourceSession);
        if (source == "retired") await fixture.Backend.RetireIdleNowAsync();
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.User, "suggest the next message"));
        var options = new ChatOptions { Instructions = source == "changed" ? "other instructions" : null };

        using (BackendFixture.Conversation("fork", "fork-window", forkedFrom: "thread"))
            Assert.Equal("完成 ✓", (await fixture.Client.GetResponseAsync(history, options)).Text);

        var fork = fixture.Files("capture-*.json").Single(c => c.GetProperty("pid").GetInt32() != sourcePid);
        var args = fork.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        var input = fork.GetProperty("input").GetRawText();
        if (source == "changed")
        {
            Assert.DoesNotContain("--resume", args);
            Assert.Contains("Conversation history", input);
        }
        else
        {
            Assert.Equal($"session-{sourcePid}", args[Array.IndexOf(args, "--resume") + 1]);
            Assert.Contains("--fork-session", args);
            Assert.Contains("suggest the next message", input);
            Assert.DoesNotContain("Conversation history", input);
        }
        Assert.Equal(recorded, await File.ReadAllTextAsync(sourceSession));
        Assert.Equal(source != "retired", BackendFixture.IsAlive(sourcePid));
    }

    [Fact]
    public async Task ColdStartPreservesInstructionsTranscriptImagesAndNestedToolSchemas()
    {
        await using var fixture = new BackendFixture();
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"value":{"anyOf":[{"type":"string"},{"$ref":"#/$defs/Item"}]}},
             "$defs":{"Item":{"type":"object","properties":{"nested":{"type":"boolean"}}}}}
            """);
        var options = new ChatOptions
        {
            Instructions = "dotcraft instructions",
            Tools = [AIFunctionFactory.CreateDeclaration("Inspect", "Inspect a value", schema)]
        };
        ChatMessage[] history =
        [
            new(ChatRole.System, "workspace instructions"),
            new(new ChatRole("developer"), "developer instructions"),
            new(ChatRole.User, "earlier user"),
            new(ChatRole.Assistant, [new FunctionCallContent("prior", "Inspect", new Dictionary<string, object?> { ["value"] = "old" })]),
            new(ChatRole.Tool, [new FunctionResultContent("prior", new AIContent[] { new TextContent("earlier result"), new DataContent(new byte[] { 4, 5 }, "image/jpeg") })]),
            new(ChatRole.User, [new TextContent("current user"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        ];

        var response = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", response.Text);
        var capture = fixture.Capture;
        var prompt = capture.GetProperty("prompt").GetString()!;
        Assert.Contains("dotcraft instructions", prompt);
        Assert.Contains("workspace instructions", prompt);
        Assert.Contains("developer instructions", prompt);
        var blocks = capture.GetProperty("input").GetProperty("message").GetProperty("content");
        Assert.Contains("earlier user", blocks[0].GetProperty("text").GetString());
        Assert.Contains("earlier result", blocks[0].GetProperty("text").GetString());
        Assert.Contains("prior", blocks[0].GetProperty("text").GetString());
        Assert.Equal("BAU=", blocks[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("current user", blocks[2].GetProperty("text").GetString());
        Assert.Equal("AQID", blocks[3].GetProperty("source").GetProperty("data").GetString());
        var advertised = Assert.Single(fixture.Files("tools-*.json")).GetProperty("result").GetProperty("tools")[0].GetProperty("inputSchema");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schema.GetRawText()), JsonNode.Parse(advertised.GetRawText())));
        var args = capture.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Contains("--strict-mcp-config", args);
        Assert.DoesNotContain("--no-session-persistence", args);
        Assert.DoesNotContain("--bare", args);
        Assert.Contains("--append-system-prompt-file", args);
        Assert.DoesNotContain("--system-prompt-file", args);
        Assert.Equal(fixture.Work, capture.GetProperty("cwd").GetString());
        Assert.Equal("WebSearch", args[Array.IndexOf(args, "--tools") + 1]);
        Assert.Equal("", args[Array.IndexOf(args, "--setting-sources") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("instructions")]
    [InlineData("window")]
    [InlineData("thread")]
    [InlineData("model")]
    [InlineData("tool-schema")]
    public async Task ChangedConversationRebuildsFromCompleteHostHistory(string change)
    {
        await using var fixture = new BackendFixture();
        using var originalScope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        var oldPid = Assert.Single(fixture.ChatPids);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-a", "one"), new FunctionResultContent("call-b", "two")]));
        if (change == "history") history[0] = new(ChatRole.User, "changed parallel");
        if (change == "instructions") options.Instructions = "new instructions";
        if (change == "tool-schema") options.Tools!.Add(AIFunctionFactory.Create(() => "new tool", "Extra"));
        var client = change == "model" ? fixture.Provider.CreateChatClient(fixture.Runtime with { Model = "other-model" }) : fixture.Client;
        using var nextScope = BackendFixture.Conversation(change == "thread" ? "fork" : "thread", change == "window" ? "new-window" : "window");

        var response = await client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("完成 ✓", response.Text);
        Assert.Equal(2, fixture.ChatPids.Length);
        if (change is not ("thread" or "window")) Assert.False(BackendFixture.IsAlive(oldPid));
        var newest = fixture.Files("capture-*.json").Single(c => c.GetProperty("pid").GetInt32() != oldPid);
        var input = newest.GetProperty("input").GetRawText();
        Assert.Contains("call-a", input);
        Assert.Contains("one", input);
    }

    [Fact]
    public async Task ConsecutiveSamplingBoundariesKeepToolResultsAndUsageSeparate()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "two-rounds parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-a", "A"), new FunctionResultContent("call-b", "B")]));

        var second = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ChatFinishReason.ToolCalls, second.FinishReason);
        Assert.Equal("call-c", Assert.Single(second.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Equal(17, second.Usage!.InputTokenCount);
        history.AddRange(second.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-c", "C")]));
        var final = await fixture.Client.GetResponseAsync(history, options).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("A|B|C", final.Text);
        Assert.Single(fixture.ChatPids);
    }

    [Fact]
    public async Task CliExitWhileWaitingForToolsRebuildsInsteadOfWaitingOnAClosedReader()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "exit-after-tools parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-a", "A"), new FunctionResultContent("call-b", "B")]));
        var pid = Assert.Single(fixture.ChatPids);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (BackendFixture.IsAlive(pid)) await Task.Delay(20, timeout.Token);

        var response = await fixture.Client.GetResponseAsync(history, options, timeout.Token);

        Assert.Equal("完成 ✓", response.Text);
        Assert.Equal(2, fixture.ChatPids.Length);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exit-early")]
    public async Task FailedSamplingClosesTheProcessAndDeletesPrivateFiles(string input)
    {
        await using var fixture = new BackendFixture();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, input)]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.All(fixture.ChatPids, pid => Assert.False(BackendFixture.IsAlive(pid)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    [Theory]
    [InlineData("ExtraHigh", true, "xhigh", null)]
    [InlineData("Max", true, "max", null)]
    [InlineData("High", false, null, "1")]
    public async Task ReasoningSettingsReachTheCli(string effort, bool enabled, string? flag, string? thinkingDisabled)
    {
        await using var fixture = new BackendFixture();
        using var pipeline = ProviderPipelineOptionsScope.Push(new(fixture.Runtime, effort, null, enabled, "standard", false, null));

        await fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]);

        var capture = fixture.Capture;
        var args = capture.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(flag, Array.IndexOf(args, "--effort") is var index and >= 0 ? args[index + 1] : null);
        Assert.Equal(thinkingDisabled, capture.GetProperty("thinking").GetString());
    }

    [Fact]
    public async Task RequiredToolsAndJsonResponsesBecomeInstructions()
    {
        await using var fixture = new BackendFixture();
        var options = Options();
        options.ToolMode = ChatToolMode.RequireSpecific("Echo-Two");
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"title":{"type":"string"}}}"""));

        await fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")], options);

        var prompt = fixture.Capture.GetProperty("prompt").GetString()!;
        Assert.Contains("mcp__dotcraft__Echo-Two", prompt);
        Assert.Contains("\"title\"", prompt);
    }

    [Fact]
    public async Task ClaudeCodeWebSearchRunsInsideTheTurn()
    {
        await using var fixture = new BackendFixture();
        var invoked = 0;

        var response = await fixture.Client.GetResponseAsync([new(ChatRole.User, "web-search")], Options(() => invoked++));

        Assert.Equal("Found it.", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Empty(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal(0, invoked);
        var args = fixture.Capture.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Contains("WebSearch", args[Array.IndexOf(args, "--allowedTools") + 1]!.Split(','));
    }

    internal static ChatOptions Options(Action? invoked = null) => new()
    {
        Tools =
        [
            AIFunctionFactory.Create((string value) => { invoked?.Invoke(); return value; }, "Echo.One"),
            AIFunctionFactory.Create((string value) => { invoked?.Invoke(); return value; }, "Echo-Two")
        ]
    };
}
