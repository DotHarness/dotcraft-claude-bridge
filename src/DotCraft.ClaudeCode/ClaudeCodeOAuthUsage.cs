using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotCraft.ClaudeCode;

/// <summary>Reads plan usage with the CLI's OAuth token; the token is only read, never refreshed or written.</summary>
internal sealed class ClaudeCodeOAuthUsage(string configDirectory, HttpMessageHandler? handler = null) : IDisposable
{
    private const string KeychainService = "Claude Code-credentials";
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private JsonElement? _usage;

    public async Task<JsonElement?> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _fetchedAt >= CacheLifetime)
            {
                _usage = await FetchAsync(ct);
                _fetchedAt = DateTimeOffset.UtcNow;
            }
            return _usage;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement?> FetchAsync(CancellationToken ct)
    {
        if (await ReadAccessTokenAsync(ct) is not { } token) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<string?> ReadAccessTokenAsync(CancellationToken ct)
    {
        var stored = (OperatingSystem.IsMacOS() ? await ReadKeychainAsync(ct) : null) ?? await ReadFileAsync(ct);
        if (stored is null) return null;
        try
        {
            using var document = JsonDocument.Parse(stored);
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token) || token.GetString() is not { Length: > 0 } value) return null;
            return value;
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> ReadFileAsync(CancellationToken ct)
    {
        try { return await File.ReadAllTextAsync(Path.Combine(configDirectory, ".credentials.json"), ct); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<string?> ReadKeychainAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "find-generic-password", "-s", KeychainServiceName(), "-a", Environment.UserName, "-w" })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            var output = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || output.Length == 0) return null;
            if (output.StartsWith('{')) return output;
            try { return Encoding.UTF8.GetString(Convert.FromHexString(output)); }
            catch (FormatException) { return null; }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private string KeychainServiceName()
    {
        var path = Path.GetFullPath(configDirectory);
        if (path == Path.GetFullPath(ClaudeCodeSessions.DefaultConfigDirectory())) return KeychainService;
        return KeychainService + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
