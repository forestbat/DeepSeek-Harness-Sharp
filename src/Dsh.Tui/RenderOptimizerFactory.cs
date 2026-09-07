namespace Dsh.Tui;

public static class RenderOptimizerFactory
{
    public static IRenderOptimizer Create()
    {
        return new SoftwareRenderOptimizer();
    }
}