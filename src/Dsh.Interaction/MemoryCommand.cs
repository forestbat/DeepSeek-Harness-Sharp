using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

namespace Dsh.Interaction;

public static class MemoryCommand
{
    private const string MemoryPackage = "@deepseek-ai/dsh-memory";
    private const string MemorySectionName = "memory:policy";
    private const string MemoryContextName = "memory:project";

    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        systemPrompt.Section(new PromptSection(
            MemorySectionName,
            context => MemoryPolicyText(ResolveMemory(ctx, context.Agent?.Session.Header.Cwd))));
        systemPrompt.Context(new PromptContext(
            MemoryContextName,
            context => MemoryContextText(ResolveMemory(ctx, context.Agent?.Session.Header.Cwd))));

        return commands.Register(new CommandDefinition
        {
            Name = "memory",
            Description = "Enable, disable, show, search, or forget project memory",
            Input = new CommandInputDescriptor("on|off|show|forget <key>|find <query>"),
            Handler = invocation => HandleAsync(ctx, invocation),
        });
    }

    private static async Task<CommandResult> HandleAsync(Context ctx, CommandInvocation invocation)
    {
        var raw = invocation.RawInput.Trim();
        switch (raw)
        {
            case "on":
            case "off":
                return await Toggle(ctx, raw == "on");
            case "show":
                if (ResolveMemory(ctx, invocation.Agent.Session.Header.Cwd) is not { } memoryToShow)
                    return new CommandResult.Error("project memory is unavailable; run /memory on first");
                return new CommandResult.Success(await memoryToShow.ShowAsync(invocation.Signal));
        }
        var space = raw.IndexOf(' ');
        var verb = space < 0 ? raw : raw[..space];
        var argument = space < 0 ? "" : raw[(space + 1)..].Trim();
        if (ResolveMemory(ctx, invocation.Agent.Session.Header.Cwd) is not { } memory)
            return new CommandResult.Error("project memory is unavailable; run /memory on first");
        return verb switch
        {
            "forget" => await ForgetAsync(memory, argument, invocation.Signal),
            "find" => await FindAsync(memory, argument, invocation.Signal),
            _ => new CommandResult.Error("usage: /memory on | off | show | forget <key> | find <query>"),
        };
    }

    private static async Task<CommandResult> ForgetAsync(ProjectMemory memory, string key, CancellationToken signal)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new CommandResult.Error("usage: /memory forget <key>");
        var result = await memory.ForgetAsync(new MemoryForgetRequest([key], null, null, false), "cli", signal);
        return new CommandResult.Success($"forgotten: {string.Join(", ", result.Keys)}");
    }

    private static async Task<CommandResult> FindAsync(ProjectMemory memory, string query, CancellationToken signal)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new CommandResult.Error("usage: /memory find <query>");
        var matches = await memory.FindAsync(query, null, signal);
        if (matches.Count == 0)
            return new CommandResult.Success($"no memory records match \"{query}\"");
        return new CommandResult.Success(string.Join('\n', matches.Select(match => $"[{match.Section}] {match.Key} :: {match.Text}")));
    }

    /** 记忆的启用即插件的启用:enable/disable 由插件管理器执行并持久化,效果当场装卸。 */
    private static async Task<CommandResult> Toggle(Context ctx, bool enabled)
    {
        if (ctx.Get<IPluginManager>("pluginManager") is not { } plugins)
            return new CommandResult.Error("plugin manager is not available in this host");
        var message = enabled ? await plugins.EnableAsync(MemoryPackage) : await plugins.DisableAsync(MemoryPackage);
        return new CommandResult.Success($"project memory {(enabled ? "on" : "off")} ({message})");
    }

    private static string MemoryPolicyText(ProjectMemory? memory)
    {
        if (memory is null)
            return "";
        return $"""
            Project memory is enabled (store: {memory.Description}).
            Maintain it with the memory_save tool: remember (upsert a record), correct (record a correction), forget (remove by key(s) or text query), skip (out-of-scope content).
            Records are timestamped automatically; the injected index is capped at 8192 bytes, so read the memory file directly when you need full content.
            """;
    }

    private static string MemoryContextText(ProjectMemory? memory)
    {
        if (memory is null)
            return "";
        try
        {
            return memory.BuildIndexAsync().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            return $"Project memory ({memory.Description}) is unavailable: {error.Message}";
        }
    }

    private static ProjectMemory? ResolveMemory(Context ctx, string? cwd)
        => ctx.Get<IProjectMemoryProvider>(MemoryServices.Provider, false)?.For(cwd)
           ?? ctx.Get<ProjectMemory>(MemoryServices.ProjectMemory, false);
}
