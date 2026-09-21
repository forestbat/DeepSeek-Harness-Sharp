using Avalonia.Headless;

namespace Dsh.Tests;

/** AvaloniaFact 的自托管替代: 在共享 headless 会话的 UI 线程上执行测试体。 */
/** Avalonia.Headless.XUnit 12.1.2 按 xunit.v3 3.2.2 编译, 与 xunit.v3 4.x 混用会在发现阶段 MissingMethodException, 故不再依赖它。 */
internal static class HeadlessGui
{
    private static HeadlessUnitTestSession Session
        => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GuiHeadlessAppBuilder).Assembly);

    public static void Run(Action test)
        => Session.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    public static Task Run(Func<Task> test)
        => Session.Dispatch(async () =>
        {
            await test();
            return 0;
        }, CancellationToken.None);
}
