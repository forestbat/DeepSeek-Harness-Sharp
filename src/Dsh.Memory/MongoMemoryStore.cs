using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Dsh.Memory;

public sealed record MongoMemorySettings(string ConnectionString, string Database, string Collection);

public sealed class MongoMemoryStore : IDisposable
{
    private readonly IMongoCollection<MemoryDocument> _collection;
    private readonly MongoClient _client;

    public MongoMemoryStore(MongoMemorySettings settings)
    {
        _client = new MongoClient(settings.ConnectionString);
        _collection = _client.GetDatabase(settings.Database).GetCollection<MemoryDocument>(settings.Collection);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var document = await _collection.Find(document => document.Id == key).FirstOrDefaultAsync(cancellationToken);
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
        await _collection.ReplaceOneAsync(
            document => document.Id == key,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class MemoryDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}
