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
            Description = "List, add, remove, disable, or enable DSH plugin modules",
            Input = new CommandInputDescriptor("list | add <pkg|path> | remove <pkg> [--force] | disable <pkg> | enable <pkg>"),
            Handler = invocation =>
            {
                var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var manager = ctx.Get<IPluginManager>("pluginManager", false);
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
                    case "remove" when tokens.Length > 1:
                        {
                            var force = tokens.Contains("--force", StringComparer.Ordinal);
                            var package = tokens.Skip(1).First(token => token != "--force");
                            return RunAsync(manager.RemoveAsync(package, force));
                        }
                    case "disable" when tokens.Length > 1:
                        return RunAsync(manager.DisableAsync(tokens[1]));
                    case "enable" when tokens.Length > 1:
                        return RunAsync(manager.EnableAsync(tokens[1]));
                    default:
                        return Task.FromResult<CommandResult>(
                            new CommandResult.Error("usage: /plugins list | doctor | add <pkg|path> | remove <pkg> [--force] | disable <pkg> | enable <pkg>"));
                }
            },
        });
    }

    private static string Ops => RuntimeFeature.IsDynamicCodeSupported
        ? "ops: list | doctor | add <pkg|path> | remove <pkg> [--force] | disable <pkg> | enable <pkg>"
        : "ops: list | doctor | add <native library path> | remove <pkg> [--force] | disable <pkg> | enable <pkg>";

    private static async Task<CommandResult> RunAsync(Task<string> operation)
    {
        var message = await operation;
        return new CommandResult.Success(message);
    }
}
