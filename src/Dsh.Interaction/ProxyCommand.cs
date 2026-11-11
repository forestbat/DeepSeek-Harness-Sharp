using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Interaction;

/** /proxy: 查看、写回并热更新本机出口; test 与 GUI 共用自检; reload 重新读取 settings.yaml。 */
public static class ProxyCommand
{
    private const string Usage = "usage: /proxy [set <proxy-url> | clear | test [probe-url] | reload | via <machine> | direct]";

    public static IDisposable Register(Context ctx, HarnessHome home)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "proxy",
            Description = "Show/save the local outbound proxy; test checks connectivity, reload applies settings.yaml",
            Input = new CommandInputDescriptor("set <proxy-url> | clear | test [probe-url] | reload | via <machine> | direct"),
            Handler = invocation => RunAsync(ctx, OutboundProxy.FromContext(ctx), home, invocation),
        });
    }

    private static async Task<CommandResult> RunAsync(Context ctx, OutboundProxy proxy, HarnessHome home, CommandInvocation invocation)
    {
        var network = HarnessSettings.Load(home).Network;
        var settings = network?.OutboundProxy ?? new OutboundProxySettings();
        var configured = string.IsNullOrWhiteSpace(settings.Url) ? "留空(跟随系统代理)" : OutboundProxy.Redact(settings.Url);
        switch (invocation.RawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            case []:
                return new CommandResult.Success($"本机出口代理: 当前生效 {proxy.Describe()}; settings.yaml network.outbound_proxy.url: {configured}");
            case ["reload"]:
                if (ctx.Get<IOutboundRouteController>(IOutboundRouteController.ServiceName, false) is { } reloadController)
                {
                    await reloadController.ReloadAsync(invocation.Signal);
                    return new CommandResult.Success($"Local outbound settings reloaded: {proxy.Describe()}");
                }
                if (network?.BorrowedOutboundMachine is not null)
                {
                    proxy.SuspendBorrowed();
                    return new CommandResult.Error("The saved borrowed selection requires the outbound route controller.");
                }
                return Reload(proxy, settings);
            case ["via", _, ..]:
                return await SelectAsync(ctx, proxy, invocation.RawInput.AsSpan().Trim()["via".Length..].Trim().ToString(), invocation.Signal);
            case ["direct"]:
                return await SelectAsync(ctx, proxy, null, invocation.Signal);
            case ["set", var url]:
                return Save(proxy, home, url);
            case ["clear"]:
                return Save(proxy, home, null);
            case ["test", .. var rest] when rest.Length <= 1:
                settings.ProbeUrl = rest is [var probeUrl] ? probeUrl : settings.ProbeUrl;
                var result = await OutboundProxyProbe.RunAsync(settings, invocation.Signal);
                return result.Reachable ? new CommandResult.Success(result.Message) : new CommandResult.Error(result.Message);
            default:
                return new CommandResult.Error(Usage);
        }
    }

    private static async Task<CommandResult> SelectAsync(Context ctx, OutboundProxy proxy, string? machine, CancellationToken cancellationToken)
    {
        var controller = ctx.Get<IOutboundRouteController>(IOutboundRouteController.ServiceName, false);
        if (controller is null)
            return new CommandResult.Error("The outbound route controller is unavailable.");
        try
        {
            await controller.SelectAsync(machine, cancellationToken);
            return new CommandResult.Success($"Local outbound selection saved: {proxy.Describe()}");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return new CommandResult.Error(error.Message);
        }
    }

    private static CommandResult Save(OutboundProxy proxy, HarnessHome home, string? url)
    {
        var outbound = HarnessSettings.Load(home).Network?.OutboundProxy ?? new OutboundProxySettings();
        outbound.Url = url;
        try
        {
            HarnessSettings.SaveOutboundProxy(home, outbound, proxy);
        }
        catch (ArgumentException error)
        {
            return new CommandResult.Error(error.Message);
        }
        return new CommandResult.Success($"本机出口代理已保存并生效: {proxy.Describe()}");
    }

    private static CommandResult Reload(OutboundProxy proxy, OutboundProxySettings settings)
    {
        try
        {
            proxy.Configure(settings.Url, settings.Bypass);
            proxy.ConfigureBorrowed(null, null);
            return new CommandResult.Success($"本机出口代理已应用: {proxy.Describe()}");
        }
        catch (ArgumentException error)
        {
            return new CommandResult.Error(error.Message);
        }
    }
}
