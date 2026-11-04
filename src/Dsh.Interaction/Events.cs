using Dsh.Core;
using Dsh.Runtime.Events;

namespace Dsh.Interaction;

public sealed record ApprovalRequestNotification(ApprovalRequest Request, CancellationToken Signal) : INotification
{
    public static string EventName => "approval/request";
}

public sealed record UserQuestionsRequestNotification(AskUserQuestionRequest Request, CancellationToken Signal) : INotification
{
    public static string EventName => "user-questions/request";
}

public sealed record CommandsChangedNotification : INotification
{
    public static string EventName => "commands/change";
}
