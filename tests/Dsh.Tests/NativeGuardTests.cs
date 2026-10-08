using System.Runtime.InteropServices;
using Dsh.Plugins.Native;

namespace Dsh.Tests;

/**
 * §9/D12 信号级遏制回归: 通过 dsh_plugin_guard(SEH/sigsetjmp) 调用一个真实的、会访问违例的原生函数
 * (dsh_fault_probe_boom), 断言故障被接住、进程存活、正常调用不受影响。探针/guard 缺失(无 C 工具链)时跳过。
 */
public sealed unsafe class NativeGuardTests
{
    [Fact]
    public void Guard_TrapsNativeAccessViolation_AndProcessSurvives()
    {
        if (!NativeLibrary.TryLoad("dsh_fault_probe", out var probe))
            Assert.Skip("dsh_fault_probe 未构建(缺 C 工具链), 跳过原生遏制回归");

        var ok = NativeLibrary.GetExport(probe, "dsh_fault_probe_ok");
        var boom = NativeLibrary.GetExport(probe, "dsh_fault_probe_boom");
        try
        {
            Assert.True(DshNativeGuard.TryInvokeTool(ok, 0, 0, null, null, 0, out var value));
            Assert.Equal(42, value);

            Assert.False(DshNativeGuard.TryInvokeTool(boom, 0, 0, null, null, 0, out _));

            // 故障被接住后进程仍在: 正常调用继续可用。
            Assert.True(DshNativeGuard.TryInvokeTool(ok, 0, 0, null, null, 0, out var after));
            Assert.Equal(42, after);
        }
        finally
        {
            NativeLibrary.Free(probe);
        }
    }
}
