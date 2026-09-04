using Xunit;

namespace Cordis.Tests;

public class ServiceTests
{
    [Fact]
    public void Provide_RegistersService()
    {
        var ctx = new Context();
        ctx.Provide("foo", 123);
        Assert.Equal(123, ctx.Get("foo"));
    }

    [Fact]
    public void Provide_DuplicateThrows()
    {
        var ctx = new Context();
        ctx.Provide("foo", 123);
        Assert.Throws<CordisException>(() => ctx.Provide("foo", 456));
    }

    [Fact]
    public async Task Unprovide_AwaitsDependents()
    {
        var ctx = new Context();
        ctx.Provide("foo", 1);
        var consumer = ctx.Inject(new Dictionary<string, object?> { ["foo"] = null }, (ctx, _) => null);
        await consumer.Await();
        Assert.Equal(FiberState.Active, consumer.State);
    }

    [Fact]
    public void Isolate_HidesServiceFromOtherRealms()
    {
        var ctx = new Context();
        var realm = ctx.Isolate("foo");
        realm.Provide("foo", "inner");
        Assert.Equal("inner", realm.Get("foo"));
        Assert.NotEqual(
            ctx.Root.IsolateMap["foo"],
            realm.IsolateMap["foo"]);
    }

    [Fact]
    public async Task Service_ClassProvidesItself()
    {
        var ctx = new Context();
        var fiber = ctx.Plugin(typeof(FooPlugin), null);
        await fiber.Await();
        var service = ctx.Get<FooPlugin>("foo");
        Assert.NotNull(service);
        Assert.Equal(42, service.Value);
    }

    private sealed class FooPlugin(Context ctx, object? config) : Service(ctx, "foo")
    {
        public int Value = 42;
    }
}

public class LoggerTests
{
    private sealed class CollectExporter : Exporter
    {
        public List<Message> Messages { get; } = [];
        public int Colors => 0;
        public int? MaxLength => null;
        public void Export(Message message) => Messages.Add(message);
    }

    [Fact]
    public void Logger_FormatsPlaceholders()
    {
        var exporter = new CollectExporter();
        var exporter2 = new CollectExporter();
        var ctx = new Context();
        ctx.Logger.Exporter(exporter);
        var logger = ctx.LoggerFor("test");
        logger.Info("hello %s, answer %d", "world", 42);
        Assert.Single(exporter.Messages);
        Assert.Equal("test", exporter.Messages[0].Name);
        var text = Logger.Format(exporter, exporter.Messages[0]);
        Assert.Equal("hello world, answer 42", text);
    }

    [Fact]
    public void Logger_FiltersByLevel()
    {
        var ctx = new Context();
        var exporter = new LevelExporter(1);
        ctx.Logger.Exporter(exporter);
        var logger = ctx.LoggerFor("test");
        logger.Debug("hidden");
        logger.Warn("visible");
        Assert.Single(exporter.Messages);
    }

    private sealed class LevelExporter(int maxLevel) : Exporter
    {
        public List<Message> Messages { get; } = [];
        public int Colors => 0;
        public int? MaxLength => null;
        public IReadOnlyDictionary<string, int>? Levels { get; } = new Dictionary<string, int> { ["default"] = maxLevel };
        public void Export(Message message) => Messages.Add(message);
    }
}
