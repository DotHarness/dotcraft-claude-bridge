using System.Diagnostics;
using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Plugins;
using DotCraft.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

public sealed class ClaudeCodePluginTests
{
    [Fact]
    public async Task PackagedBackendRunsThroughHostGatesAndReclaimsAfterToolBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-plugin-host-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var userData = Path.Combine(root, "user-data");
        const string id = "claude-code";
        var source = Path.Combine(AppContext.BaseDirectory, "claude-code-plugin");
        var destination = Path.Combine(workspace, ".craft", "plugins", id);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }
        var parsed = PluginManifestParser.Load(destination);
        Assert.NotNull(parsed.Manifest?.Dotnet);
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error);
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, PluginConfigStore.FileName),
            JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new { executablePath = BackendFixture.Executable } }));
        var config = new AppConfig
        {
            GlobalConfigPath = Path.Combine(userData, "config.json"),
            Providers = new() { ["claude"] = new() { BackendId = id } }
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDotCraftRuntime(new() { Config = config, WorkspacePath = workspace, UserDataPath = userData });
        builder.Services.AddSingleton<IConfigSchemaProvider>(new EmptySchema());
        builder.Services.AddSingleton<IConfigDescriptorRegistry>(new EmptySchema());
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            var runtime = host.Services.GetRequiredService<IPluginDotnetRuntimeCoordinator>();
            await runtime.TrustAsync(id);
            await runtime.SetEnabledAsync(id, true);
            var active = Assert.Single(runtime.Snapshot.Plugins, p => p.PluginId == id);
            Assert.True(active.State == PluginDotnetRuntimeState.Active, string.Join(" | ", active.Blockers.Select(b => b.Message)));
            var clients = host.Services.GetRequiredService<ChatClientRegistry>();
            var client = clients.GetChatClient(config, "claude", "claude-fake");
            using var scope = BackendFixture.Conversation();
            var result = await client.GetResponseAsync([new(ChatRole.User, "parallel")], BackendSamplingTests.Options()).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ChatFinishReason.ToolCalls, result.FinishReason);
            Assert.Equal(2, result.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
            var resolver = host.Services.GetRequiredService<IModelRuntimeResolver>();
            Assert.Equal("claude-fake", (await ModelProviderCatalog.FetchAsync(config, resolver, "claude")).Models[0].Id);
            await runtime.SetEnabledAsync(id, false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (runtime.Snapshot.Plugins.Single(p => p.PluginId == id).State != PluginDotnetRuntimeState.Stopped)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(0, runtime.Snapshot.Plugins.Single(p => p.PluginId == id).LeakedGenerations);
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([]));
            var dataRoot = Path.Combine(userData, "plugins", id, "data");
            if (Directory.Exists(dataRoot))
            {
                Assert.Empty(Directory.GetDirectories(dataRoot, "run-*"));
                foreach (var file in Directory.GetFiles(dataRoot, "started-*.json"))
                {
                    var pid = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file)).GetProperty("pid").GetInt32();
                    try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
                    catch (ArgumentException) { }
                }
            }
        }
        finally
        {
            await host.StopAsync();
            await ((IAsyncDisposable)host).DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class EmptySchema : IConfigSchemaProvider, IConfigDescriptorRegistry
    {
        public IReadOnlyList<ConfigSchemaSection> GetConfigSchema() => [];
        public IReadOnlyList<ConfigFieldDescriptor> Fields => [];
    }
}
