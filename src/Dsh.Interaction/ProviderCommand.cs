using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dsh.Interaction;

public static class ProviderCommand
{
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
                    "catalog" => ListCatalog(home, OutboundProxy.FromContext(ctx), tokens[1..], invocation.Signal),
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
    private static async Task<CommandResult> ListCatalog(HarnessHome home, OutboundProxy proxy, string[] args, CancellationToken signal)
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

        var snapshot = await ProviderCatalog.LoadAsync(home, proxy, refresh, signal);
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
            // 有效模型(声明 ∪ 目录 ∪ 端点自报): 后两者都不在 settings 里, 只看 Models.Count 会把它们漏掉。
            var effective = ProviderCatalog.EffectiveModels(llm, entry.Key, entry.Value);
            var models = effective.Count > 0 ? $" models=[{string.Join(',', effective)}]" : "";
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
        settings.Providers.Remove(name);
        settings.Save(home);
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
        var (models, error) = await DeclareModelsAsync(home, OutboundProxy.FromContext(ctx), name, baseUrl, apiKey, modelIds);
        if (error is { Length: > 0 })
            return new CommandResult.Error(error);
        var effective = ProviderCatalog.EffectiveModels(name, baseUrl, models.Keys);
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
        settings.GlobalDefaultModel ??= effective.Count > 0 ? $"{name}/{effective[0]}" : null;
        settings.Providers[name] = provider;
        settings.Save(home);
        var options = new HarnessOptions(home, Environment.CurrentDirectory);
        var result = ProviderRegistrar.RegisterProvider(ctx, options, name, provider, isDefault: true);
        if (result.Handle is null)
            return new CommandResult.Error($"provider \"{name}\" saved but not activated: {result.Error}");
        var fromCatalog = effective.Count - models.Count;
        var catalogNote = fromCatalog > 0 ? $" (+{fromCatalog} from the models.dev catalog, not written to settings.yaml)" : "";
        return new CommandResult.Success($"added provider \"{name}\" with {models.Count} model(s){catalogNote}");
    }

    /**
     * 要落盘的模型条目: `--model-ids` 给定时用它(或 <all> 展开目录), 否则打端点 /models 自动发现。
     * models.dev 已收录的模型跳过不写(目录是唯一真相, settings 里只留目录之外的追加/覆盖)。
     */
    private static async Task<(Dictionary<string, ProviderModelSettings> Models, string? Error)> DeclareModelsAsync(
        HarnessHome home,
        OutboundProxy proxy,
        string name,
        string baseUrl,
        string apiKey,
        string? modelIds)
    {
        IReadOnlyList<string>? ids = null;
        if (modelIds is not null)
        {
            ids = ProviderCatalog.IsAllModels(modelIds)
                ? ProviderCatalog.AllModelsFor(home, name)
                : modelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids is null)
                return ([], $"--model-ids {ProviderCatalog.AllModelsMarker} requires \"{name}\" to exist in the models.dev catalog");
            if (ids.Count == 0)
                return ([], $"--model-ids for \"{name}\" resolved to no models");
        }
        else
        {
            try
            {
                ids = [.. (await FetchModelsAsync(proxy, baseUrl, apiKey)).Keys];
            }
            catch (Exception failure)
            {
                return ([], $"model auto-discovery failed for \"{name}\": {failure.Message}");
            }

            if (ids.Count == 0)
                return ([], $"model auto-discovery for \"{name}\" returned no models");
        }

        var known = new HashSet<string>(ModelsDevCatalog.Embedded.ModelIdsFor(name, baseUrl), StringComparer.Ordinal);
        var models = new Dictionary<string, ProviderModelSettings>();
        foreach (var modelId in ids)
        {
            if (!known.Contains(modelId))
                models[modelId] = new ProviderModelSettings { Name = modelId };
        }

        return (models, null);
    }

    private static async Task<Dictionary<string, ProviderModelSettings>> FetchModelsAsync(OutboundProxy proxy, string baseUrl, string apiKey)
    {
        using var http = new HttpClient(proxy.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request);
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
