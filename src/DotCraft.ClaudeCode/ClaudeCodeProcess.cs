using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    public StreamReader Output => _process.StandardOutput;
    public StreamWriter Input => _process.StandardInput;

    public ClaudeCodeProcess(ClaudeCodeSettings settings, string workingDirectory, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(settings.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.ToArray())
        {
            if (name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CLAUDE_CODE_OAUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CLAUDE_CODE_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CLAUDE_CODE_SESSION_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLAUDE_CODE_SIMPLE", StringComparison.OrdinalIgnoreCase) || name == "CLAUDECODE")
                start.Environment.Remove(name);
        }
        start.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "0";
        start.Environment["ENABLE_TOOL_SEARCH"] = "0";
        start.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        start.Environment["DISABLE_AUTO_COMPACT"] = "1";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        _process = Process.Start(start) ?? throw new IOException("ClaudeCode.StartFailed: Could not start the executable.");
        _stderr = DrainErrorsAsync();
    }

    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        while (await _process.StandardError.ReadAsync(buffer) > 0) { }
    }

    public async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        await Input.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
        await Input.FlushAsync(ct);
    }

    public async Task<int> ExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        await _stderr;
        return _process.ExitCode;
    }

    public async Task<(int ExitCode, string Text)> ReadToExitAsync(CancellationToken ct)
    {
        var text = await Output.ReadToEndAsync(ct);
        await _process.WaitForExitAsync(ct);
        await _stderr;
        return (_process.ExitCode, text);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await _process.WaitForExitAsync();
        await _stderr;
        _process.Dispose();
    }
}
