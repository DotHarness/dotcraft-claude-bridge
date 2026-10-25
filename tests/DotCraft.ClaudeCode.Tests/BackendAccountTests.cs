using System.Text.Json;
using DotCraft.Agents;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

public sealed class BackendAccountTests
{
    [Fact]
    public async Task SignInReusesTheSignedInCliAccount()
    {
        await using var fixture = new BackendFixture();
        Assert.Equal("cli", Assert.Single(fixture.Backend.Descriptor.AuthenticationMethods).Kind);
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);
        var interaction = new Interaction();

        var status = await auth.LoginAsync("cli", interaction, default);

        Assert.True(status.IsAuthenticated);
        Assert.Equal("test@example.com", status.Email);
        Assert.Equal("max", status.PlanType);
        Assert.Empty(interaction.Prompts);
        Assert.Empty(fixture.Files("login-*.json"));
    }

    [Fact]
    public async Task SignInRunsTheCliLoginThroughTheHostInteractionAndSignOutEndsIt()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), "{\"loggedIn\":false,\"authMethod\":\"none\"}");
        var auth = Assert.IsAssignableFrom<IProviderAuthenticator>(fixture.Provider);
        var interaction = new Interaction("wrong-code", "good-code");

        var status = await auth.LoginAsync("cli", interaction, default);

        Assert.True(status.IsAuthenticated);
        Assert.Equal("https://claude.example.test/oauth/authorize?code=true", Assert.Single(interaction.Events, e => e.Kind == "auth_url").Url);
        Assert.Single(interaction.Events, e => e.Kind == "info");
        Assert.Equal(["manual_code", "manual_code"], interaction.Prompts.Select(p => p.Kind));
        Assert.Equal("dotcraft-no-browser", Assert.Single(fixture.Files("login-*.json")).GetProperty("browser").GetString());

        await auth.LogoutAsync(default);
        Assert.False((await auth.GetStatusAsync(default)).IsAuthenticated);
    }

    [Fact]
    public async Task SignedOutCliCannotListModelsOrStartInference()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), "{\"loggedIn\":false,\"authMethod\":\"none\"}");
        var catalog = Assert.IsAssignableFrom<IModelCatalogProvider>(fixture.Provider);

        var result = await catalog.FetchModelsAsync(fixture.Runtime, default);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]));

        Assert.False(result.Success);
        Assert.Equal(ModelCatalogErrorCode.Unauthorized, result.ErrorCode);
        Assert.Contains("claude auth login", result.ErrorMessage);
        Assert.Contains("SubscriptionLoginRequired", exception.Message);
        Assert.Empty(fixture.ChatPids);
    }

    [Fact]
    public async Task NonSubscriptionAuthenticationCannotStartInference()
    {
        await using var fixture = new BackendFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"), JsonSerializer.Serialize(new { loggedIn = true, authMethod = "api_key" }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]));

        Assert.Contains("SubscriptionLoginRequired", exception.Message);
        Assert.Empty(fixture.ChatPids);
    }

    [Fact]
    public async Task ModelDiscoverySendsOnlyAnInitializeControlRequest()
    {
        await using var fixture = new BackendFixture();
        var catalog = Assert.IsAssignableFrom<IModelCatalogProvider>(fixture.Provider);

        var result = await catalog.FetchModelsAsync(fixture.Runtime, default);

        Assert.True(result.Success);
        Assert.Equal(["claude-fake", "claude-fake-lite"], result.Models.Select(model => model.Id));
        var model = result.Models[0];
        Assert.Equal("Fake model", model.DisplayName);
        Assert.False(model.Reasoning!.SupportsDisable);
        Assert.Equal(ProviderReasoningEffort.High, model.Reasoning.DefaultEffort);
        Assert.Equal([ProviderReasoningEffort.Low, ProviderReasoningEffort.Medium, ProviderReasoningEffort.High, ProviderReasoningEffort.ExtraHigh, ProviderReasoningEffort.Max],
            model.Reasoning.SupportedEfforts);
        Assert.Null(result.Models[1].Reasoning);
        Assert.Empty(fixture.ChatPids);
        Assert.All(fixture.Files("started-*.json"), start => Assert.False(BackendFixture.IsAlive(start.GetProperty("pid").GetInt32())));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    [Fact]
    public async Task UsageReportsSessionAndWeeklyLimitsFromTheCli()
    {
        await using var fixture = new BackendFixture();
        var reader = Assert.IsAssignableFrom<IProviderUsageReader>(fixture.Provider);
        Assert.Null(await reader.ReadAsync(default));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-usage.json"), """
            {"subscription_type":"max","rate_limits":{
              "five_hour":{"utilization":42.4,"resets_at":"2099-01-01T05:00:00+00:00"},
              "seven_day":{"utilization":100,"resets_at":null}}}
            """);

        var usage = await reader.ReadAsync(default);

        Assert.Equal("max", usage!.PlanType);
        Assert.Equal(42, usage.Primary!.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), usage.Primary.WindowDuration);
        Assert.Equal(DateTimeOffset.Parse("2099-01-01T05:00:00+00:00"), usage.Primary.ResetAt);
        Assert.Equal(100, usage.Secondary!.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), usage.Secondary.WindowDuration);
        Assert.Equal("seven_day", usage.LimitReachedKind);
        Assert.Empty(fixture.ChatPids);
    }

    [Fact]
    public async Task UsageFallsBackToTheAccountEndpointWithTheCliTokenWhenTheCliHasNone()
    {
        var endpoint = new UsageEndpoint("""{"five_hour":{"utilization":12,"resets_at":"2099-01-01T05:00:00+00:00"},"seven_day":{"utilization":63,"resets_at":null}}""");
        await using var rejected = new BackendFixture(usageHandler: endpoint);
        Directory.CreateDirectory(rejected.ClaudeConfig);
        await File.WriteAllTextAsync(Path.Combine(rejected.ClaudeConfig, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"revoked-token","expiresAt":4102444800000}}""");
        Assert.Null(await Assert.IsAssignableFrom<IProviderUsageReader>(rejected.Provider).ReadAsync(default));

        await using var fixture = new BackendFixture(usageHandler: endpoint);
        Directory.CreateDirectory(fixture.ClaudeConfig);
        var stored = """{"claudeAiOauth":{"accessToken":"cli-token","expiresAt":1000}}""";
        await File.WriteAllTextAsync(Path.Combine(fixture.ClaudeConfig, ".credentials.json"), stored);
        var reader = Assert.IsAssignableFrom<IProviderUsageReader>(fixture.Provider);
        var usage = await reader.ReadAsync(default);

        Assert.Equal(["revoked-token", "cli-token"], endpoint.Tokens);
        Assert.Equal(12, usage!.Primary!.UsedPercent);
        Assert.Equal(63, usage.Secondary!.UsedPercent);
        Assert.Equal(stored, await File.ReadAllTextAsync(Path.Combine(fixture.ClaudeConfig, ".credentials.json")));

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-usage.json"),
            """{"subscription_type":"max","rate_limits":{"five_hour":{"utilization":42,"resets_at":null}}}""");
        Assert.Equal(42, (await reader.ReadAsync(default))!.Primary!.UsedPercent);
    }

    [Fact]
    public async Task UsageKeepsTheWindowsReportedDuringATurn()
    {
        await using var fixture = new BackendFixture();
        var reader = Assert.IsAssignableFrom<IProviderUsageReader>(fixture.Provider);

        await fixture.Client.GetResponseAsync([new(ChatRole.User, "usage-warning")]);
        var usage = await reader.ReadAsync(default);

        Assert.Equal(85, usage!.Primary!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4_102_444_800), usage.Primary.ResetAt);
        Assert.Equal(40, usage.Secondary!.UsedPercent);
    }

    private sealed class UsageEndpoint(string body) : HttpMessageHandler
    {
        public List<string> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
            Assert.Equal("oauth-2025-04-20", Assert.Single(request.Headers.GetValues("anthropic-beta")));
            var token = request.Headers.Authorization!.Parameter!;
            Tokens.Add(token);
            if (token == "revoked-token") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtraUsageStopsTheTurnUnlessAllowed(bool allowed)
    {
        await using var fixture = new BackendFixture(allowed);
        var reader = Assert.IsAssignableFrom<IProviderUsageReader>(fixture.Provider);

        if (allowed)
        {
            Assert.Equal("完成 ✓", (await fixture.Client.GetResponseAsync([new(ChatRole.User, "extra-usage")])).Text);
            Assert.Null(await reader.ReadAsync(default));
            return;
        }
        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, "extra-usage")]));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.GetResponseAsync([new(ChatRole.User, "hello")]));

        Assert.StartsWith("ClaudeCode.ExtraUsageBlocked:", stopped.Message);
        Assert.Contains("2100-01-01 00:00 UTC", stopped.Message);
        Assert.StartsWith("ClaudeCode.ExtraUsageBlocked:", refused.Message);
        Assert.False(BackendFixture.IsAlive(Assert.Single(fixture.ChatPids)));
        Assert.Equal("extra_usage", (await reader.ReadAsync(default))?.LimitReachedKind);
    }

    [Fact]
    public async Task AccountChangeDiscardsPendingCliState()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        var options = BackendSamplingTests.Options();
        var history = new List<ChatMessage> { new(ChatRole.User, "parallel") };
        var first = await fixture.Client.GetResponseAsync(history, options);
        var oldPid = Assert.Single(fixture.ChatPids);
        history.AddRange(first.Messages);
        history.Add(new(ChatRole.Tool, [new FunctionResultContent("call-a", "one"), new FunctionResultContent("call-b", "two")]));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fake-account.json"),
            "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"another@example.com\",\"subscriptionType\":\"pro\"}");

        Assert.Equal("完成 ✓", (await fixture.Client.GetResponseAsync(history, options)).Text);

        Assert.False(BackendFixture.IsAlive(oldPid));
        Assert.Equal(2, fixture.ChatPids.Length);
    }

    private sealed class Interaction(params string[] answers) : IProviderLoginInteraction
    {
        private readonly Queue<string> _answers = new(answers);
        public List<ProviderLoginPrompt> Prompts { get; } = [];
        public List<ProviderLoginEvent> Events { get; } = [];
        public ValueTask NotifyAsync(ProviderLoginEvent notification, CancellationToken cancellationToken)
        {
            Events.Add(notification);
            return ValueTask.CompletedTask;
        }
        public Task<string> PromptAsync(ProviderLoginPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_answers.Dequeue());
        }
    }
}
