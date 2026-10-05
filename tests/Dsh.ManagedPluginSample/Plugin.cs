using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("test/local")]

namespace Dsh.ManagedPluginSample;

/** 热装载用例的磁盘载荷: 只求能被发现、激活与回收, 不注册任何服务/工具/提示词段, 以免动到全局命名空间。 */
public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
