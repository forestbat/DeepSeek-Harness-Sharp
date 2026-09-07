using Terminal.Gui.Views;

namespace Dsh.Tui;

public interface IRenderOptimizer
{
    bool IsAccelerated { get; }

    string BackendName { get; }

    void Append(TextView view, string text);
}