using System.Text;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Interaction;

/** provider 注册(交互插件持有):读 settings.yaml(CLI 覆盖仅作用于默认 provider),按 `type` 查适配器工厂;
 *  缺 key、未知类型、缺 baseUrl 都记 WARN 并跳过;适配器插件只登记工厂,宿主不含 provider 语义。
 *  适配器工厂集合变化(插件启用/停用)时重新同步。 */
public sealed class ProviderRegistrar : IDisposable
{
    /** settings.yaml 缺省的类型:绝大多数厂商只写 baseUrl/apiKey 即可;DeepSeek 专属行为需显式 `type: deepseek`。 */
    public const string DefaultWire = "openai-compatible";

    private readonly Context _ctx;
    private readonly HarnessOptions _options;
    private readonly HarnessSettings _settings;
    private readonly LlmAdapterFactoryRegistry _factories;
    private readonly List<IDisposable> _handles = [];
    private readonly Func<bool> _unsubscribeFactoryUpdates;
    private readonly Func<bool> _unsubscribeReady;
    private bool _ready;

    private ProviderRegistrar(
        Context ctx,
        HarnessOptions options,
        HarnessSettings settings,
        LlmAdapterFactoryRegistry factories)
    {
        _ctx = ctx;
        _options = options;
        _settings = settings;
        _factories = factories;
        _unsubscribeFactoryUpdates = ctx.On<LlmAdapterFactoriesUpdatedNotification>(_ => Sync());
        _unsubscribeReady = ctx.On<CompositionReadyNotification>(_ =>
        {
            _ready = true;
            Sync();
        });
    }

    public static ProviderRegistrar? Register(Context ctx, HarnessOptions options)
    {
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        var credentials = ctx.GetProp("credentials") as ICredentials;
        if (llm is null || factories is null || credentials is null)
            return null;
        var registrar = new ProviderRegistrar(ctx, options, HarnessSettings.Load(options.Home), factories);
        registrar.Sync();
        return registrar;
    }

    private void Sync()
    {
        if (!_ready)
            return;   // 组合未完成:适配器插件可能还没登记工厂,等 CompositionReady 后再首次同步,避免误报
        foreach (var handle in _handles)
            handle.Dispose();
        _handles.Clear();

        var defaultProvider = _options.Provider ?? _settings.ResolveDefaultModel()?.Provider ?? "";
        var providers = new Dictionary<string, ProviderSettings>(_settings.Providers, StringComparer.OrdinalIgnoreCase);
        if (_options.Provider is { Length: > 0 } cliProvider && !providers.ContainsKey(cliProvider))
            providers[cliProvider] = new ProviderSettings();
        var served = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, provider) in providers)
        {
            var isDefault = string.Equals(id, defaultProvider, StringComparison.OrdinalIgnoreCase);
            var result = RegisterProvider(_ctx, _options, id, provider, isDefault);
            if (result.Handle is null)
            {
                _ctx.Logger.Warn("%s", result.Error);
                continue;
            }
            _handles.Add(result.Handle);
            if (result.Wire is not null)
                served.Add(result.Wire);
        }

        foreach (var (source, factory) in _factories.ListAll())
        {
            // 整个插件都没被任何 provider 用到才算"缺配置待命";只是某个 wire 没人用不算。
            var idle = factory.Wires
                .Where(definition => !served.Contains(definition.Wire))
                .Select(definition => definition.Wire)
                .ToList();
            if (idle.Count == factory.Wires.Count)
            {
                _ctx.Logger.Warn("%s",
                    $"adapter plugin \"{source}\" serves [{string.Join(", ", idle)}] but no matching provider is configured; it stays idle");
            }
        }
    }

    /** 单个 provider 条目的注册结果:句柄、命中的 wire 与来源插件,或失败原因。 */
    public sealed record ProviderRegistrationResult(
        IDisposable? Handle,
        string? Wire = null,
        string? Source = null,
        string? Error = null);

    /** 按 settings.yaml 的一个 provider 条目登记适配器;CLI 覆盖仅作用于默认 provider。 */
    public static ProviderRegistrationResult RegisterProvider(
        Context ctx,
        HarnessOptions options,
        string id,
        ProviderSettings provider,
        bool isDefault)
    {
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        var credentials = ctx.GetProp("credentials") as ICredentials;
        if (llm is null || factories is null || credentials is null)
            return new ProviderRegistrationResult(null, Error: "LLM runtime or adapter factory registry is not available");

        var wire = string.IsNullOrWhiteSpace(provider.Type) ? DefaultWire : provider.Type.Trim();
        if (!factories.TryResolve(wire, out var source, out var factory))
        {
            return new ProviderRegistrationResult(null, Wire: wire,
                Error: $"provider \"{id}\" skipped: no adapter plugin serves type \"{wire}\" (known: {KnownWires(factories)})");
        }
        var definition = factories.DefinitionFor(wire);
        var baseUrl = (isDefault ? options.BaseUrl : null)
            ?? provider.Options?.BaseUrl
            ?? definition?.DefaultBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ProviderRegistrationResult(null, Wire: wire, Source: source,
                Error: $"provider \"{id}\" skipped: baseUrl is not configured for type \"{wire}\"");
        }
        var apiKeyEnv = (isDefault ? options.ApiKeyEnv : null)
            ?? provider.Options?.ApiKeyEnv
            ?? definition?.DefaultApiKeyEnv
            ?? $"{EnvironmentName(id)}_API_KEY";
        var apiKey = (isDefault ? options.ApiKey : null) ?? provider.Options?.ApiKey;
        var resolvedKey = string.IsNullOrWhiteSpace(apiKey) ? credentials.Get(apiKeyEnv) : apiKey;
        if (string.IsNullOrWhiteSpace(resolvedKey) && !AccountCovers(ctx, baseUrl))
        {
            return new ProviderRegistrationResult(null, Wire: wire, Source: source,
                Error: $"provider \"{id}\" skipped: API key is not configured "
                    + $"(set providers.{id}.options.apiKey or environment variable {apiKeyEnv}); requests using it will fail with NO_ADAPTER");
        }
        var resolved = new ResolvedLlmProvider(
            id,
            wire,
            baseUrl,
            apiKeyEnv,
            resolvedKey,
            provider.Models.Select(model => new ProviderModelSpec(
                model.Key, model.Value.Name, model.Value.SystemPromptUpdate, model.Value.Reasoning == true)).ToList());
        return new ProviderRegistrationResult(llm.RegisterAdapter([id], factory!.Create(resolved)), wire, source);
    }

    /** 没配 API key 时, 账号凭据源能覆盖该 baseUrl 的 origin 就照常注册, 令牌在请求时按 origin 取。 */
    private static bool AccountCovers(Context ctx, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var destination))
            return false;
        return ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false)?.Covers(destination) is true;
    }

    /** 当前可供 `type` 使用的 wire 列表(未知类型报错时提示用户)。 */
    public static string KnownWires(LlmAdapterFactoryRegistry factories)
    {
        var wires = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, factory) in factories.ListAll())
        {
            foreach (var definition in factory.Wires)
                wires.Add(definition.Wire);
        }
        return wires.Count == 0 ? "none" : string.Join(", ", wires);
    }

    private static string EnvironmentName(string providerId)
    {
        var builder = new StringBuilder(providerId.Length);
        foreach (var ch in providerId)
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
        return builder.ToString();
    }

    public void Dispose()
    {
        _unsubscribeFactoryUpdates();
        _unsubscribeReady();
        foreach (var handle in _handles)
            handle.Dispose();
        _handles.Clear();
    }
}
