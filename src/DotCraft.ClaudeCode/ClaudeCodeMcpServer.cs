using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeMcpServer(ClaudeCodeTools tools, CancellationToken stopping) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;
    public string Address { get; private set; } = "";

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(WebApplication).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            server.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
        });
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "dotcraft", Version = "0.1.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools.ProtocolTools }))
            .WithCallToolHandler(CallAsync);
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + _token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        _app.MapMcp("/mcp");
        await _app.StartAsync(ct);
        Address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/mcp";
    }

    public JsonObject Configuration => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["dotcraft"] = new JsonObject { ["type"] = "http", ["url"] = Address, ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + _token } }
        }
    };

    public void Register(string callId, string wireName)
    {
        var pending = _pending.GetOrAdd(callId, _ => new(wireName));
        if (pending.Name != wireName) throw new InvalidDataException("ClaudeCode.ToolIdentity: Tool call identity changed.");
    }

    public void Complete(string callId, object? result, bool isError = false)
    {
        if (!_pending.TryGetValue(callId, out var pending) || !pending.Result.TrySetResult(ClaudeCodeContent.McpResult(result, isError)))
            throw new InvalidDataException("ClaudeCode.ToolResult: Unknown or duplicate tool result.");
    }

    private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var request = context.Params;
        if (request is null || !tools.Contains(request.Name) ||
            request.Meta?["claudecode/toolUseId"] is not JsonValue value ||
            !value.TryGetValue<string>(out var callId) || string.IsNullOrWhiteSpace(callId))
            throw new McpProtocolException("Declared tool and Claude Code tool-use ID are required.", McpErrorCode.InvalidParams);
        var wire = "mcp__dotcraft__" + request.Name;
        var pending = _pending.GetOrAdd(callId, _ => new(wire));
        if (pending.Name != wire || Interlocked.Exchange(ref pending.Claimed, 1) != 0)
            throw new McpProtocolException("Duplicate or mismatched tool call.", McpErrorCode.InvalidParams);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        return await pending.Result.Task.WaitAsync(linked.Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pending in _pending.Values) pending.Result.TrySetCanceled();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    private sealed class Pending(string name)
    {
        public string Name { get; } = name;
        public TaskCompletionSource<CallToolResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Claimed;
    }
}
