using System.Diagnostics;
using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

internal sealed class BackendFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dotcraft-claude-test-" + Guid.NewGuid().ToString("N"));
    public string Work => Path.Combine(Root, "work");
    public string Sessions => Path.Combine(Root, "claude-config", "projects",
        new string(Work.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));
    public ClaudeCodeBackend Backend { get; }
    public IModelProvider Provider { get; }
    public IChatClient Client { get; }
    public EffectiveModelRuntime Runtime { get; } = new("test", "claude-fake", "claude-code", "Test", "", "", 30, null, new());
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _supervisor;
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "fake-cli", "DotCraft.ClaudeCode.FakeCli" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public string ClaudeConfig => Path.Combine(Root, "claude-config");

    public BackendFixture(bool allowExtraUsage = false, HttpMessageHandler? usageHandler = null)
    {
        Directory.CreateDirectory(Root);
        Backend = new(new(Executable, 10, allowExtraUsage), Root, _stopping.Token, ClaudeConfig, usageHandler);
        Provider = Backend.CreateProvider(new(Runtime, new UnusedCredentials(), _stopping.Token));
        Client = Provider.CreateChatClient(Runtime);
        _supervisor = Backend.SuperviseAsync(_stopping.Token);
    }

    public static IDisposable Conversation(string thread = "thread", string window = "window", ProviderRequestKind kind = ProviderRequestKind.Turn,
        string? forkedFrom = null) =>
        ProviderRequestContextScope.Push(new(new(thread, thread, null, forkedFrom, "turn", window, kind, 0, "test", null)));

    public JsonElement[] Files(string pattern) => Directory.GetFiles(Root, pattern).Select(p => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p))).ToArray();
    public JsonElement Capture => Assert.Single(Files("capture-*.json"));
    public int[] ChatPids => Files("capture-*.json").Select(c => c.GetProperty("pid").GetInt32()).ToArray();
    public static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _stopping.CancelAsync();
        await _supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        await Backend.DisposeAsync();
        _stopping.Dispose();
        Directory.Delete(Root, true);
    }

    private sealed class UnusedCredentials : IProviderCredentialStore
    {
        public Task<JsonElement?> ReadAsync(CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
        public Task<JsonElement?> ModifyAsync(Func<JsonElement?, CancellationToken, Task<JsonElement?>> update, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("CLI credentials must remain external.");
    }
}
