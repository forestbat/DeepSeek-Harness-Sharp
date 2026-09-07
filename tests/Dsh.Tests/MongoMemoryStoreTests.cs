using Dsh.Memory;
using MongoDB.Driver;

namespace Dsh.Tests;

public sealed class MongoMemoryStoreTests
{
    [Fact]
    public async Task WritesAndReadsMemoryThroughMongo()
    {
        const string connectionString = "mongodb://localhost:27017";
        const string database = "dsh_memory_test";
        const string collection = "memory_test";
        var key = $"dsh-test-{Guid.NewGuid():N}";
        var client = new MongoClient(connectionString);
        var mongoCollection = client.GetDatabase(database).GetCollection<MemoryDocument>(collection);
        using var store = new MongoMemoryStore(new MongoMemorySettings(connectionString, database, collection));
        try
        {
            await store.SetAsync(key, "hello from mongo", CancellationToken.None);
            var text = await store.GetAsync(key, CancellationToken.None);
            Assert.Equal("hello from mongo", text);
        }
        finally
        {
            await mongoCollection.DeleteOneAsync(document => document.Id == key);
        }
    }
}
