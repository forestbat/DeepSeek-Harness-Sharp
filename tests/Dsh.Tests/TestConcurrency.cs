using System.Runtime.CompilerServices;

namespace Dsh.Tests;

/** GUI and real-process tests keep many independent async continuations alive; the default CI worker count starves the headless bridge. */
internal static class TestConcurrency
{
    private const int MinimumWorkerThreads = 256;

    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.SetMinThreads(MinimumWorkerThreads, MinimumWorkerThreads);
    }
}
