using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Subagent;

/**
 * 后台子代理的收尾: 跑完后把结果投回父会话并唤醒它, 同时(在有 job 控制器时)把状态回报给 job。
 *
 * 为什么要有这一步: 后台化的意义就是"父会话停了, 子代理还在跑"。子代理跑完如果没人叫醒父会话,
 * 成果等于白跑 —— 用户回来只会看到一条停住的会话。
 */
internal sealed class BackgroundRunDispatch
{
    private const string LogName = "subagent";
    private const string JobKind = "subagent";

    private readonly Context _ctx;
    private readonly IAgent _parent;
    private readonly SubagentRuntime _subagents;
    private readonly ISubagentRun _run;
    private readonly string _label;
    private readonly TaskCompletionSource<JobOutcome> _jobDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _lastOutput = "";

    private BackgroundRunDispatch(
        Context ctx, IAgent parent, SubagentRuntime subagents, ISubagentRun run, string label)
    {
        _ctx = ctx;
        _parent = parent;
        _subagents = subagents;
        _run = run;
        _label = label;
    }

    /** 有 job 控制器时返回 job id; 没有或登记失败时为 null(后台运行照旧进行)。 */
    public string? JobId { get; private set; }

    public static BackgroundRunDispatch Start(
        Context ctx, IAgent parent, SubagentRuntime subagents, ISubagentRun run, string label)
    {
        var dispatch = new BackgroundRunDispatch(ctx, parent, subagents, run, label);
        dispatch.JobId = dispatch.TryStartJob();
        _ = dispatch.CompleteAsync();
        return dispatch;
    }

    /**
     * job 只是可见性与可停止的载体: 组合里没有 job 控制器、或达到并发上限时, 后台运行照旧进行。
     * 让一个可选插件决定这条能力的有无, 就会退回到"契约在招徕、实际无条件失败"的老问题。
     */
    private string? TryStartJob()
    {
        if (_ctx.Get<JobsService>(JobsService.ServiceName, false) is not { } jobs)
            return null;
        try
        {
            return jobs.Start(new JobStart
            {
                Kind = JobKind,
                Label = _label,
                Owner = _parent,
                Run = () => new JobHooks(
                    _ =>
                    {
                        // 停止该后台子代理: 走控制面, 只影响这一个 run, 父会话不受牵连。
                        _subagents.CancelChild(_parent.Id, _run.Id);
                    },
                    _jobDone.Task,
                    () => _lastOutput),
            });
        }
        catch (Exception error)
        {
            _ctx.LoggerFor(LogName).Warn($"background subagent runs without a job: {error.Message}");
            return null;
        }
    }

    private async Task CompleteAsync()
    {
        SubagentResult? result = null;
        Exception? error = null;
        try
        {
            result = await _run.Result;
        }
        catch (Exception failure)
        {
            error = failure;
        }
        try
        {
            await _run.DisposeAsync();
        }
        catch (Exception failure)
        {
            error ??= failure;
        }
        _lastOutput = Describe(result, error);
        NotifyParent();
        _jobDone.TrySetResult(Outcome(result, error));
    }

    private void NotifyParent()
    {
        try
        {
            // Followup 会在父会话空闲时直接唤醒它跑下一回合 —— 这正是"跑完叫醒已停止的父会话"。
            _parent.Followup(BackgroundResultNotice.Create(_lastOutput));
        }
        catch (Exception error)
        {
            _ctx.LoggerFor(LogName).Warn($"background subagent could not notify its parent: {error.Message}");
        }
    }

    private JobOutcome Outcome(SubagentResult? result, Exception? error)
    {
        if (error is not null)
            return new JobOutcome(JobStatus.Failed, error.Message, _lastOutput);
        return result?.StopReason switch
        {
            SubagentStopReason.Completed => new JobOutcome(JobStatus.Completed, null, _lastOutput),
            SubagentStopReason.Aborted => new JobOutcome(JobStatus.Killed, "subagent run was cancelled", _lastOutput),
            _ => new JobOutcome(JobStatus.Failed, StopReasonText(result?.StopReason), _lastOutput),
        };
    }

    private string Describe(SubagentResult? result, Exception? error)
    {
        var headline = $"Background subagent finished: {_label}";
        if (error is not null)
            return $"{headline}\n\nit failed: {error.Message}";
        if (result is null)
            return $"{headline}\n\nit ended without a result.";
        var text = Text(result.Output);
        if (result.StopReason != SubagentStopReason.Completed)
        {
            return $"{headline}\n\nit ended abnormally: {StopReasonText(result.StopReason)}"
                + (text.Length == 0 ? "" : $"\n\npartial output: {text}");
        }
        return text.Length == 0 ? $"{headline}\n\nit returned no text." : $"{headline}\n\n{text}";
    }

    private static string StopReasonText(SubagentStopReason? stopReason) => stopReason switch
    {
        SubagentStopReason.MaxTokens => "it hit its token limit before finishing",
        SubagentStopReason.Refusal => "it declined the task",
        SubagentStopReason.Aborted => "it was cancelled",
        _ => "it failed",
    };

    private static string Text(IReadOnlyList<ContentBlock> output)
        => string.Concat(output.OfType<TextBlock>().Select(block => block.Text));
}
