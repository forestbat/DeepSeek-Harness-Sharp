using Dsh.Boot;

namespace Dsh.Tests;

public sealed class ProfileSurfaceRegistryTests
{
    [Fact]
    public void IncludesGuiSurface()
    {
        Assert.Contains("gui", ProfileSurfaceRegistry.All.Keys);
    }
}