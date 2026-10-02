using Dsh.Core;

namespace Dsh.Memory;

/** 按插件参数 memory.backend 选后端:缺省 file(项目根/.dsh-memory.md,worktree 归并到主工作树),`mongo` 时用 Mongo 文档。 */
public static class MemoryStoreFactory
{
    public const string MongoBackend = "mongo";
    public const string DefaultFileName = ".dsh-memory.md";

    public static IMemoryStore Create(MemoryPluginConfig config, string cwd)
    {
        if (string.Equals(config.Backend, MongoBackend, StringComparison.OrdinalIgnoreCase))
        {
            var mongo = config.Mongo;
            var connection = new MongoMemorySettings(mongo.ConnectionString, mongo.Database, mongo.Collection);
            var key = string.IsNullOrWhiteSpace(mongo.Key) ? "project" : mongo.Key;
            return new MongoTextStore(new MongoMemoryStore(connection), connection, key);
        }

        var path = string.IsNullOrWhiteSpace(config.File)
            ? Path.Combine(ProjectRoot.Resolve(cwd), DefaultFileName)
            : Path.GetFullPath(config.File, cwd);
        return new FileMemoryStore(path);
    }
}
