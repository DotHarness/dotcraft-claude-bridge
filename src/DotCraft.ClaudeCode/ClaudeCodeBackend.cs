using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DotCraft.Agents;
using DotCraft.Configuration;
using DotCraft.Contributions;
using Microsoft.Extensions.AI;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeBackend : IModelBackendSource, IModelBackend, IAsyncDisposable
{
    private const int MaxIdleRuns = 6;
    private const int MaxShelvedSessions = 64;
    private readonly ClaudeCodeSettings _settings;
    private readonly string _root;
    private readonly ClaudeCodeSessions _sessions;
    private readonly ClaudeCodeOAuthUsage _usage;
    private readonly ClaudeCodeLimits _limits;
    private readonly CancellationTokenSource _stopping;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, ClaudeCodeRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Shelved> _shelf = new(StringComparer.Ordinal);
    private string? _accountIdentity;
    private int _disposed;
    public IModelBackend Backend => this;
    public CancellationToken Stopping => _stopping.Token;
    public ModelBackendDescriptor Descriptor => new("claude-code", "Claude Code", "claude-code", ["claude-code"],
        [new("cli", "Claude Code account", "cli")], new ModelProviderCapabilities
        {
            CachedInputUsageReporting = true, ExtendedThinking = true
        });

    public ClaudeCodeBackend(ClaudeCodeSettings settings, string root, CancellationToken stopping = default, string? claudeConfigDirectory = null,
        HttpMessageHandler? usageHandler = null)
    {
        _settings = settings;
        _root = root;
        var configDirectory = ClaudeCodeSessions.ConfigDirectory(claudeConfigDirectory);
        _sessions = new(root, configDirectory);
        _usage = new(configDirectory, usageHandler);
        _sessions.Sweep();
        _limits = new(settings.AllowExtraUsage);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public IModelProvider CreateProvider(ModelBackendContext context) => new Provider(this, context.Stopping);

    private async Task CheckAccountAsync(ClaudeCodeAccount account, CancellationToken ct)
    {
        var status = await account.GetStatusAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var identity = status.IsAuthenticated ? status.AccountId + "\0" + status.PlanType : null;
            if (!status.IsAuthenticated || _accountIdentity != identity)
            {
                await DiscardAllAsync();
                _accountIdentity = identity;
            }
        }
        finally { _gate.Release(); }
        if (!status.IsAuthenticated)
            throw new InvalidOperationException("ClaudeCode.SubscriptionLoginRequired: " + ClaudeCodeAccount.LoginHint);
        _limits.ThrowIfBlocked();
    }

    internal async IAsyncEnumerable<ChatResponseUpdate> SampleAsync(ClaudeCodeRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var identity = ProviderRequestContextScope.Current?.CurrentIdentity;
        var key = identity is null ? Guid.NewGuid().ToString("N") :
            string.Join('\0', request.Runtime.ProviderId, request.Runtime.BackendGeneration,
                identity.CurrentThreadId, identity.ContextWindowId, identity.RequestKind.ToString());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
        var token = linked.Token;
        ClaudeCodeRun run;
        await _gate.WaitAsync(token);
        try
        {
            _runs.TryGetValue(key, out var prior);
            if (prior?.IsBusy == true)
                throw new InvalidOperationException("ClaudeCode.ConcurrentSampling: This conversation already has an active sampling request.");
            var reused = false;
            if (prior is not null)
            {
                try { reused = await prior.TryResumeAsync(request, token); }
                catch
                {
                    _runs.Remove(key);
                    await RetireAsync(key, prior, shelve: false);
                    throw;
                }
                if (!reused)
                {
                    _runs.Remove(key);
                    await RetireAsync(key, prior, shelve: false);
                }
            }
            run = reused ? prior! : await StartAsync(key, identity?.ForkedFromThreadId, request, token);
        }
        finally { _gate.Release(); }
        var completed = false;
        try
        {
            await foreach (var update in run.Output.ReadAllAsync(token)) yield return update;
            completed = true;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (completed && identity is not null && run.IsWaiting &&
                    (run.AwaitingTools || identity.RequestKind == ProviderRequestKind.Turn))
                {
                    run.Release();
                    var idle = _runs.Where(pair => !pair.Value.IsBusy).OrderBy(pair => pair.Value.LastUsed).ToArray();
                    foreach (var (idleKey, idleRun) in idle.Take(Math.Max(0, idle.Length - MaxIdleRuns)))
                    {
                        _runs.Remove(idleKey);
                        await RetireAsync(idleKey, idleRun, shelve: true);
                    }
                }
                else
                {
                    if (_runs.TryGetValue(key, out var current) && ReferenceEquals(current, run)) _runs.Remove(key);
                    await RetireAsync(key, run, shelve: false);
                }
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<ClaudeCodeRun> StartAsync(string key, string? forkedFrom, ClaudeCodeRequest request, CancellationToken ct)
    {
        if (_shelf.Remove(key, out var saved))
        {
            if (saved.Fingerprint == request.Fingerprint && request.After(saved.Expected) is { } added &&
                ClaudeCodeRequest.UserInput(added) is { } input)
            {
                var resumed = await LaunchAsync(key, new(_settings, _root, _sessions.WorkDirectory, _limits, request, Stopping, saved.Session), input, ct);
                if (resumed is not null) return resumed;
            }
            _sessions.Remove(saved.Session);
        }
        if (forkedFrom is not null && ForkSource(forkedFrom, request) is var (session, branch))
        {
            var forked = await LaunchAsync(key, new(_settings, _root, _sessions.WorkDirectory, _limits, request, Stopping, session, forkSession: true), branch, ct);
            if (forked is not null) return forked;
        }
        return (await LaunchAsync(key, new(_settings, _root, _sessions.WorkDirectory, _limits, request, Stopping), request.ColdInput(), ct))!;
    }

    private (string Session, JsonObject Input)? ForkSource(string thread, ClaudeCodeRequest request)
    {
        var prefix = string.Join('\0', request.Runtime.ProviderId, request.Runtime.BackendGeneration, thread) + "\0";
        var candidates = _runs.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !pair.Value.IsBusy && pair.Value.CanShelve)
            .Select(pair => (Session: pair.Value.SessionId!, pair.Value.Fingerprint, Expected: pair.Value.Expected!))
            .Concat(_shelf.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => (pair.Value.Session, pair.Value.Fingerprint, pair.Value.Expected)));
        foreach (var (session, fingerprint, expected) in candidates)
        {
            if (fingerprint == request.Fingerprint && request.After(expected) is { } added && ClaudeCodeRequest.UserInput(added) is { } input)
                return (session, input);
        }
        return null;
    }

    private async Task<ClaudeCodeRun?> LaunchAsync(string key, ClaudeCodeRun run, JsonObject input, CancellationToken ct)
    {
        _runs.Add(key, run);
        try
        {
            if (await run.StartAsync(input, ct)) return run;
        }
        catch
        {
            _runs.Remove(key);
            await RetireAsync(key, run, shelve: false);
            throw;
        }
        _runs.Remove(key);
        await RetireAsync(key, run, shelve: false);
        return null;
    }

    private async Task RetireAsync(string key, ClaudeCodeRun run, bool shelve)
    {
        var session = run.SessionId;
        if (shelve && run.CanShelve)
        {
            _shelf[key] = new(session!, run.Fingerprint, run.Expected!, run.LastUsed);
            foreach (var (oldKey, old) in _shelf.OrderBy(pair => pair.Value.IdleSince).Take(Math.Max(0, _shelf.Count - MaxShelvedSessions)).ToArray())
            {
                _shelf.Remove(oldKey);
                _sessions.Remove(old.Session);
            }
            await run.DisposeAsync();
            return;
        }
        await run.DisposeAsync();
        _sessions.Remove(session);
    }

    internal async Task RetireIdleAsync(TimeSpan idleFor)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, run) in _runs.ToArray())
        {
            if (run.IsBusy || now - run.LastUsed < idleFor) continue;
            _runs.Remove(key);
            await RetireAsync(key, run, shelve: true);
        }
        foreach (var (key, saved) in _shelf.ToArray())
        {
            if (now - saved.IdleSince < ClaudeCodeSessions.CacheLifetime) continue;
            _shelf.Remove(key);
            _sessions.Remove(saved.Session);
        }
    }

    internal async Task RetireIdleNowAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try { await RetireIdleAsync(TimeSpan.Zero); }
        finally { _gate.Release(); }
    }

    public async Task SuperviseAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Stopping);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                await _gate.WaitAsync(linked.Token);
                try { await RetireIdleAsync(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds)); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { await StopRunsAsync(); }
    }

    private async Task DiscardAllAsync()
    {
        foreach (var (key, run) in _runs.ToArray()) await RetireAsync(key, run, shelve: false);
        _runs.Clear();
        foreach (var saved in _shelf.Values) _sessions.Remove(saved.Session);
        _shelf.Clear();
    }

    internal async Task StopRunsAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try { await DiscardAllAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync();
        await StopRunsAsync();
        _usage.Dispose();
        _stopping.Dispose();
    }

    private sealed record Shelved(string Session, string Fingerprint, JsonArray Expected, DateTimeOffset IdleSince);

    private sealed class Provider(ClaudeCodeBackend backend, CancellationToken stopping)
        : IModelProvider, IProviderAuthenticator, IModelCatalogProvider, IProviderUsageReader
    {
        private readonly ClaudeCodeAccount _account = new(backend._settings, backend._root, backend._limits, backend._usage, backend.Stopping, stopping, backend.StopRunsAsync);
        public IReadOnlyCollection<string> Protocols => ["claude-code"];
        public IChatClient CreateChatClient(EffectiveModelRuntime runtime) => new Client(backend, runtime, stopping, _account);
        public object? GetService(Type type, object? key = null) => key is null && type.IsInstanceOfType(this) ? this : null;
        public Task<ProviderAuthenticationStatus> GetStatusAsync(CancellationToken ct) => _account.GetStatusAsync(ct);
        public Task<ProviderAuthenticationStatus> LoginAsync(string method, IProviderLoginInteraction interaction, CancellationToken ct) =>
            _account.LoginAsync(method, interaction, ct);
        public Task LogoutAsync(CancellationToken ct) => _account.LogoutAsync(ct);
        public Task<ModelCatalogResult> FetchModelsAsync(EffectiveModelRuntime runtime, CancellationToken ct) => _account.FetchModelsAsync(runtime, ct);
        public Task<ProviderUsageSnapshot?> ReadAsync(CancellationToken ct) => _account.ReadUsageAsync(ct);
    }

    private sealed class Client(ClaudeCodeBackend backend, EffectiveModelRuntime runtime, CancellationToken stopping, ClaudeCodeAccount account) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            var request = new ClaudeCodeRequest(runtime, messages, options);
            await backend.CheckAccountAsync(account, linked.Token);
            await foreach (var update in backend.SampleAsync(request, linked.Token)) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
