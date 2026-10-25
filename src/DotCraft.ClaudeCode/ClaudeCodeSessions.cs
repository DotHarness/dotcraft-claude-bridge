namespace DotCraft.ClaudeCode;

internal sealed class ClaudeCodeSessions
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private readonly string _projects;
    public string WorkDirectory { get; }

    public ClaudeCodeSessions(string root, string configDirectory)
    {
        WorkDirectory = PrivateDirectory.Create(Path.Combine(root, "work"));
        _projects = Path.Combine(configDirectory, "projects", new string(WorkDirectory.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));
    }

    public static string DefaultConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string ConfigDirectory(string? configured) =>
        configured ?? (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } fromEnvironment
            ? fromEnvironment : DefaultConfigDirectory());

    public void Remove(string? session)
    {
        if (string.IsNullOrEmpty(session) || session.IndexOfAny(['/', '\\']) >= 0 || session.Contains("..", StringComparison.Ordinal)) return;
        try
        {
            File.Delete(Path.Combine(_projects, session + ".jsonl"));
            var files = Path.Combine(_projects, session);
            if (Directory.Exists(files)) Directory.Delete(files, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Sweep()
    {
        if (!Directory.Exists(_projects)) return;
        foreach (var file in Directory.EnumerateFiles(_projects, "*.jsonl"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > CacheLifetime + TimeSpan.FromMinutes(1))
                Remove(Path.GetFileNameWithoutExtension(file));
        }
    }
}
