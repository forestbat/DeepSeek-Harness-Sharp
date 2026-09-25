using Avalonia.Headless;

namespace Dsh.Tests;

/** AvaloniaFact 的自托管替代: 在共享 headless 会话的 UI 线程上异步执行测试体。 */
/** Avalonia.Headless.XUnit 12.1.2 依赖 xunit.v3.extensibility.core 3.2.2, 与本仓库 xunit.v3 4.0.1 不兼容(发现阶段 MissingMethodException), 故自行实现等价集成。 */
internal static class HeadlessGui
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static HeadlessUnitTestSession Session
        => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GuiHeadlessAppBuilder).Assembly);

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
     * 故经 RunContinuationsAsynchronously 的桥接 Task 把消费方续体甩回线程池; 超时抛诊断异常而非无限等待。
     */
    public static async Task RunAsync(Func<Task> test)
    {
        var dispatched = Session.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);
        await BridgeToPool(dispatched).WaitAsync(Timeout, TestContext.Current.CancellationToken).ConfigureAwait(false);
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
