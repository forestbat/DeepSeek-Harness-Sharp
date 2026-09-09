using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

public class GpuSmokeTests
{
    [Fact]
    public void OpenTkAssembly_IsLoadable()
    {
        Assert.NotNull(typeof(GL).Assembly);
    }
}