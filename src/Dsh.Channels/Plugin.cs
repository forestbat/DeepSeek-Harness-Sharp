using Dsh.Agent;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Channels.Plugin.Package)]

namespace Dsh.Channels;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-channels";

    public string[] Inject => packageName switch
    {
        Package => [ToolRuntime.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public async Task<IDisposable?> ApplyAsync(Context ctx, object? config, CancellationToken cancellationToken = default)
        => packageName switch
        {
            Package => await ApplyChannelsAsync(ctx, config, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
        };

    public IDisposable Apply(Context ctx, object? config)
        => ApplyAsync(ctx, config).GetAwaiter().GetResult()!;

    private static async Task<IDisposable> ApplyChannelsAsync(Context ctx, object? config, CancellationToken cancellationToken)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        var channelsDir = dict?.GetValueOrDefault("channelsDir") as string;
        var router = new ExternalChannelRouter(ctx, channelsDirectory: channelsDir);
        ApplyConfigSecrets(router, dict);

        HttpClient CreateHttpClient() => new(OutboundProxy.FromContext(ctx).CreateHandler(), disposeHandler: true);
        var github = new WebhookChannelAdapter(router, CreateHttpClient);
        var slack = new SlackChannelAdapter(router, CreateHttpClient);
        var discord = new DiscordChannelAdapter(router, CreateHttpClient);

        var registrations = new List<IDisposable>
        {
            router.RegisterAdapter(github),
            router.RegisterAdapter(slack),
            router.RegisterAdapter(discord),
        };

        var listener = new ChannelWebhookListener(github, slack, discord);
        ctx.Provide("channelWebhookListener", listener);

        var endpoint = ResolveListenerEndpoint(dict);
        if (endpoint is { } target)
            await listener.StartListenerAsync(target.Port, target.Host, cancellationToken).ConfigureAwait(false);

        var tools = ChannelTools.Apply(ctx, router);
        var commands = ChannelCommands.Apply(ctx, router, listener);

        registrations.Add(commands);
        registrations.Add(tools);
        registrations.Add(listener);
        registrations.Add(router);
        registrations.Reverse();
        return new DisposeBundle(registrations);
    }

    private static void ApplyConfigSecrets(ExternalChannelRouter router, IReadOnlyDictionary<string, object?>? dict)
    {
        if (dict is null)
            return;
        if (dict.GetValueOrDefault("secrets") is IReadOnlyDictionary<string, object?> secretsMap)
        {
            foreach (var (key, value) in secretsMap)
            {
                if (value is string secretValue)
                    router.SetSecret(key, secretValue);
            }
        }
        if (dict.GetValueOrDefault("slackBotToken") is string slackBotToken)
            router.SetSecret("slack:botToken", slackBotToken);
        if (dict.GetValueOrDefault("discordBotToken") is string discordBotToken)
            router.SetSecret("discord:botToken", discordBotToken);
    }

    private static (string Host, int Port)? ResolveListenerEndpoint(IReadOnlyDictionary<string, object?>? dict)
    {
        if (dict is null)
            return null;
        if (dict.GetValueOrDefault("listenPrefix") is string prefix && Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
            return (uri.Host, uri.Port);
        var host = dict.GetValueOrDefault("listenHost") as string ?? "127.0.0.1";
        var port = dict.GetValueOrDefault("listenPort") switch
        {
            long longPort => (int)longPort,
            int intPort => intPort,
            _ => 0,
        };
        return port > 0 ? (host, port) : null;
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
