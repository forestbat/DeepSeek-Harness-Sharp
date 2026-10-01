using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Dsh.Memory;

public sealed record MongoMemorySettings(string ConnectionString, string Database, string Collection);

public sealed class MongoMemoryStore : IDisposable
{
    private readonly Lazy<MongoClient> _client;
    private readonly Lazy<IMongoCollection<MemoryDocument>> _collection;

    /**
     * 驱动与它的集群监视线程(心跳)推迟到首次读写才起: 构造本身不再拉起 MongoDB 驱动。
     */
    public MongoMemoryStore(MongoMemorySettings settings)
    {
        _client = new Lazy<MongoClient>(() => new MongoClient(settings.ConnectionString));
        _collection = new Lazy<IMongoCollection<MemoryDocument>>(
            () => _client.Value.GetDatabase(settings.Database).GetCollection<MemoryDocument>(settings.Collection));
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var document = await _collection.Value.Find(document => document.Id == key).FirstOrDefaultAsync(cancellationToken);
        return document?.Text;
    }

    public async Task SetAsync(string key, string text, CancellationToken cancellationToken = default)
    {
        var document = new MemoryDocument
        {
            Id = key,
            Text = text,
            UpdatedAt = DateTime.UtcNow,
        };
        await _collection.Value.ReplaceOneAsync(
            candidate => candidate.Id == key,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }
}

public sealed class MemoryDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}
