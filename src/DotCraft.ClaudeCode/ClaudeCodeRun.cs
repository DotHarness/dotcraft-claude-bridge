using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotCraft.Configuration;
using Microsoft.Extensions.AI;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeRun : IAsyncDisposable
{
    private const string WebSearch = "WebSearch";
    private readonly PrivateDirectory _files;
    private readonly ClaudeCodeSettings _settings;
    private readonly string _work;
    private readonly string? _resume;
    private readonly bool _forkSession;
    private readonly ClaudeCodeLimits _limits;
    private readonly CancellationTokenSource _stopping;
    private readonly ClaudeCodeMcpServer _mcp;
    private readonly object _state = new();
    private ClaudeCodeProcess? _process;
    private Task? _reader;
    private ClaudeCodeRequest _request;
    private ClaudeCodeStreamDecoder _decoder;
    private Channel<ChatResponseUpdate> _output = NewChannel();
    private JsonArray? _expected;
    private TaskCompletionSource<bool>? _initialized;
    private string? _initializeId;
    private int _busy = 1;
    private int _disposed;
    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsWaiting => _expected is not null && !_stopping.IsCancellationRequested;
    public ChannelReader<ChatResponseUpdate> Output => _output.Reader;
    public string? SessionId { get; private set; }
    public string Fingerprint => _request.Fingerprint;
    public JsonArray? Expected => _expected;
    public bool CanShelve => IsWaiting && !AwaitingTools && SessionId is not null;

    public ClaudeCodeRun(ClaudeCodeSettings settings, string root, string work, ClaudeCodeLimits limits,
        ClaudeCodeRequest request, CancellationToken stopping, string? resume = null, bool forkSession = false)
    {
        _settings = settings;
        _work = work;
        _resume = resume;
        _forkSession = forkSession;
        SessionId = forkSession ? null : resume;
        _limits = limits;
        _request = request;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _files = new(root);
        _mcp = new(request.Tools, _stopping.Token);
        _decoder = new(request.Tools, _mcp, _limits);
    }

    public async Task<bool> StartAsync(JsonObject input, CancellationToken ct)
    {
        await _mcp.StartAsync(ct);
        var prompt = await _files.WriteAsync("system.txt", _request.SystemPrompt, ct);
        var config = await _files.WriteAsync("mcp.json", _mcp.Configuration.ToJsonString(), ct);
        var arguments = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json",
            "--include-partial-messages", "--verbose", "--model", _request.Runtime.Model,
            "--tools", _request.Tools.Count > 0 ? WebSearch : "", "--strict-mcp-config", "--mcp-config", config,
            "--setting-sources", "", "--append-system-prompt-file", prompt
        };
        if (_resume is not null)
        {
            arguments.Add("--resume");
            arguments.Add(_resume);
            if (_forkSession) arguments.Add("--fork-session");
            _initializeId = Guid.NewGuid().ToString("N");
            _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (_request.Tools.Count > 0)
        {
            arguments.Add("--allowedTools");
            arguments.Add(string.Join(",", _request.Tools.WireNames.Append(WebSearch)));
        }
        var environment = new Dictionary<string, string> { ["CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS"] = "1" };
        if (_request.MaxOutputTokens is { } maximum)
            environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_request.Effort == ProviderReasoningEffort.None) environment["CLAUDE_CODE_DISABLE_THINKING"] = "1";
        else if (_request.Effort is { } effort)
        {
            arguments.Add("--effort");
            arguments.Add(effort.ToToken());
        }
        _process = new(_settings, _work, arguments, environment);
        _reader = ReadAsync();
        if (_initialized is not null)
        {
            await _process.WriteAsync(new() { ["type"] = "control_request", ["request_id"] = _initializeId,
                ["request"] = new JsonObject { ["subtype"] = "initialize" } }, ct);
            try
            {
                if (!await _initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), ct)) return false;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException) { return false; }
        }
        await _process.WriteAsync(input, ct);
        return true;
    }

    public async Task<bool> TryResumeAsync(ClaudeCodeRequest request, CancellationToken ct)
    {
        JsonObject? input = null;
        lock (_state)
        {
            if (!IsWaiting || _request.Fingerprint != request.Fingerprint || request.After(_expected!) is not { } added) return false;
            FunctionResultContent[] results = [];
            if (AwaitingTools)
            {
                if (added.Length != _decoder.CallIds.Count ||
                    !added.All(n => n?["role"]?.GetValue<string>() == "tool" && n["content"]?["CallId"] is not null)) return false;
                results = request.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().TakeLast(_decoder.CallIds.Count).ToArray();
                var ids = _decoder.CallIds.ToHashSet(StringComparer.Ordinal);
                if (results.Length != ids.Count || !ids.SetEquals(results.Select(r => r.CallId))) return false;
            }
            else if ((input = ClaudeCodeRequest.UserInput(added)) is null) return false;
            Volatile.Write(ref _busy, 1);
            _request = request;
            _expected = null;
            _output = NewChannel();
            _decoder = new(request.Tools, _mcp, _limits);
            foreach (var result in results) _mcp.Complete(result.CallId, result.Result, result.Exception is not null);
        }
        if (input is null) return true;
        try { await _process!.WriteAsync(input, ct); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { return false; }
        return true;
    }

    public bool AwaitingTools => _decoder.CallIds.Count > 0;

    public void Release()
    {
        LastUsed = DateTimeOffset.UtcNow;
        Volatile.Write(ref _busy, 0);
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process!.Output.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                switch (message.GetProperty("type").GetString())
                {
                    case "system" when message.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String:
                        SessionId = session.GetString();
                        break;
                    case "control_response" when message.GetProperty("response") is var response &&
                        response.TryGetProperty("request_id", out var id) && id.GetString() == _initializeId:
                        _initialized?.TrySetResult(response.GetProperty("subtype").GetString() == "success");
                        continue;
                }
                var decoder = _decoder;
                var output = _output;
                foreach (var update in decoder.Read(message))
                    await output.Writer.WriteAsync(update, _stopping.Token);
                lock (_state)
                {
                    if (!ReferenceEquals(decoder, _decoder)) continue;
                    if (decoder.ToolBoundary || decoder.FinalResult)
                    {
                        _expected = ClaudeCodeRequest.Snapshot(_request.Messages.Append(new ChatMessage(ChatRole.Assistant, decoder.Contents)), reasoning: false);
                        output.Writer.TryComplete();
                    }
                }
            }
            throw new IOException("ClaudeCode.UnexpectedExit: The CLI closed its output.");
        }
        catch (Exception exception)
        {
            _initialized?.TrySetException(new IOException("ClaudeCode.ResumeFailed: The CLI ended before it loaded the session.", exception));
            lock (_state)
            {
                _expected = null;
                _output.Writer.TryComplete(exception);
            }
        }
    }

    private static Channel<ChatResponseUpdate> NewChannel() => Channel.CreateBounded<ChatResponseUpdate>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync();
        if (_process is not null) await _process.DisposeAsync();
        if (_reader is not null) await _reader;
        await _mcp.DisposeAsync();
        await _files.DisposeAsync();
        _stopping.Dispose();
    }
}
