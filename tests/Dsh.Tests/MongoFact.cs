using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Dsh.Tests;

/** Mongo 是可选后端: 本机没跑 mongo 时自动跳过整个用例, 而不是把它算作失败。 */
public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute([CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1) : base(sourceFilePath, sourceLineNumber)
    {
        if (!MongoProbe.IsAvailable)
            Skip = "MongoDB is not reachable at mongodb://localhost:27017";
    }
}

internal static class MongoProbe
{
    private const string ConnectionString = "mongodb://localhost:27017";

    private static readonly Lazy<bool> Probe = new(Detect);

    public static bool IsAvailable => Probe.Value;

    private static bool Detect()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            settings.ConnectTimeout = TimeSpan.FromSeconds(2);
            using var client = new MongoClient(settings);
            client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return true;
        }
        catch (Exception error) when (error is TimeoutException or MongoException)
        {
            return false;
        }
    }
}
