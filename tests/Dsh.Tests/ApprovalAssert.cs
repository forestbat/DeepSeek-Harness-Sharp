using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Agent;

namespace Dsh.Tests;

internal static class ApprovalAssert
{
    public static bool IsSettingsDiff(string? arguments)
        => ApprovalHints.SettingsDiff(arguments).Length != 0;

    public static IDisposable AnswerSettingsDiff(Context context, bool allowed)
    {
        var unsubscribe = context.OnWaterfall<ApprovalRequestNotification>((notification, _) =>
            ValueTask.FromResult<object?>(!IsSettingsDiff(notification.Request.Arguments) || allowed
                ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected), new EventOptions { Global = true });
        return new ApprovalSubscription(unsubscribe);
    }

    private sealed class ApprovalSubscription(Func<bool> unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
