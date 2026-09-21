using Dsh.Gui.Services;

namespace Dsh.Tests;

/** 单实例: 第二个实例必须通知第一个实例后自己退出(命名 Mutex 持锁 + 命名管道传"激活窗口")。 */
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dsh-single-instance", Guid.NewGuid().ToString("N"));

    public SingleInstanceTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task SecondAcquire_NotifiesFirst_AndReturnsNull()
    {
        using var first = SingleInstance.Acquire(_home);
        Assert.NotNull(first);
        Assert.True(first.HasLock);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += () => activated.TrySetResult();

        var second = SingleInstance.Acquire(_home);

        Assert.Null(second);
        var winner = await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(activated.Task, winner);
    }

    [Fact]
    public void Dispose_ReleasesTheLock_SoANewInstanceCanTakeOver()
    {
        var first = SingleInstance.Acquire(_home);
        Assert.NotNull(first);
        first.Dispose();

        using var second = SingleInstance.Acquire(_home);

        Assert.NotNull(second);
        Assert.True(second.HasLock);
    }

    /** 锁名被别的进程占着但没有管道监听时, 不能静默退出: 降级成"无单实例保护"照常启动。 */
    [Fact]
    public void LockHeldWithoutPipe_DegradesInsteadOfExiting()
    {
        var key = SingleInstance.KeyForHome(_home);
        using var squatter = new Mutex(initiallyOwned: false, SingleInstance.MutexName(key), out var createdNew);
        Assert.True(createdNew);

        using var instance = SingleInstance.Acquire(_home);

        Assert.NotNull(instance);
        Assert.False(instance.HasLock);
    }

    [Fact]
    public void KeyForHome_IsStableAndNameSafe()
    {
        var key = SingleInstance.KeyForHome(_home);

        Assert.Equal(key, SingleInstance.KeyForHome(_home));
        Assert.NotEqual(key, SingleInstance.KeyForHome(_home + "-other"));
        Assert.Matches("^[0-9a-f]{16}$", key);
    }
}
