using Dsh.Plugins;
using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Sdk;

namespace Dsh.Subagent;

internal static class SubagentCodecRegistration
{
    // 启动时由生成目录显式调用：子会话日志可能在 SubagentRuntime 构造之前被持久层读取/写入。
    [DshPluginInitializer]
    internal static void Register()
        => SessionEventCodec.Register<SubagentDescriptorPayload>(SubagentDescriptorPayload.EventType);
}
