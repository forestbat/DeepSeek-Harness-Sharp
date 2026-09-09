using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dsh.Boot;

public static class ProviderCommand
{
    private static readonly HttpClient ModelHttpClient = new();
    private static readonly JsonSerializerOptions ModelJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            var models = entry.Value.Models.Count > 0 ? $" models=[{string.Join(',', entry.Value.Models.Keys)}]" : "";
            return $"{entry.Key}: {entry.Value.Type} {entry.Value.Options?.BaseUrl}{models}";
        }));
    }

    private static Task<CommandResult> RemoveProvider(HarnessHome home, string name)
    {
        var settings = HarnessSettings.Load(home);
        if (!settings.Providers.ContainsKey(name))
            return Task.FromResult<CommandResult>(new CommandResult.Error($"provider \"{name}\" is not configured"));
        var updated = new HarnessSettings
        {
            GlobalDefaultModel = settings.GlobalDefaultModel,
            CompactionModel = settings.CompactionModel,
            Subagent = settings.Subagent,
            Providers = settings.Providers.Where(entry => entry.Key != name).ToDictionary(entry => entry.Key, entry => entry.Value),
            Skills = settings.Skills,
            Rules = settings.Rules,
            McpServers = settings.McpServers,
            Compaction = settings.Compaction,
            Safety = settings.Safety,
            Memory = settings.Memory,
        };
        updated.Save(home);
        return Task.FromResult<CommandResult>(new CommandResult.Success($"removed provider \"{name}\""));
    }

    private static async Task<CommandResult> AddProvider(Context ctx, HarnessHome home, string[] args)
    {
        if (args.Length < 2)
            return new CommandResult.Error("usage: /provider add <name> --base-url <url> --api-key <key> [--type ...] [--model-ids ...]");
        var name = args[0];
        var baseUrl = NextValue(args, "--base-url");
        var apiKey = NextValue(args, "--api-key");
        var type = NextValue(args, "--type") ?? "openai-compatible";
        var modelIds = NextValue(args, "--model-ids") ?? NextValue(args, "--model_ids") ?? NextValue(args, "--models");
        if (baseUrl is null || apiKey is null)
            return new CommandResult.Error("provider add requires --base-url and --api-key");
        var settings = HarnessSettings.Load(home);
        if (settings.Providers.ContainsKey(name))
            return new CommandResult.Error($"provider \"{name}\" already exists");
        var models = new Dictionary<string, ProviderModelSettings>();
        if (modelIds is not null)
        {
            foreach (var modelId in modelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                models[modelId] = new ProviderModelSettings { Name = modelId };
        }
        else
        {
            try
            {
                models = await FetchModelsAsync(baseUrl, apiKey);
            }
            catch (Exception error)
            {
                return new CommandResult.Error($"model auto-discovery failed for \"{name}\": {error.Message}");
            }

            if (models.Count == 0)
                return new CommandResult.Error($"model auto-discovery for \"{name}\" returned no models");
        }

        var provider = new ProviderSettings
        {
            Type = type,
            Options = new ProviderOptions
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
            },
            Models = models,
        };
        var updated = new HarnessSettings
        {
            GlobalDefaultModel = settings.GlobalDefaultModel ?? (models.Count > 0 ? $"{name}/{models.Keys.First()}" : null),
            CompactionModel = settings.CompactionModel,
            Subagent = settings.Subagent,
            Providers = new Dictionary<string, ProviderSettings>(settings.Providers)
            {
                [name] = provider,
            },
            Skills = settings.Skills,
            Rules = settings.Rules,
            McpServers = settings.McpServers,
            Compaction = settings.Compaction,
            Safety = settings.Safety,
            Memory = settings.Memory,
        };
        updated.Save(home);
        try
        {
            var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
            var credentials = new EnvCredentials(home, Environment.CurrentDirectory);
            var options = new HarnessOptions(home, Environment.CurrentDirectory);
            HarnessComposer.RegisterProviderAdapter(ctx, name, provider, baseUrl, null, apiKey, options, credentials, llm);
        }
        catch (Exception error)
        {
            return new CommandResult.Error($"provider \"{name}\" saved but could not be activated: {error.Message}");
        }
        return new CommandResult.Success($"added provider \"{name}\" with {models.Count} model(s)");
    }

    private static async Task<Dictionary<string, ProviderModelSettings>> FetchModelsAsync(string baseUrl, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await ModelHttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {raw}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<ModelListResponse>(json, ModelJsonOptions);
        var models = new Dictionary<string, ProviderModelSettings>();
        foreach (var entry in payload?.Data ?? [])
        {
            var modelId = entry.Id ?? entry.Model;
            if (string.IsNullOrWhiteSpace(modelId))
                continue;
            models[modelId] = new ProviderModelSettings { Name = modelId };
        }

        return models;
    }

    private sealed record ModelListResponse(List<ModelEntry>? Data);

    private sealed record ModelEntry(string? Id, string? Model);

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
