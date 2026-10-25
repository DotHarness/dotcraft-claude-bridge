using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace DotCraft.ClaudeCode;

internal static class ClaudeCodeContent
{
    public static IEnumerable<AIContent> Expand(AIContent content) => content is FunctionResultContent result
        ? RichResult(result.Result) : [content];

    private static IEnumerable<AIContent> RichResult(object? result) => result switch
    {
        AIContent content => [content],
        IEnumerable<AIContent> contents => contents,
        _ => []
    };

    public static JsonNode? HistoryResult(object? result) => result switch
    {
        AIContent content => HistoryContent(content),
        IEnumerable<AIContent> contents => new JsonArray(contents.Select(HistoryContent).ToArray()),
        _ => ClaudeCodeJson.Value(result)
    };

    private static JsonNode HistoryContent(AIContent content) => content is TextContent text
        ? ClaudeCodeJson.Text(text.Text) : ImageBlock(content);

    public static JsonObject ImageBlock(AIContent content) => content switch
    {
        DataContent data when data.MediaType.StartsWith("image/", StringComparison.Ordinal) =>
            new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = data.MediaType, ["data"] = Convert.ToBase64String(data.Data.Span) } },
        UriContent uri when uri.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true =>
            new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = uri.Uri.ToString() } },
        _ => throw new NotSupportedException($"ClaudeCode.ContentType: Unsupported content '{content.GetType().Name}'.")
    };

    public static CallToolResult McpResult(object? value, bool isError)
    {
        var contents = new List<ContentBlock>();
        if (value is AIContent or IEnumerable<AIContent>)
        {
            foreach (var item in RichResult(value))
            {
                contents.Add(item switch
                {
                    TextContent text => new TextContentBlock { Text = text.Text },
                    DataContent data when data.MediaType.StartsWith("image/", StringComparison.Ordinal) =>
                        ImageContentBlock.FromBytes(data.Data, data.MediaType),
                    UriContent uri => new TextContentBlock { Text = uri.Uri.ToString() },
                    _ => throw new NotSupportedException($"ClaudeCode.ToolResultType: Unsupported tool result '{item.GetType().Name}'.")
                });
            }
        }
        else contents.Add(new TextContentBlock { Text = value as string ?? ClaudeCodeJson.Value(value)?.ToJsonString() ?? "null" });
        return new() { Content = contents, IsError = isError };
    }
}
