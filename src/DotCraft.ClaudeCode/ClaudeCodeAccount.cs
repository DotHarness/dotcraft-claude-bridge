using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using DotCraft.Configuration;

namespace DotCraft.ClaudeCode;

internal sealed partial class ClaudeCodeAccount(ClaudeCodeSettings settings, string root, ClaudeCodeLimits limits, ClaudeCodeOAuthUsage oauthUsage,
    CancellationToken backendStopping,
    CancellationToken providerStopping, Func<Task> stopRuns)
{
    public async Task<ProviderAuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        using var timeout = Budget(ct);
        await using var files = new PrivateDirectory(root);
        await using var process = new ClaudeCodeProcess(settings, files.Path, ["auth", "status", "--json"]);
        var (_, text) = await process.ReadToExitAsync(timeout.Token);
        using var document = JsonDocument.Parse(text);
        var status = document.RootElement;
        var authenticated = status.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.GetBoolean() &&
            Read(status, "authMethod") == "claude.ai";
        return new(authenticated, AccountId: Read(status, "email"), DisplayName: Read(status, "email"),
            PlanType: Read(status, "subscriptionType"), Email: Read(status, "email"),
            FailureCode: authenticated ? null : "ClaudeCode.SubscriptionLoginRequired",
            FailureMessage: authenticated ? null : LoginHint);
    }

    public const string LoginHint = "Sign in to a Claude subscription from the provider settings, or with claude auth login --claudeai, then try again.";

    public async Task<ProviderAuthenticationStatus> LoginAsync(string method, IProviderLoginInteraction interaction, CancellationToken ct)
    {
        if (method != "cli") throw new ArgumentException("ClaudeCode.AuthMethod: Unknown authentication method.");
        var status = await GetStatusAsync(ct);
        if (!status.IsAuthenticated)
        {
            await SignInAsync(interaction, ct);
            status = await GetStatusAsync(ct);
            if (!status.IsAuthenticated) throw new InvalidOperationException("ClaudeCode.SubscriptionLoginRequired: " + LoginHint);
        }
        await stopRuns();
        return status;
    }

    private async Task SignInAsync(IProviderLoginInteraction interaction, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, backendStopping, providerStopping);
        var token = stopping.Token;
        await using var files = new PrivateDirectory(root);
        await using var process = new ClaudeCodeProcess(settings, files.Path, ["auth", "login", "--claudeai"],
            new Dictionary<string, string> { ["BROWSER"] = "dotcraft-no-browser" });
        string? url = null;
        using (var linkWait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            linkWait.CancelAfter(TimeSpan.FromSeconds(30));
            while (url is null && await process.Output.ReadLineAsync(linkWait.Token) is { } line)
                url = Link().Match(Ansi().Replace(line, "")).Value is { Length: > 0 } match ? match : null;
        }
        if (url is null) throw new InvalidOperationException("ClaudeCode.LoginFailed: Claude Code did not provide a sign-in link.");
        await interaction.NotifyAsync(new("auth_url", "Sign in to Claude in your browser, then paste the code it shows.", url), token);
        var last = "";
        while (true)
        {
            var code = await interaction.PromptAsync(new("manual_code", "Paste the code shown after you sign in to Claude."), token);
            await process.Input.WriteLineAsync(code.Trim().AsMemory(), token);
            await process.Input.FlushAsync(token);
            if (await process.Output.ReadLineAsync(token) is not { } reply) break;
            last = Ansi().Replace(reply, "").Replace("Paste code here if prompted >", "").Trim();
            if (!last.Contains("Invalid code", StringComparison.OrdinalIgnoreCase)) break;
            await interaction.NotifyAsync(new("info", "That code was not accepted. Copy the whole code and paste it again."), token);
        }
        while (await process.Output.ReadLineAsync(token) is { } line)
            if (Ansi().Replace(line, "").Trim() is { Length: > 0 } text) last = text;
        if (await process.ExitAsync(token) != 0)
            throw new InvalidOperationException("ClaudeCode.LoginFailed: " + (last.Length > 0 ? last : "Claude Code could not sign in."));
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        using var timeout = Budget(ct);
        await stopRuns();
        await using var files = new PrivateDirectory(root);
        await using var process = new ClaudeCodeProcess(settings, files.Path, ["auth", "logout"]);
        var (code, _) = await process.ReadToExitAsync(timeout.Token);
        if (code != 0) throw new InvalidOperationException("ClaudeCode.LogoutFailed: Claude Code could not sign out.");
    }

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex Link();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    public async Task<ModelCatalogResult> FetchModelsAsync(EffectiveModelRuntime runtime, CancellationToken ct)
    {
        if (!(await GetStatusAsync(ct)).IsAuthenticated)
            return new() { Success = false, ErrorCode = ModelCatalogErrorCode.Unauthorized, ErrorMessage = LoginHint, ProviderId = runtime.ProviderId, Protocol = runtime.Protocol };
        var response = await ControlAsync("initialize", ct);
        return new ModelCatalogResult
        {
            Success = true, ProviderId = runtime.ProviderId, Protocol = runtime.Protocol,
            Models = response.GetProperty("models").EnumerateArray()
                .Where(model => Read(model, "value") is { Length: > 0 } and not "default")
                .Select(model => new ModelCatalogEntry
                {
                    Id = Read(model, "value")!, DisplayName = Read(model, "displayName"), OwnedBy = "Anthropic", Reasoning = Reasoning(model)
                }).ToList()
        };
    }

    private static ModelReasoningSupport? Reasoning(JsonElement model)
    {
        if (!model.TryGetProperty("supportedEffortLevels", out var levels) || levels.ValueKind != JsonValueKind.Array) return null;
        var efforts = levels.EnumerateArray().Select(level => level.GetString())
            .SelectMany(token => Enum.GetValues<ProviderReasoningEffort>().Where(effort => effort != ProviderReasoningEffort.None && effort.ToToken() == token))
            .Distinct().ToArray();
        if (efforts.Length == 0) return null;
        return new(false, efforts, efforts.Contains(ProviderReasoningEffort.High) ? ProviderReasoningEffort.High : efforts[0]);
    }

    public async Task<ProviderUsageSnapshot?> ReadUsageAsync(CancellationToken ct)
    {
        var usage = await ControlAsync("get_usage", ct);
        var windows = usage.TryGetProperty("rate_limits", out var value) && value.ValueKind == JsonValueKind.Object ? value
            : await oauthUsage.ReadAsync(ct) is { ValueKind: JsonValueKind.Object } fetched ? fetched : default;
        var session = (windows.ValueKind == JsonValueKind.Object ? Window(windows, "five_hour", TimeSpan.FromHours(5)) : null)
            ?? limits.Window("five_hour");
        var weekly = (windows.ValueKind == JsonValueKind.Object ? Window(windows, "seven_day", TimeSpan.FromDays(7)) : null)
            ?? limits.Window("seven_day");
        var reached = limits.BlockedKind ?? (session?.UsedPercent >= 100 ? "five_hour" : weekly?.UsedPercent >= 100 ? "seven_day" : null);
        if (session is null && weekly is null && reached is null) return null;
        return new(DateTimeOffset.UtcNow, new Dictionary<string, double>(), PlanType: Read(usage, "subscription_type"),
            Primary: session, Secondary: weekly, LimitReachedKind: reached);
    }

    private static ProviderRateLimitWindow? Window(JsonElement limits, string name, TimeSpan duration)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        var reset = DateTimeOffset.TryParse(Read(window, "resets_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : DateTimeOffset.UtcNow + duration;
        return new((int)Math.Round(used.GetDouble()), duration, reset);
    }

    private async Task<JsonElement> ControlAsync(string subtype, CancellationToken ct)
    {
        using var timeout = Budget(ct);
        await using var files = new PrivateDirectory(root);
        await using var process = new ClaudeCodeProcess(settings, files.Path,
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--tools", "", "--setting-sources", "", "--no-session-persistence"]);
        var id = "";
        foreach (var request in subtype == "initialize" ? ["initialize"] : new[] { "initialize", subtype })
        {
            id = Guid.NewGuid().ToString("N");
            await process.WriteAsync(new() { ["type"] = "control_request", ["request_id"] = id, ["request"] = new JsonObject { ["subtype"] = request } }, timeout.Token);
        }
        while (await process.Output.ReadLineAsync(timeout.Token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (Read(message, "type") != "control_response") continue;
            var response = message.GetProperty("response");
            if (Read(response, "request_id") != id) continue;
            if (Read(response, "subtype") != "success")
                throw new InvalidOperationException($"ClaudeCode.ControlRequestFailed: The CLI could not complete {subtype}.");
            return response.GetProperty("response").Clone();
        }
        throw new IOException($"ClaudeCode.ControlRequestFailed: The CLI exited before completing {subtype}.");
    }

    private CancellationTokenSource Budget(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, backendStopping, providerStopping);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        return linked;
    }

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
