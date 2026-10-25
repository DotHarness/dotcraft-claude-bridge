using System.Security.AccessControl;
using System.Security.Principal;

namespace DotCraft.ClaudeCode;

internal sealed class PrivateDirectory : IAsyncDisposable
{
    public string Path { get; }

    public PrivateDirectory(string root) => Path = Create(System.IO.Path.Combine(root, "run-" + Guid.NewGuid().ToString("N")));

    public static string Create(string path)
    {
        if (Directory.Exists(path)) return path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    public async Task<string> WriteAsync(string name, string contents, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(Path, name);
        await File.WriteAllTextAsync(path, contents, ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        for (var attempt = 1; Directory.Exists(Path); attempt++)
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) when (attempt < 50) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) when (attempt < 50) { await Task.Delay(100); }
        }
    }
}
