#pragma warning disable CA2255
using System.Runtime.CompilerServices;
using Dsh.Core;

namespace Dsh.Workflow;

internal static class ToolWorkflowCodecRegistration
{
    // ReSharper disable once All
    [ModuleInitializer]
    internal static void Register()
    {
        SessionEventCodec.Register<ToolWorkflowRunStartPayload>(ToolWorkflowRunStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentStartPayload>(ToolWorkflowAgentStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentEndPayload>(ToolWorkflowAgentEndPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowRunEndPayload>(ToolWorkflowRunEndPayload.EventType);
    }
}