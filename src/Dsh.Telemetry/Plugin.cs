using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Agent;

[assembly: DshPlugin(Dsh.Telemetry.Plugin.Package)]

namespace Dsh.Telemetry;

/** 本地遥测:提供服务并把 agent 生命周期与错误事件写进 JSONL(默认 `home/telemetry.jsonl`)。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-telemetry";

    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        var resolved = TelemetryConfig.From(config, ctx);
        if (!resolved.Enabled)
            return new NoopDisposable();
        var service = new TelemetryService(ctx, resolved.Path);
        var options = new EventOptions { Global = true };
        var subscriptions = new List<Func<bool>>
        {
            ctx.On<AgentSessionStartNotification>(
                notification => service.AgentSessionStarted(notification.Agent.Id.Value, notification.Source), options),
            ctx.On<AgentDisposedNotification>(
                notification => service.AgentDisposed(notification.Agent.Id.Value), options),
            ctx.On<AgentErrorNotification>(
                notification => service.AgentFailed(
                    notification.Agent.Id.Value, notification.Turn, notification.Step, notification.Error), options),
        };
        return new Registration(service, subscriptions);
    }

    private sealed class Registration(TelemetryService service, List<Func<bool>> subscriptions) : IDisposable
    {
        public void Dispose()
        {
            foreach (var unsubscribe in subscriptions)
                unsubscribe();
            service.Dispose();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed class TelemetryConfig
{
    public const string DefaultFileName = "telemetry.jsonl";

    public bool Enabled { get; init; } = true;

    public string Path { get; init; } = "";

    public static TelemetryConfig From(object? config, Context ctx)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        var enabled = dict?.GetValueOrDefault("enabled") is not false;
        var configuredPath = dict?.GetValueOrDefault("path") as string;
        var paths = DshPaths.FromContext(ctx);
        var path = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : paths is null
                ? DefaultFileName
                : paths.TelemetryPath;
        return new TelemetryConfig { Enabled = enabled, Path = path };
    }
}
