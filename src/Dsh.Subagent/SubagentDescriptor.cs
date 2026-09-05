using System.Runtime.CompilerServices;
using Dsh.Core;

namespace Dsh.Subagent;

public sealed record SubagentDescriptorPayload(int Version, string Mode, string Provider, string? Label = null)
    : SessionEventPayload
{
    public const string EventType = "subagent/descriptor";
    public const int CurrentVersion = 3;
    public const string OneShotMode = "one-shot";
    public const string ContinuableMode = "continuable";

    public override string Type => EventType;

    public static SubagentDescriptorPayload OneShot(string provider, string? label)
        => new(CurrentVersion, OneShotMode, provider, label);

    // 提供者每次激活只向子会话 own 后缀追加一个 descriptor；own 后缀里首个即为身份（fork 种子里的祖先 descriptor 不参与）。
    public static SubagentDescriptorPayload? IdentityOf(Session session)
    {
        foreach (var sessionEvent in session.OwnEvents())
        {
            if (sessionEvent.Data is SubagentDescriptorPayload payload)
                return payload.Version == CurrentVersion ? payload : null;
        }
        return null;
    }
}

internal static class SubagentCodecRegistration
{
    // 程序集加载即注册：子会话日志可能在 SubagentRuntime 构造之前被持久层读取/写入。
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
        => SessionEventCodec.Register<SubagentDescriptorPayload>(SubagentDescriptorPayload.EventType);
}
