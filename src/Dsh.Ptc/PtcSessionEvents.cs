using System.Text.Json;
using Dsh.Core;
using Dsh.Plugins;

namespace Dsh.Ptc;

public static class PtcEvents
{
    public const string DispatchStart = "tool/ptc-dispatch-start";
    public const string Dispatch = "tool/ptc-dispatch";
}

/** 程序内子派发开始: 记录工具名与参数快照, 供审计与重建。 */
public sealed record PtcDispatchStartPayload(
    string RunId,
    string ToolName,
    string CallId,
    JsonElement Arguments) : SessionEventPayload
{
    public override string Type => PtcEvents.DispatchStart;
}

/** 程序内子派发结束: 记录结果内容或错误, 不进模型上下文。 */
public sealed record PtcDispatchPayload(
    string RunId,
    string ToolName,
    string CallId,
    bool IsError,
    JsonElement? Result,
    string? Error) : SessionEventPayload
{
    public override string Type => PtcEvents.Dispatch;
}

internal static class PtcCodecRegistration
{
    [DshPluginInitializer]
    internal static void Register()
    {
        SessionEventCodec.Register<PtcDispatchStartPayload>(PtcEvents.DispatchStart);
        SessionEventCodec.Register<PtcDispatchPayload>(PtcEvents.Dispatch);
    }
}
