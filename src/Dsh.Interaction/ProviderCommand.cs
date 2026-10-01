using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dsh.Interaction;

public static class ProviderCommand
{
    private static readonly HttpClient ModelHttpClient = new();
    private static readonly JsonSerializerOptions ModelJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "provider",
            Description = "Manage LLM providers",
            Input = new CommandInputDescriptor("add|list|remove|catalog ..."),
            Handler = invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    return Task.FromResult<CommandResult>(new CommandResult.Success(ListProviders(ctx, home)));
                return tokens[0] switch
                {
                    "list" => Task.FromResult<CommandResult>(new CommandResult.Success(ListProviders(ctx, home))),
                    "remove" when tokens.Length >= 2 => RemoveProvider(ctx, home, tokens[1]),
                    "catalog" => ListCatalog(home, tokens[1..], invocation.Signal),
                    "add" => AddProvider(ctx, home, tokens[1..]),
                    _ => Task.FromResult<CommandResult>(new CommandResult.Error(Usage)),
                };
            },
        });
    }

    private const string Usage = "usage: /provider list | add <name> --base-url <url> --api-key <key> "
        + "[--type openai-compatible|openai-compatible(response)|anthropic-messages|deepseek|custom(...)] [--model-ids <all>|id,...] "
        + "| remove <name> | catalog [query] [--page N] [--all] [refresh]";

    /** 目录浏览每页条数: 数百个 provider 全量铺开会把回执撑爆。 */
    private const int CatalogPageSize = 25;

    /** models.dev 目录: 列出可一键参考的 provider(族/风格/baseUrl/key 环境变量); query 按 id/显示名子串过滤, refresh 强制联网刷新。 */
    private static async Task<CommandResult> ListCatalog(HarnessHome home, string[] args, CancellationToken signal)
    {
        var refresh = false;
        var all = false;
        var page = 1;
        var query = "";
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (string.Equals(token, "refresh", StringComparison.OrdinalIgnoreCase))
            {
                refresh = true;
                continue;
            }

            if (string.Equals(token, "--all", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
                continue;
            }

            if (string.Equals(token, "--page", StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length
                && int.TryParse(args[index + 1], out var parsed))
            {
                page = Math.Max(1, parsed);
                index++;
                continue;
            }

            query = query.Length == 0 ? token : $"{query} {token}";
        }

        var snapshot = await ProviderCatalog.LoadAsync(home, refresh, signal);
        var source = snapshot.Providers.Count == 0
            ? "无目录"
            : snapshot.FetchedAt is { } fetchedAt
                ? $"{(snapshot.FromCache ? "缓存" : "联网")} {fetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "内置快照";
        var lines = new List<string> { $"models.dev: {snapshot.Providers.Count} providers ({source})" };
        if (snapshot.Error is { Length: > 0 } error)
            lines.Add($"  ! {error}");
        if (snapshot.Providers.Count == 0)
        {
            lines.Add("  (无缓存; /provider catalog refresh 联网拉取)");
            return new CommandResult.Success(string.Join('\n', lines));
        }

        IReadOnlyList<ProviderCatalogEntry> matched = query.Length == 0
            ? snapshot.Providers
            : [.. snapshot.Providers.Where(provider => Matches(provider, query))];
        if (matched.Count == 0)
        {
            lines.Add($"  (无匹配 \"{query}\")");
            return new CommandResult.Success(string.Join('\n', lines));
        }

        var pageCount = Math.Max(1, (matched.Count + CatalogPageSize - 1) / CatalogPageSize);
        var current = Math.Min(page, pageCount);
        var pageItems = all
            ? matched
            : [.. matched.Skip((current - 1) * CatalogPageSize).Take(CatalogPageSize)];
        lines.AddRange(pageItems.Select(provider => $"  {provider.Describe()}"));
        if (!all && matched.Count > CatalogPageSize)
        {
            var more = current < pageCount
                ? $" · 下一页: /provider catalog --page {current + 1}{QuerySuffix(query)}"
                : "";
            lines.Add($"  第 {current}/{pageCount} 页 · 共 {matched.Count} 项{more} · --all 看全部");
        }

        return new CommandResult.Success(string.Join('\n', lines));
    }

    private static bool Matches(ProviderCatalogEntry provider, string query)
        => provider.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string QuerySuffix(string query) => query.Length == 0 ? "" : $" {query}";

    private static string ListProviders(Context ctx, HarnessHome home)
    {
        var settings = HarnessSettings.Load(home);
        if (settings.Providers.Count == 0)
            return "no providers configured";
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        return string.Join('\n', settings.Providers.Select(entry =>
        {
            var models = entry.Value.Models.Count > 0 ? $" models=[{string.Join(',', entry.Value.Models.Keys)}]" : "";
            var type = ProviderTypes.Canonical(entry.Value.Type);
            var wire = ProviderTypes.Parse(entry.Value.Type).Wire;
            var source = factories is not null && factories.TryResolve(wire, out var resolvedSource, out _)
                ? resolvedSource
                : null;
            var adapter = llm?.ListProviders().FirstOrDefault(provider => provider.Id == entry.Key);
            var state = adapter is not null ? $"active ({adapter.Name})" : "inactive";
            var served = source is null ? $"no adapter plugin serves type \"{type}\"" : $"served by {source}";
            return $"{entry.Key}: type={type} {state}, {served}, baseUrl={entry.Value.Options?.BaseUrl}{models}";
        }));
    }

    private static Task<CommandResult> RemoveProvider(Context ctx, HarnessHome home, string name)
    {
        var settings = HarnessSettings.Load(home);
        if (!settings.Providers.ContainsKey(name))
            return Task.FromResult<CommandResult>(new CommandResult.Error($"provider \"{name}\" is not configured"));
        ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)?.UnregisterAdapter(name);
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
            return new CommandResult.Error(Usage);
        var name = args[0];
        var baseUrl = NextValue(args, "--base-url");
        var apiKey = NextValue(args, "--api-key");
        var type = ProviderTypes.Canonical(NextValue(args, "--type"));
        var modelIds = NextValue(args, "--model-ids") ?? NextValue(args, "--model_ids") ?? NextValue(args, "--models");
        if (baseUrl is null || apiKey is null)
            return new CommandResult.Error("provider add requires --base-url and --api-key");
        var settings = HarnessSettings.Load(home);
        if (settings.Providers.ContainsKey(name))
            return new CommandResult.Error($"provider \"{name}\" already exists");
        var models = new Dictionary<string, ProviderModelSettings>();
        if (modelIds is not null)
        {
            IReadOnlyList<string>? ids = ProviderCatalog.IsAllModels(modelIds)
                ? ProviderCatalog.AllModelsFor(home, name)
                : modelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids is null)
                return new CommandResult.Error($"--model-ids {ProviderCatalog.AllModelsMarker} requires \"{name}\" to exist in the models.dev catalog");
            foreach (var modelId in ids)
                models[modelId] = new ProviderModelSettings { Name = modelId };
            if (models.Count == 0)
                return new CommandResult.Error($"--model-ids for \"{name}\" resolved to no models");
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
        var options = new HarnessOptions(home, Environment.CurrentDirectory);
        var result = ProviderRegistrar.RegisterProvider(ctx, options, name, provider, isDefault: true);
        if (result.Handle is null)
            return new CommandResult.Error($"provider \"{name}\" saved but not activated: {result.Error}");
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
