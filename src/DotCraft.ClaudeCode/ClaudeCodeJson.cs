using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotCraft.ClaudeCode;

internal static class ClaudeCodeJson
{
    public static JsonNode? Value(object? value) => JsonSerializer.SerializeToNode(value);
    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
}
