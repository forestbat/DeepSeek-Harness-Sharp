using Cordis;
using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Interaction;

public static class MemoryCommand
{
    private const string MemorySectionName = "memory:policy";
    private const string MemoryContextName = "memory:project";
    private const int MemoryPolicyOrder = 550;
    private const int MemoryContextOrder = 105;

    public static IDisposable Register(Context ctx, HarnessOptions options)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        systemPrompt.Section(new PromptSection(
            MemorySectionName,
            MemoryPolicyOrder,
            _ => MemoryPolicyText(options)));
        systemPrompt.Context(new PromptContext(
            MemoryContextName,
            MemoryContextOrder,
            _ => MemoryContextText(options)));

        return commands.Register(new CommandDefinition
        {
            Name = "memory",
            Description = "Enable or disable project memory",
            Input = new CommandInputDescriptor("on|off"),
            Handler = invocation =>
            {
                var raw = invocation.RawInput.Trim();
                if (raw != "on" && raw != "off")
                    return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /memory on | /memory off"));
                var enabled = raw == "on";
                var settings = HarnessSettings.Load(options.Home);
                var updated = new HarnessSettings
                {
                    GlobalDefaultModel = settings.GlobalDefaultModel,
                    CompactionModel = settings.CompactionModel,
                    Subagent = settings.Subagent,
                    Providers = settings.Providers,
                    Skills = settings.Skills,
                    Rules = settings.Rules,
                    McpServers = settings.McpServers,
                    Compaction = settings.Compaction,
                    Safety = settings.Safety,
                    Memory = new MemorySettings { Enabled = enabled, File = settings.Memory?.File },
                };
                updated.Save(options.Home);
                return Task.FromResult<CommandResult>(new CommandResult.Success($"project memory {raw}"));
            },
        });
    }

    private static string MemoryPolicyText(HarnessOptions options)
    {
        if (!IsEnabled(options))
            return "";
        var path = ResolveMemoryPath(options);
        return $"""
            Project memory is enabled.
            Maintain {path} as the project Markdown memory file.
            When you learn stable project facts, decisions, conventions, or corrections, update this file with your file tools.
            Keep entries concise, actionable, and grouped by topic.
            """;
    }

    private static string MemoryContextText(HarnessOptions options)
    {
        if (!IsEnabled(options))
            return "";
        var path = ResolveMemoryPath(options);
        if (!File.Exists(path))
            return $"Project memory ({path}) does not exist yet. Create it when you record project knowledge.";
        return $"Project memory ({path}):\n\n{File.ReadAllText(path)}";
    }

    private static bool IsEnabled(HarnessOptions options)
        => HarnessSettings.Load(options.Home).Memory?.Enabled == true;

    private static string ResolveMemoryPath(HarnessOptions options)
    {
        var configured = HarnessSettings.Load(options.Home).Memory?.File;
        var cwd = options.Cwd ?? Environment.CurrentDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(cwd, ".dsh-memory.md")
            : Path.GetFullPath(configured, cwd);
    }
}
