using Avalonia.Headless;

namespace Dsh.Tests;

/** AvaloniaFact 的自托管替代: 在 headless 会话的 UI 线程上异步执行测试体。 */
/** Avalonia.Headless.XUnit 12.1.2 依赖 xunit.v3.extensibility.core 3.2.2, 与本仓库 xunit.v3 4.0.1 不兼容(发现阶段 MissingMethodException), 故自行实现等价集成。 */
internal static class HeadlessGui
{
    /**
     * 外层兜底必须比用例内部的等待保护更宽松(GuiTestEnvironment.PumpAsync 是 30s)。
     * 外层先超时会把"本来能报出条件不满足"的慢用例压成一句干瘪的 TimeoutException, 且让调度线程停在半路。
     */
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private static readonly Lock Gate = new();

    private static HeadlessUnitTestSession? _sharedSession;

    /** 整个进程只跑一条 headless 调度线程; 所有用例经 GuiSerialCollection 串行, 不会并发进队列。 */
    private static HeadlessUnitTestSession AcquireSession()
    {
        lock (Gate)
        {
            return _sharedSession ??= HeadlessUnitTestSession.StartNew(
                typeof(GuiHeadlessAppBuilder),
                AvaloniaTestIsolationLevel.PerTest);
        }
    }

    /**
     * 把"测试体没跑完"的会话换掉, 让后续用例拿到新的调度线程。
     * HeadlessUnitTestSession 只有一条线程按队列顺序执行, 遇到异步体就 PushFrame 泵消息直到它完成;
     * 一旦某个测试体永不结束, 该线程就永远停在那次 PushFrame 里, 之后入队的项再也不会被取出 ——
     * 表现为第一个卡住的用例报超时, 其后每个用例都跟着超时, 整批验证结果不可信。
     */
    private static void DiscardIfUnsettled(HeadlessUnitTestSession active, Task dispatched)
    {
        if (dispatched.IsCompleted)
            return;

        lock (Gate)
        {
            if (!ReferenceEquals(_sharedSession, active))
                return;
            _sharedSession = null;
        }

        // 卡住的会话要 await 一条永不结束的调度线程, 因此不能同步 Dispose; 丢给线程池自行了结。
        _ = Task.Run(async () => await active.DisposeAsync().ConfigureAwait(false));
    }

    public static Task RunAsync(Action test)
        => RunAsync(() =>
        {
            test();
            return Task.CompletedTask;
        });

    /**
     * 异步执行测试体并 await, 不回退为同步阻塞。
     * Dispatch 的 Task 在 headless dispatcher 线程上完成; 若把它的续体交给 xunit 直接在 dispatcher 线程上内联执行,
     * xunit 的测试收尾逻辑会占住该线程, 此后任何 Dispatch 都永不返回(整套测试挂死)。
     * 故经 RunContinuationsAsynchronously 的桥接 Task 把消费方续体甩回线程池。
     * timeout 只给"要跑整段真实会话矩阵"的用例使用: 默认值会把这些用例判成超时而不是失败。
     */
    public static async Task RunAsync(Func<Task> test, TimeSpan? timeout = null)
    {
        var active = AcquireSession();
        var dispatched = active.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);
        try
        {
            await BridgeToPool(dispatched).WaitAsync(timeout ?? Timeout, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            DiscardIfUnsettled(active, dispatched);
            throw;
        }
    }

    /** 续体调度到线程池: 桥接 Task 完成于 dispatcher 线程, 但 RunContinuationsAsynchronously 保证等待方在线程池上继续。 */
    private static Task BridgeToPool(Task dispatched)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatched.ContinueWith(
            finished =>
            {
                if (finished.IsCanceled)
                    completion.TrySetCanceled(TestContext.Current.CancellationToken);
                else if (finished.IsFaulted)
                    completion.TrySetException(finished.Exception!.InnerExceptions);
                else
                    completion.TrySetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }
}
