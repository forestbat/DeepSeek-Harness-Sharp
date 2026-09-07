using Cordis;
using Dsh.Interaction;
using Dsh.Memory;

namespace Dsh.Boot;

public static class MemoryCommand
{
    private const string DefaultConnectionString = "mongodb://localhost:27017";
    private const string DefaultDatabase = "dsh_memory";
    private const string DefaultCollection = "memory";

    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "memory",
            Description = "Read or write project memory (MongoDB-backed)",
            Input = new CommandInputDescriptor("get <key> | set <key> <text>"),
            Handler = async invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length is 0 or 1)
                    return new CommandResult.Error("usage: /memory get <key> | /memory set <key> <text>");
                using var store = new MongoMemoryStore(new MongoMemorySettings(DefaultConnectionString, DefaultDatabase, DefaultCollection));
                switch (tokens[0])
                {
                    case "get":
                    {
                        var text = await store.GetAsync(tokens[1], invocation.Signal);
                        return new CommandResult.Success(text ?? "(no memory)");
                    }
                    case "set" when tokens.Length >= 3:
                        await store.SetAsync(tokens[1], tokens[2], invocation.Signal);
                        return new CommandResult.Success($"memory written: {tokens[1]}");
                    default:
                        return new CommandResult.Error("usage: /memory get <key> | /memory set <key> <text>");
                }
            },
        });
    }
}