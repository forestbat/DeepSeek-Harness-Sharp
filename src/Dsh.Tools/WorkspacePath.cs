using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Agent;

namespace Dsh.Tools;

internal static class WorkspacePath
{
    public static string Resolve(ToolRunContext exec, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var sessionCwd = exec.Agent?.Session.WorkingDirectory;
        return Path.GetFullPath(sessionCwd is not null ? Path.Combine(sessionCwd, path) : path);
    }

    /** The resolved path in a string argument as a write target; malformed arguments yield none and are rejected by the tool itself. */
    public static IReadOnlyList<string> WriteTarget(JsonElement args, ToolRunContext exec, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { } path
           && path.Trim().Length > 0
            ? [Resolve(exec, path)]
            : [];

    /** read 系工具(read、read_image)共用的读取目标: 按会话工作目录解析, 文件不存在时报 FS_NOT_FOUND。 */
    public static string ResolveExistingFile(ToolRunContext exec, string path)
    {
        var target = Resolve(exec, path);
        exec.Signal.ThrowIfCancellationRequested();
        if (!File.Exists(target))
            throw new HarnessException($"file not found: \"{target}\"", "FS_NOT_FOUND");
        return target;
    }
}
