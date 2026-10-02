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
            _ => MemoryPolicyText(ctx)));
        systemPrompt.Context(new PromptContext(
            MemoryContextName,
            _ => MemoryContextText(ctx)));

        return commands.Register(new CommandDefinition
        {
            Name = "memory",
            Description = "Enable, disable, or show project memory",
            Input = new CommandInputDescriptor("on|off|show"),
            Handler = async invocation =>
            {
                var raw = invocation.RawInput.Trim();
                switch (raw)
                {
                    case "on":
                    case "off":
                        return await Toggle(ctx, raw == "on");
                    case "show":
                        if (ResolveMemory(ctx) is not { } memoryToShow)
                            return new CommandResult.Error("project memory is unavailable; run /memory on first");
                        return new CommandResult.Success(await memoryToShow.ShowAsync(invocation.Signal));
                    default:
                        return new CommandResult.Error("usage: /memory on | /memory off | /memory show");
                }
            },
        });
    }

    /** 记忆的启用即插件的启用:enable/disable 由插件管理器执行并持久化,效果当场装卸。 */
    private static async Task<CommandResult> Toggle(Context ctx, bool enabled)
    {
        if (ctx.Get<IPluginManager>("pluginManager") is not { } plugins)
            return new CommandResult.Error("plugin manager is not available in this host");
        var message = enabled ? await plugins.EnableAsync(MemoryPackage) : await plugins.DisableAsync(MemoryPackage);
        return new CommandResult.Success($"project memory {(enabled ? "on" : "off")} ({message})");
    }

    private static string MemoryPolicyText(Context ctx)
    {
        if (ResolveMemory(ctx) is not { } memory)
            return "";
        return $"""
            Project memory is enabled (store: {memory.Description}).
            Maintain it with the memory_save tool: remember (upsert a record), correct (record a correction), forget (remove by key), skip (out-of-scope content).
            Records are timestamped automatically; the injected index is capped at 8192 bytes, so read the memory file directly when you need full content.
            """;
    }

    private static string MemoryContextText(Context ctx)
    {
        if (ResolveMemory(ctx) is not { } memory)
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

    private static ProjectMemory? ResolveMemory(Context ctx)
        => ctx.Get<ProjectMemory>(MemoryServices.ProjectMemory, false);
}
