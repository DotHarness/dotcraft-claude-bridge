using Microsoft.Extensions.AI;
using Xunit;

namespace DotCraft.ClaudeCode.Tests;

public sealed class BackendLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAndAbandonedEnumerationTerminateTheChild(bool cancel)
    {
        await using var fixture = new BackendFixture();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = fixture.Client.GetStreamingResponseAsync([new(ChatRole.User, "hang")], cancellationToken: ct.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal("ready", stream.Current.Text);
            if (cancel)
            {
                ct.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.MoveNextAsync().AsTask());
            }
        }
        Assert.All(fixture.ChatPids, pid => Assert.False(BackendFixture.IsAlive(pid)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }

    [Fact]
    public async Task GenerationShutdownDrainsParkedMcpRequests()
    {
        var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        await fixture.Client.GetResponseAsync([new(ChatRole.User, "parallel")], BackendSamplingTests.Options()).WaitAsync(TimeSpan.FromSeconds(15));
        var pid = Assert.Single(fixture.ChatPids);
        Assert.True(BackendFixture.IsAlive(pid));

        await fixture.DisposeAsync();

        Assert.False(BackendFixture.IsAlive(pid));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task IdleExpirationTerminatesAnUnansweredToolCall()
    {
        await using var fixture = new BackendFixture();
        using var scope = BackendFixture.Conversation();
        await fixture.Client.GetResponseAsync([new(ChatRole.User, "parallel")], BackendSamplingTests.Options()).WaitAsync(TimeSpan.FromSeconds(15));
        var pid = Assert.Single(fixture.ChatPids);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (BackendFixture.IsAlive(pid) || Directory.GetDirectories(fixture.Root, "run-*").Length > 0)
            await Task.Delay(100, timeout.Token);
        Assert.Empty(Directory.GetDirectories(fixture.Root, "run-*"));
    }
}
