using Dsh.Core;
using Dsh.Interaction;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Host;

/**
 * harness 的审批/问答瀑布接到 host 的待决策队列: 远端发起, 连到 host 的任一客户端回答, 先答者生效。
 * 请求按根会话归属(客户端只认识根会话), 并以发起它的 agent 为 Scope, 该 agent 回到 Idle 时只取消它自己的请求。
 */
internal sealed class HarnessDecisionBridge : IDisposable
{
    private readonly HostPendingDecisions _pending;
    private readonly Func<bool> _approvalBridge;
    private readonly Func<bool> _questionBridge;

    public HarnessDecisionBridge(Context ctx, HostPendingDecisions pending)
    {
        _pending = pending;
        _approvalBridge = ctx.OnWaterfall<ApprovalRequestNotification>(BridgeApprovalAsync, new EventOptions { Global = true });
        _questionBridge = ctx.OnWaterfall<UserQuestionsRequestNotification>(BridgeQuestionAsync, new EventOptions { Global = true });
    }

    private async ValueTask<object?> BridgeApprovalAsync(ApprovalRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var request = notification.Request;
        var response = await _pending.AskApprovalAsync(
            new RemoteApprovalRequest(NewRequestId(), RootSessionId(request.Agent), request.ToolName, request.Arguments, request.Reason,
                ApprovalHints.Impact(request.ToolName, request.Arguments)),
            request.Agent.Id.Value, notification.Signal).ConfigureAwait(false);
        if (response is null || response.Cancelled)
            return ApprovalOutcome.Cancelled;
        return !response.Allow ? ApprovalOutcome.Rejected
            : response.RememberForSession ? ApprovalOutcome.AllowedForSession : ApprovalOutcome.AllowedOnce;
    }

    private async ValueTask<object?> BridgeQuestionAsync(UserQuestionsRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var request = notification.Request;
        var items = request.Questions
            .Select(question => new RemoteQuestionItem(
                question.Id,
                question.Question,
                question.Detail,
                question.Header,
                [.. (question.Options ?? []).Select(option => new RemoteQuestionOption(option.Label, option.Description, option.Value, option.Disabled, option.Default))],
                question.MultiSelect,
                question.Intent?.Approve,
                question.AllowCustom))
            .ToList();
        var sessionId = request.Agent is { } agent ? RootSessionId(agent) : "";
        var response = await _pending.AskQuestionAsync(new RemoteQuestionRequest(NewRequestId(), sessionId, items), request.Agent?.Id.Value, notification.Signal).ConfigureAwait(false)
            ?? throw new UserQuestionException("ask_user_question was cancelled before any client answered", UserQuestionException.AskAborted);
        return response.Status == "dismissed" ? AskUserQuestionAnswer.Dismissed
            : new AskUserQuestionAnswer([.. response.Answers.Select(answer => new AskUserQuestionAnswerItem(answer.Id, answer.Selected, answer.Custom))], response.Status, response.Reason);
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private static string RootSessionId(IAgent agent) => (agent.Session.Header.RootSession ?? agent.Id).Value;

    public void Dispose()
    {
        _approvalBridge();
        _questionBridge();
    }
}
