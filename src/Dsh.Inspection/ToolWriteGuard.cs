using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Inspection;

/**
 * 把工具调用接到 SettingsFileGuard 上: 这是守卫唯一需要 Dsh.Core 的地方。
 * 不认工具名 —— 任何工具跑完只要 settings.yaml 变了就校验, 所以 MCP 工具(rider 的 apply_patch)、
 * 插件工具和 bash 重定向都覆盖; 唯一让步是坏字节会短暂落盘(同一轮工具调用内撤销)。
 */
public static class ToolWriteGuard
{
    internal static IDisposable Register(Context ctx)
    {
        if ((ctx.GetProp(ToolSettings.HomeProperty) as HarnessOptions)?.Home is not { } home)
            return new ActionDisposable(static () => { });
        var guard = new SettingsFileGuard(home);
        guard.Accept();
        var pre = ctx.OnWaterfall<ToolPreExecuteNotification>(
            (_, next) => { guard.Capture(); return next(); },
            new EventOptions { Global = true });
        // PostExecute 可替换结果: 让模型当场知道自己的写入被撤销。抛出异常的工具走不到这里, 由下方兜住。
        var post = ctx.OnWaterfall<ToolPostExecuteNotification>(
            async (_, next) =>
            {
                var decision = await next();
                return guard.Check() is { } message ? new PostToolDecision.Block([new TextBlock(message)]) : decision;
            },
            new EventOptions { Global = true });
        var result = ctx.On<ToolResultNotification>(_ => guard.Check(), new EventOptions { Global = true });
        return new DisposableBundle(
            new ActionDisposable(() => pre()),
            new ActionDisposable(() => post()),
            new ActionDisposable(() => result()));
    }
}
