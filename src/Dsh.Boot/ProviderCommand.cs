using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Boot;

public static class ProviderCommand
{
    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        return commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Input = new CommandInputDescriptor("add|list|remove ..."),
            Handler = invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    return Task.FromResult<CommandResult>(new CommandResult.Success(ListProviders(home)));
                return tokens[0] switch
                {
                    "list" => Task.FromResult<CommandResult>(new CommandResult.Success(ListProviders(home))),
                    "remove" when tokens.Length >= 2 => RemoveProvider(home, tokens[1]),
                    "add" => AddProvider(ctx, home, tokens[1..]),
                    _ => Task.FromResult<CommandResult>(new CommandResult.Error("usage: /provider list | add <name> --base-url <url> --api-key <key> [--type ...] [--model-ids ...] | remove <name>")),
                };
            },
        });
    }

    private static string ListProviders(HarnessHome home)
    {
        var settings = HarnessSettings.Load(home);
        if (settings.Providers.Count == 0)
            return "no providers configured";
        return string.Join('\n', settings.Providers.Select(entry =>
        {
            var modelIds = entry.Value.ModelIds is { Count: > 0 } models ? $" models=[{string.Join(',', models)}]" : "";
            return $"{entry.Key}: {entry.Value.Type} {entry.Value.BaseUrl}{modelIds}";
        }));
    }

    private static Task<CommandResult> RemoveProvider(HarnessHome home, string name)
    {
        var settings = HarnessSettings.Load(home);
        if (!settings.Providers.ContainsKey(name))
            return Task.FromResult<CommandResult>(new CommandResult.Error($"provider \"{name}\" is not configured"));
        var updated = new HarnessSettings
        {
            Default = settings.Default,
            Providers = settings.Providers.Where(entry => entry.Key != name).ToDictionary(entry => entry.Key, entry => entry.Value),
            Configs = settings.Configs,
            Safety = settings.Safety,
        };
        updated.Save(home);
        return Task.FromResult<CommandResult>(new CommandResult.Success($"removed provider \"{name}\""));
    }

    private static Task<CommandResult> AddProvider(Context ctx, HarnessHome home, string[] args)
    {
        if (args.Length < 2)
            return Task.FromResult<CommandResult>(new CommandResult.Error("usage: /provider add <name> --base-url <url> --api-key <key> [--type ...] [--model-ids ...]"));
        var name = args[0];
        var baseUrl = NextValue(args, "--base-url");
        var apiKey = NextValue(args, "--api-key");
        var type = NextValue(args, "--type") ?? "openai-compatible";
        var modelIds = NextValue(args, "--model-ids") ?? NextValue(args, "--model_ids");
        if (baseUrl is null || apiKey is null)
            return Task.FromResult<CommandResult>(new CommandResult.Error("provider add requires --base-url and --api-key"));
        var settings = HarnessSettings.Load(home);
        if (settings.Providers.ContainsKey(name))
            return Task.FromResult<CommandResult>(new CommandResult.Error($"provider \"{name}\" already exists"));
        var updated = new HarnessSettings
        {
            Default = settings.Default,
            Providers = new Dictionary<string, ProviderSettings>(settings.Providers)
            {
                [name] = new ProviderSettings
                {
                    Type = type,
                    BaseUrl = baseUrl,
                    ApiKey = apiKey,
                    ModelIds = modelIds is null ? null : modelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                },
            },
            Configs = settings.Configs,
            Safety = settings.Safety,
        };
        updated.Save(home);
        try
        {
            var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            var credentials = new EnvCredentials(home, Environment.CurrentDirectory);
            var options = new HarnessOptions(home, Environment.CurrentDirectory);
            var provider = new ProviderSettings
            {
                Type = type,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                ModelIds = modelIds is null ? null : modelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            };
            HarnessComposer.RegisterProviderAdapter(ctx, name, provider, baseUrl, null, apiKey, options, credentials, llm);
        }
        catch (Exception error)
        {
            return Task.FromResult<CommandResult>(new CommandResult.Error($"provider \"{name}\" saved but could not be activated: {error.Message}"));
        }
        return Task.FromResult<CommandResult>(new CommandResult.Success($"added provider \"{name}\""));
    }

    private static string? NextValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == option)
                return args[index + 1];
        }
        return null;
    }
}