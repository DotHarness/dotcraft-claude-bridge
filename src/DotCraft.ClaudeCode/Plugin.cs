using System.Text.Json;
using DotCraft.Contributions;
using DotCraft.Plugins;

namespace DotCraft.ClaudeCode;

public sealed class Plugin : IDotCraftPlugin
{
    public ValueTask ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = ClaudeCodeSettings.Read(context.Settings);
        var backend = new ClaudeCodeBackend(options, context.DataRoot, context.Lifetime.Stopping);
        context.Lifetime.OwnAsync(backend);
        context.Lifetime.Run(backend.SuperviseAsync);
        context.Contributions.Add<IModelBackendSource>(backend);
        return ValueTask.CompletedTask;
    }
}

internal sealed record ClaudeCodeSettings(string ExecutablePath = "claude", int IdleTimeoutSeconds = 3600, bool AllowExtraUsage = false)
{
    public static ClaudeCodeSettings Read(JsonElement json)
    {
        var path = json.TryGetProperty("executablePath", out var executable) ? executable.GetString() : "claude";
        var idle = json.TryGetProperty("idleTimeoutSeconds", out var value) ? value.GetInt32() : 3600;
        var extraUsage = json.TryGetProperty("allowExtraUsage", out var allow) && allow.GetBoolean();
        if (string.IsNullOrWhiteSpace(path) || idle is < 10 or > 3600)
            throw new ArgumentException("ClaudeCode.InvalidSettings: executablePath or idleTimeoutSeconds is invalid.");
        return new(path, idle, extraUsage);
    }
}
