using System.Runtime.CompilerServices;
using Dsh.Core;

namespace Dsh.Workflow;

internal static class ToolWorkflowCodecRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        SessionEventCodec.Register<ToolWorkflowRunStartPayload>(ToolWorkflowRunStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentStartPayload>(ToolWorkflowAgentStartPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowAgentEndPayload>(ToolWorkflowAgentEndPayload.EventType);
        SessionEventCodec.Register<ToolWorkflowRunEndPayload>(ToolWorkflowRunEndPayload.EventType);
    }
}