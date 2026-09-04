using Dsh.Core;

namespace Dsh.Tools;

internal static class WorkspacePath
{
    public static string Resolve(ToolRunContext exec, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var sessionCwd = exec.Agent?.Session.Header.Cwd;
        return Path.GetFullPath(sessionCwd is not null ? Path.Combine(sessionCwd, path) : path);
    }
}
