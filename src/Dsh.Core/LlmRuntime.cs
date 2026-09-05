using System.Runtime.CompilerServices;
using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record PreparedLlmCall(
    LlmCallConfig Config,
    ResolvedRetryPolicy RetryPolicy,
    LlmCallConfigAdapterDefaults AdapterDefaults,
    int? ContextWindow,
    Func<GenerateOptions, CancellationToken, IAsyncEnumerable<StreamChunk>> Stream);

public sealed class AdapterRegistrationHandle : IDisposable
{
    private Action? _dispose;

    internal AdapterRegistrationHandle(Action dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        _dispose?.Invoke();
        _dispose = null;
    }
}

public sealed class LlmRuntime : Service
{
    public const string ServiceName = "llm";
    public const string StreamEvent = "llm/stream";
    public const string AdaptersUpdatedEvent = "llm/adapters-updated";

    private sealed record Registration(LlmAdapter Adapter, LlmProviderInfo Provider, ResolvedRetryPolicy RetryPolicy);

    private readonly Dictionary<string, Registration> _adapters = [];

    public LlmRuntime(Context ctx) : base(ctx, ServiceName)
    {
    }

    public AdapterRegistrationHandle RegisterAdapter(IReadOnlyList<string> providers, LlmAdapter adapter)
    {
        foreach (var provider in providers)
        {
            if (_adapters.ContainsKey(provider))
                throw new InvalidOperationException($"provider \"{provider}\" already has a registered adapter");
            _adapters[provider] = new Registration(adapter, adapter.ProviderInfo, adapter.ProviderRetryPolicy);
        }
        Ctx.Emit(AdaptersUpdatedEvent);
        return new AdapterRegistrationHandle(() =>
        {
            foreach (var provider in providers)
                _adapters.Remove(provider);
            Ctx.Emit(AdaptersUpdatedEvent);
        });
    }

    public IReadOnlyList<LlmProviderInfo> ListProviders()
        => _adapters.Values.Select(registration => registration.Provider).DistinctBy(provider => provider.Id).ToList();

    public IReadOnlyList<LlmModelInfo> ListModels(string provider)
        => _adapters.TryGetValue(provider, out var registration)
            ? registration.Adapter.ListModels()
            : throw new LlmException(new LlmFailure($"no adapter registered for provider \"{provider}\"", LlmFailureCodes.NoAdapter));

    public LlmResolvedModelInfo ResolveModelInfo(string provider, string model)
    {
        var registration = RegistrationFor(provider);
        return registration.Adapter.ResolveModel(model) ?? new LlmResolvedModelInfo(provider, model, model);
    }

    private Registration RegistrationFor(string provider)
        => _adapters.TryGetValue(provider, out var registration)
            ? registration
            : throw new LlmException(new LlmFailure($"no adapter registered for provider \"{provider}\"", LlmFailureCodes.NoAdapter));

    public Task<PreparedLlmCall> PrepareCall(LlmCallConfig config, CancellationToken signal = default)
    {
        var registration = RegistrationFor(config.Provider);
        var adapterCall = registration.Adapter.PrepareCall(config.Model, signal);
        var modelInfo = registration.Adapter.ResolveModel(adapterCall.Model)
            ?? new LlmResolvedModelInfo(config.Provider, adapterCall.Model, adapterCall.Model);
        var resolved = ResolveCallWithInfo(config, modelInfo);
        var adapterDefaults = new LlmCallConfigAdapterDefaults(
            config.ReasoningEffort is null && resolved.ReasoningEffort is not null,
            config.MaxTokens is null && resolved.MaxTokens is not null);
        var dispatched = false;
        return Task.FromResult(new PreparedLlmCall(
            resolved,
            registration.RetryPolicy,
            adapterDefaults,
            modelInfo.ContextWindow,
            (options, ct) =>
            {
                if (dispatched)
                    throw new LlmException(new LlmFailure("a prepared LLM call can only be dispatched once", "INVALID_PREPARED_CALL"));
                if (!options.ToCallConfig().Equals(resolved))
                    throw new LlmException(new LlmFailure("prepared LLM call config changed before adapter dispatch", "INVALID_PREPARED_CALL"));
                dispatched = true;
                return StreamViaWaterfall(options, registration, resolved, adapterCall);
            }));
    }

    private LlmCallConfig ResolveCallWithInfo(LlmCallConfig config, LlmResolvedModelInfo info)
    {
        var defaulted = config.MaxTokens is null && info.DefaultMaxTokens is not null
            ? config with { MaxTokens = info.DefaultMaxTokens }
            : config;
        var reasoning = info.Reasoning;
        var requested = defaulted.ReasoningEffort;
        if (reasoning is null)
        {
            if (requested is not null)
            {
                throw new LlmException(new LlmFailure(
                    $"provider \"{config.Provider}\" model \"{config.Model}\" does not support reasoning effort \"{requested}\"",
                    "UNSUPPORTED_REASONING_EFFORT"));
            }
            return defaulted;
        }
        var effective = requested ?? reasoning.DefaultEffort;
        if (effective is null)
            return defaulted;
        if (!reasoning.Efforts.Any(effort => effort.Id == effective.Value))
        {
            throw new LlmException(new LlmFailure(
                $"provider \"{config.Provider}\" model \"{config.Model}\" does not support reasoning effort \"{effective}\"",
                "UNSUPPORTED_REASONING_EFFORT"));
        }
        return requested == effective ? defaulted : defaulted with { ReasoningEffort = effective };
    }

    public IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options)
        => StreamViaWaterfall(options, null, null, null);

    private async IAsyncEnumerable<StreamChunk> StreamViaWaterfall(
        GenerateOptions options,
        Registration? registration,
        LlmCallConfig? resolvedConfig,
        PreparedAdapterCall? adapterCall)
    {
        var result = await Ctx.Events.Waterfall(Ctx, StreamEvent, [options],
            () => new ValueTask<object?>(AdapterStream(options, registration, resolvedConfig, adapterCall)));
        if (result is not IAsyncEnumerable<StreamChunk> stream)
            throw new LlmException(new LlmFailure("llm/stream waterfall returned no stream", "INVALID_STREAM"));
        await foreach (var chunk in stream)
            yield return chunk;
    }

    private async IAsyncEnumerable<StreamChunk> AdapterStream(
        GenerateOptions options,
        Registration? prepared,
        LlmCallConfig? resolvedConfig,
        PreparedAdapterCall? adapterCall,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<StreamChunk>? iterator = null;
        StreamChunk? openFailure = null;
        try
        {
            var registration = prepared ?? RegistrationFor(options.Provider);
            var dispatch = adapterCall?.Stream ?? registration.Adapter.Stream;
            iterator = dispatch(options, options.Cancellation).GetAsyncEnumerator(options.Cancellation);
        }
        catch (Exception error)
        {
            openFailure = AdapterFailureChunk(error, options.Cancellation);
        }
        if (openFailure is not null)
        {
            yield return openFailure;
            yield break;
        }
        var completed = false;
        try
        {
            while (true)
            {
                StreamChunk? chunk = null;
                StreamChunk? iterationFailure = null;
                var done = false;
                try
                {
                    if (!await iterator!.MoveNextAsync())
                        done = true;
                    else
                        chunk = iterator.Current;
                }
                catch (Exception error)
                {
                    iterationFailure = AdapterFailureChunk(error, options.Cancellation);
                }
                if (done)
                {
                    completed = true;
                    yield break;
                }
                if (iterationFailure is not null)
                {
                    completed = true;
                    yield return iterationFailure;
                    yield break;
                }
                yield return chunk!;
            }
        }
        finally
        {
            if (!completed && iterator is not null)
                await iterator.DisposeAsync();
        }
    }

    private static StreamChunk AdapterFailureChunk(Exception error, CancellationToken signal)
    {
        var failure = NormalizeFailure(error);
        return new StreamChunk.Finish(
            signal.IsCancellationRequested || failure.Code == "ABORTED"
                ? new FinishReason.Aborted(failure)
                : new FinishReason.Error(failure));
    }

    internal static LlmFailure NormalizeFailure(Exception error)
        => error is LlmException llm
            ? llm.Failure
            : new LlmFailure(LlmFailureClassifiers.ErrorChain(error), "UNKNOWN");
}

public static class GenerateOptionsExtensions
{
    public static LlmCallConfig ToCallConfig(this GenerateOptions options)
        => new(options.Provider, options.Model, options.ReasoningEffort, options.Temperature, options.MaxTokens, options.Stop);
}
