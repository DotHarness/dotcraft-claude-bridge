using System.Globalization;
using System.Text.Json;
using DotCraft.Agents;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeLimits(bool allowExtraUsage)
{
    private static readonly Dictionary<string, TimeSpan> Durations = new()
    {
        ["five_hour"] = TimeSpan.FromHours(5),
        ["seven_day"] = TimeSpan.FromDays(7)
    };
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderRateLimitWindow> _windows = new();
    private Block? _block;

    /// <summary>The latest utilization Claude Code reported for a window during a turn.</summary>
    public ProviderRateLimitWindow? Window(string name)
    {
        lock (_gate) return _windows.TryGetValue(name, out var window) && window.ResetAt > DateTimeOffset.UtcNow ? window : null;
    }

    public string? BlockedKind
    {
        get
        {
            lock (_gate) return Current()?.Kind;
        }
    }

    public void Observe(JsonElement info)
    {
        var status = Read(info, "status");
        var window = Read(info, "rateLimitType");
        var resets = info.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds((long)at.GetDouble()) : (DateTimeOffset?)null;
        var overage = info.TryGetProperty("isUsingOverage", out var extra) && extra.ValueKind == JsonValueKind.True;
        lock (_gate)
        {
            if (window is not null) Record(window, info);
            if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
                foreach (var entry in unified.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object) Record(entry.Name, entry.Value);
            if (overage && !allowExtraUsage)
                _block = new("extra_usage", "ClaudeCode.ExtraUsageBlocked",
                    $"Your Claude {Name(window)} limit is used up, and continuing would draw extra usage, which is off for this provider.{Resets(resets)}",
                    resets ?? DateTimeOffset.UtcNow.AddMinutes(5));
            else if (status == "rejected" && !overage)
                _block = new(window ?? "limit", "ClaudeCode.UsageLimitReached",
                    $"Your Claude {Name(window)} limit is used up.{Resets(resets)}", resets ?? DateTimeOffset.UtcNow.AddMinutes(5));
            else if (status is "allowed" or "allowed_warning") _block = null;
            if (_block is not null) throw _block.Exception();
        }
    }

    private void Record(string name, JsonElement window)
    {
        if (!Durations.TryGetValue(name, out var duration)
            || !window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number) return;
        var resets = window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds((long)at.GetDouble()) : DateTimeOffset.UtcNow + duration;
        _windows[name] = new((int)Math.Round(used.GetDouble() * 100), duration, resets);
    }

    public void ThrowIfBlocked()
    {
        lock (_gate)
        {
            if (Current() is { } block) throw block.Exception();
        }
    }

    private Block? Current()
    {
        if (_block is not null && DateTimeOffset.UtcNow >= _block.Until) _block = null;
        return _block;
    }

    private static string Name(string? window) => window switch
    {
        "five_hour" => "5-hour",
        "seven_day" => "weekly",
        "seven_day_opus" => "weekly Opus",
        "seven_day_sonnet" => "weekly Sonnet",
        "overage" => "extra usage",
        _ => "usage"
    };

    private static string Resets(DateTimeOffset? at) => at is { } value
        ? " It resets at " + value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC." : "";

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Block(string Kind, string Code, string Message, DateTimeOffset Until)
    {
        public InvalidOperationException Exception() => new($"{Code}: {Message}");
    }
}
