using Terminal.Gui.Views;

namespace Dsh.Tui;

public sealed class SoftwareRenderOptimizer : IRenderOptimizer
{
    public bool IsAccelerated => false;

    public string BackendName => "software";

    public void Append(TextView view, string text)
    {
        var wasReadOnly = view.ReadOnly;
        if (wasReadOnly)
        {
            view.ReadOnly = false;
        }

        try
        {
            view.MoveEnd();
            view.InsertText(text);
        }
        finally
        {
            if (wasReadOnly)
            {
                view.ReadOnly = true;
            }
        }
    }
}