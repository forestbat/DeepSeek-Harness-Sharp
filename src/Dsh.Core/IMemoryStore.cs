namespace Dsh.Core;

/** 项目记忆存储:后端可以是 markdown 文件或数据库文档,内容是一整段 markdown 文本。 */
public interface IMemoryStore
{
    /** 提示词里描述的来源(文件路径或库表 + key)。 */
    string Description { get; }

    Task<string?> GetAsync(CancellationToken cancellationToken = default);

    Task SetAsync(string text, CancellationToken cancellationToken = default);
}

public static class MemoryServices
{
    public const string Store = "memoryStore";

    public const string ProjectMemory = "projectMemory";

    public const string Provider = "projectMemoryProvider";
}

/** 按会话工作区提供项目记忆:同一进程可同时服务多个项目,按项目根缓存。 */
public interface IProjectMemoryProvider
{
    ProjectMemory For(string? cwd);
}
