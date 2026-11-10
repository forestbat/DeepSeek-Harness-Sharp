using Dsh.Boot;
using Dsh.Runtime;
using Dsh.Plugins;
using System.Runtime.CompilerServices;

namespace Dsh.Interaction;

public static class PluginCommand
{
    public static IDisposable Register(Context ctx, PluginCatalog catalog)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false)!;
        return commands.Register(new CommandDefinition
        {
            Name = "plugins",
            Description = "List, add, remove, disable, or enable DSH plugin modules and set their read, write, or network permissions",
            Input = new CommandInputDescriptor($"list | add <pkg|path> | {PackageOps}"),
            Subcommands = MenuSubcommands,
            Handler = invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var manager = ctx.Get<HarnessPluginManager>("pluginManager", false);
                if (tokens.Length == 0 || tokens[0] == "list")
                {
                    var names = (manager?.PackageNames ?? catalog.PackageNames)
                        .Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal);
                    var lines = names.Select(name => manager is null ? name : $"{name}: {manager.Describe(name)}");
                    var failures = manager?.LoadFailures ?? [];
                    var failed = failures.Count == 0
                        ? ""
                        : $"\n加载失败 ({failures.Count}):\n" + string.Join('\n',
                            failures.Select(skip => $"  {Path.GetFileName(skip.File)}: {skip.Reason}"));
                    return Task.FromResult<CommandResult>(
                        new CommandResult.Success($"{string.Join('\n', lines)}{failed}\n{Ops}"));
                }
                if (tokens[0] == "doctor")
                {
                    var failures = manager?.LoadFailures ?? [];
                    var report = failures.Count == 0
                        ? "plugins doctor: 未发现跳过或装载失败的制品"
                        : "plugins doctor: 发现 " + failures.Count + " 个跳过/失败制品\n"
                            + string.Join('\n', failures.Select(skip => $"  {skip.File}: {skip.Reason}"));
                    return Task.FromResult<CommandResult>(new CommandResult.Success(
                        report + "\n排查顺序: 导出符号(dsh_plugin_package/dsh_plugin_entry) → ABI 版本 → 是否 NativeLib=Shared 产物;"
                        + "运行期装载仅支持原生共享库(.so/.dylib,或 Windows 下的原生 .dll)"));
                }
                if (manager is null)
                {
                    return Task.FromResult<CommandResult>(
                        new CommandResult.Error("plugin manager is not available"));
                }
                switch (tokens[0])
                {
                    case "add" when tokens.Length > 1:
                        return RunAsync(manager.AddAsync(string.Join(' ', tokens.Skip(1)).Trim()));
                    case "remove" when tokens.Skip(1).FirstOrDefault(token => token != "--force") is { } package:
                        return RunAsync(manager.RemoveAsync(package, tokens.Contains("--force", StringComparer.Ordinal)));
                    case "disable" when tokens.Length > 1:
                        return RunAsync(manager.DisableAsync(tokens[1]));
                    case "enable" when tokens.Length > 1:
                        return RunAsync(manager.EnableAsync(tokens[1]));
                    case BanRead or EnableRead when tokens.Length > 1:
                        return RunAsync(manager.SetPermissionAsync(tokens[1], PluginPermission.Read, allowed: tokens[0] == EnableRead));
                    case BanWrite or EnableWrite when tokens.Length > 1:
                        return RunAsync(manager.SetPermissionAsync(tokens[1], PluginPermission.Write, allowed: tokens[0] == EnableWrite));
                    case BanNetwork or EnableNetwork when tokens.Length > 1:
                        return RunAsync(manager.SetPermissionAsync(tokens[1], PluginPermission.Network, allowed: tokens[0] == EnableNetwork));
                    default:
                        return Task.FromResult<CommandResult>(
                            new CommandResult.Error($"usage: /plugins list | doctor | add <pkg|path> | {PackageOps}"));
                }
            },
        });
    }

    private const string BanRead = "ban_read";
    private const string EnableRead = "enable_read";
    private const string BanWrite = "ban_write";
    private const string EnableWrite = "enable_write";
    private const string BanNetwork = "ban_network";
    private const string EnableNetwork = "enable_network";
    private const string PackageOps = $"remove <pkg> [--force] | disable <pkg> | enable <pkg> | {BanRead} <pkg> | {EnableRead} <pkg> | {BanWrite} <pkg> | {EnableWrite} <pkg> | {BanNetwork} <pkg> | {EnableNetwork} <pkg>";

    private static string Ops => RuntimeFeature.IsDynamicCodeSupported
        ? $"ops: list | doctor | add <pkg|path> | {PackageOps}"
        : $"ops: list | doctor | add <native library path> | {PackageOps}";

    /** The command overlay's subcommand menu; the package argument is completed from the plugin manager by the front end. */
    private static IReadOnlyList<CommandDescriptor> MenuSubcommands
    {
        get
        {
            var package = new CommandArgumentSchema(PackageArgument, "select", "Plugin package", Required: true);
            return
            [
                new CommandDescriptor("list", "List plugins and their state"),
                new CommandDescriptor("doctor", "Explain skipped or failed plugin artifacts"),
                new CommandDescriptor("add", "Install a plugin by package name or library path",
                    ArgumentSchemas: [new CommandArgumentSchema("package-or-path", "text", "Package name or plugin library path", Required: true)]),
                new CommandDescriptor("remove", "Uninstall a plugin", ArgumentSchemas: [package]),
                new CommandDescriptor("disable", "Disable a plugin", ArgumentSchemas: [package]),
                new CommandDescriptor("enable", "Enable a plugin", ArgumentSchemas: [package]),
                new CommandDescriptor(BanRead, "Deny reads through the sandbox broker", ArgumentSchemas: [package]),
                new CommandDescriptor(EnableRead, "Allow reads through the sandbox broker", ArgumentSchemas: [package]),
                new CommandDescriptor(BanWrite, "Deny writes through the sandbox broker", ArgumentSchemas: [package]),
                new CommandDescriptor(EnableWrite, "Allow writes through the sandbox broker", ArgumentSchemas: [package]),
                new CommandDescriptor(BanNetwork, "Deny network access through the sandbox broker", ArgumentSchemas: [package]),
                new CommandDescriptor(EnableNetwork, "Allow network access through the sandbox broker", ArgumentSchemas: [package]),
            ];
        }
    }

    /** Argument schema name whose candidates are the installed plugin packages. */
    public const string PackageArgument = "package";

    private static async Task<CommandResult> RunAsync(Task<string> operation)
    {
        var message = await operation;
        return new CommandResult.Success(message);
    }
}
