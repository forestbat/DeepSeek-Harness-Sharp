using Dsh.Runtime.Events;
using Dsh.Core;

namespace Dsh.Agent;

public sealed record SystemPromptAssembleNotification(PromptAssembly Assembly, AssembleContext Context) : INotification
{
    public static string EventName => "system-prompt/assemble";
}

public sealed record SystemPromptChangeNotification : INotification
{
    public static string EventName => "system-prompt/change";
}
