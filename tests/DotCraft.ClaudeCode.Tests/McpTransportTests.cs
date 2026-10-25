using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

public sealed class McpTransportTests
{
    [Fact]
    public async Task EndpointRequiresItsPrivateTokenAndCallIdsAndWaitsForHostResults()
    {
        using var stopping = new CancellationTokenSource();
        await using var server = new ClaudeCodeMcpServer(new([AIFunctionFactory.Create((string value) => value, "Echo")]), stopping.Token);
        await server.StartAsync(default);
        using var http = new HttpClient();
        using var unauthorized = await http.PostAsJsonAsync(server.Address, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        await using var client = await ConnectAsync(server);
        Assert.Equal("Echo", Assert.Single(await client.ListToolsAsync()).Name);

        var missingId = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("Echo").AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, missingId.ErrorCode);
        var pending = client.CallToolAsync("Echo", options: Metadata("call")).AsTask();
        server.Register("call", "mcp__dotcraft__Echo");
        Assert.False(pending.IsCompleted);
        server.Complete("call", "host result");
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("host result", Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
        Assert.Throws<InvalidDataException>(() => server.Complete("unknown", "result"));
        await stopping.CancelAsync();
    }

    [Fact]
    public async Task RichToolResultsPreserveImagesTextAndFailureStatus()
    {
        using var stopping = new CancellationTokenSource();
        await using var server = new ClaudeCodeMcpServer(new([AIFunctionFactory.Create(() => "unused", "ReadImage")]), stopping.Token);
        await server.StartAsync(default);
        await using var client = await ConnectAsync(server);
        server.Register("image-call", "mcp__dotcraft__ReadImage");
        server.Complete("image-call", new AIContent[] { new TextContent("image detail"), new DataContent(new byte[] { 1, 2 }, "image/png") }, true);

        var result = await client.CallToolAsync("ReadImage", options: Metadata("image-call"));

        Assert.True(result.IsError);
        Assert.Equal("image detail", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 1, 2 }, image.DecodedData.ToArray());
    }

    private static RequestOptions Metadata(string id) => new() { Meta = new JsonObject { ["claudecode/toolUseId"] = id } };

    private static Task<McpClient> ConnectAsync(ClaudeCodeMcpServer server) => McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new Uri(server.Address), TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = server.Configuration["mcpServers"]!["dotcraft"]!["headers"]!["Authorization"]!.GetValue<string>() }
    }));
}
