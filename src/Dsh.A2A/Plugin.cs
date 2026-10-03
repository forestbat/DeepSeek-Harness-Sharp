using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin("@deepseek-ai/dsh-a2a")]

namespace Dsh.A2A;

public sealed class Plugin : IDshPlugin
{
    private const string EndpointKey = "a2aEndpoint";

    public string[] Inject => [AgentRegistry.ServiceName, SessionStore.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var home = (ctx.GetProp("harnessOptions") as HarnessOptions)?.Home ?? HarnessHome.Resolve();
        var settings = TryLoadSettings(ctx, home);
        if (settings?.A2a is not { Enabled: true } a2a)
            return new CallbackDisposable();
        var (provider, model) = ResolveDefaults(ctx, settings);
        var bridge = new A2aSessionBridge(ctx, provider, model);
        var options = new A2aServerOptions(a2a.Host ?? "127.0.0.1", a2a.Port, a2a.PublicUrl, a2a.AuthToken)
        {
            Skill = new A2aSkillOptions(
                a2a.SkillId ?? "coding",
                a2a.SkillName ?? "Coding",
                a2a.SkillDescription ?? "General software engineering assistance",
                a2a.SkillTags.Count > 0 ? a2a.SkillTags : null),
        };
        var listener = new A2aHttpListener(new A2aJsonRpc(bridge), options, ctx);
        listener.Start();
        ctx.Root.SetOwn(EndpointKey, listener.Endpoint);
        ctx.LoggerFor("a2a").Info($"a2a server listening on {listener.Endpoint}");
        return new CallbackDisposable(() =>
        {
            listener.Dispose();
            bridge.Dispose();
        });
    }

    private static HarnessSettings? TryLoadSettings(Context ctx, HarnessHome home)
    {
        try
        {
            return HarnessSettings.Load(home);
        }
        catch (Exception error)
        {
            ctx.LoggerFor("a2a").Warn($"failed to read a2a settings: {error.Message}");
            return null;
        }
    }

    private static (string? Provider, string? Model) ResolveDefaults(Context ctx, HarnessSettings settings)
    {
        if (ctx.GetProp("harnessOptions") is not HarnessOptions options)
            return (null, null);
        var fallback = settings.ResolveDefaultModel();
        return (options.Provider ?? fallback?.Provider, options.Model ?? fallback?.Model);
    }

    private sealed class CallbackDisposable(Action? dispose = null) : IDisposable
    {
        public void Dispose() => dispose?.Invoke();
    }
}
