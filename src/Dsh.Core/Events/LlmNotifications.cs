using Dsh.Runtime.Events;

namespace Dsh.Core;

public sealed record LlmAdaptersUpdatedNotification : INotification
{
    public static string EventName => "llm/adapters-updated";
}
