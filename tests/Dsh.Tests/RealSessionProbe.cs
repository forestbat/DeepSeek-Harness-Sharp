using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 指向本机真实会话日志的只读探针。
 * 复制时保持磁盘上原有的目录布局(project 目录名 + session 目录名)原样搬进隔离 home,
 * 因此路径与解析仍完全由生产代码决定, 测试不重写日志格式也不猜项目键。
 */
internal sealed class RealSessionProbe
{
    private static readonly string DefaultSessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");

    /** 本仓库真实会话: 消息条数与可折叠卡片密度不同, 覆盖"底边附近第几条"的不同分布。 */
    private static readonly string[] DefaultSessionIds =
    [
        "23408451-4808-4d05-8e09-e1a91ba22a60",
        "6687fa2a-1fda-4307-9991-e3bf30cbc235",
        "e1a4a29a-8add-48b5-9f8a-2d27c8005fd0",
    ];

    private RealSessionProbe(string projectDirectory, string sessionDirectory)
    {
        ProjectDirectory = projectDirectory;
        SessionDirectory = sessionDirectory;
        Id = SessionId.Create(Path.GetFileName(sessionDirectory));
    }

    public string ProjectDirectory { get; }

    private string SessionDirectory { get; }

    public SessionId Id { get; }

    public static RealSessionProbe Locate(string? sessionId = null)
    {
        var root = Environment.GetEnvironmentVariable("DSH_REPRO_SESSION_ROOT") ?? DefaultSessionsRoot;
        var requested = sessionId ?? Environment.GetEnvironmentVariable("DSH_REPRO_SESSION_ID");
        IReadOnlyList<string> ids = requested is { Length: > 0 } ? [requested] : DefaultSessionIds;
        foreach (var candidate in ids)
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                var directory = Path.Combine(project, candidate);
                if (File.Exists(Path.Combine(directory, "session.jsonl.zstd")))
                    return new RealSessionProbe(project, directory);
            }
        throw new InvalidOperationException($"没有找到真实会话日志: root={root} ids=[{string.Join(", ", ids)}]");
    }

    /** 按原布局复制进 home。 */
    public void Seed(HarnessHome home)
    {
        var target = Path.Combine(home.SessionsPath, Path.GetFileName(ProjectDirectory), Path.GetFileName(SessionDirectory));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(SessionDirectory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }
}
