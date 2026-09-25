using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Account.Plugin.Account)]

namespace Dsh.Account;

/** 账号插件入口: 只注册账号服务, 配置来自本库插件参数段。 */
public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Account = "@deepseek-ai/dsh-account";

    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
        => string.Equals(packageName, Account, StringComparison.Ordinal)
            ? Create(ctx, config)
            : throw new InvalidOperationException($"Unknown DSH package '{packageName}'.");

    private static AccountService Create(Context ctx, object? config)
    {
        var homePath = ctx.GetProp("dshHomePath") as string;
        var home = homePath is { Length: > 0 } ? new HarnessHome(homePath) : HarnessHome.Resolve();
        return new AccountService(ctx, AccountConfig.From(config), home);
    }
}
