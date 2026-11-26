namespace DeepSeek_Harness_Sharp;

public static class Program
{
    /**
     * 入口自己阻塞等待, 不写成 `Task<int> Main`。
     *
     * `Task`/`Task<int>` 返回的 Main 走的是编译器的异步入口桩, 而本仓库打开了 runtime-async:
     * 那个桩会调用运行时内部的 `AsyncHelpers.HandleAsyncEntryPoint` —— 一个不属于公开契约的方法,
     * 它的签名会随运行时补丁变化。发布档是自包含的(自带一份运行时), 一旦桩与自带运行时不是同一次构建,
     * 应用启动就直接 MissingMethodException, 而那只发生在打包好的产物上, 本机开发跑不出来。
     *
     * 这里是最外层的进程入口, 没有任何上下文需要保留, 同步等待就是正确做法。
     */
    public static int Main(string[] args) => Dsh.Host.HarnessEntrypoint.RunAsync(args).GetAwaiter().GetResult();
}
