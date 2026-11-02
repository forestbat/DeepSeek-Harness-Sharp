using Dsh.Core;

namespace Dsh.Memory;

/** Mongo 后端:把文档的 Text 字段当作整段 markdown 记忆,key 固定。 */
public sealed class MongoTextStore(MongoMemoryStore store, MongoMemorySettings settings, string key) : IMemoryStore, IDisposable
{
    public string Description => $"{settings.Database}.{settings.Collection}#{key}";

    public Task<string?> GetAsync(CancellationToken cancellationToken = default, ToolRunContext? run = null)
        => store.GetAsync(key, cancellationToken);

    public Task SetAsync(string text, CancellationToken cancellationToken = default, ToolRunContext? run = null)
        => store.SetAsync(key, text, cancellationToken);

    public void Dispose() => store.Dispose();
}
