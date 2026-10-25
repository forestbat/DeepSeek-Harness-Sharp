using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** Builds only the page currently selected; keeping all heavy pages in the window made every hidden page carry its full control/style tree. */
public sealed class PageTemplateSelector : IDataTemplate
{
    public IDataTemplate? Dashboard { get; set; }

    public IDataTemplate? Settings { get; set; }

    public IDataTemplate? Market { get; set; }

    public Control? Build(object? param)
    {
        var template = param switch
        {
            AppPage.Dashboard => Dashboard,
            AppPage.Settings => Settings,
            AppPage.Market => Market,
            _ => null,
        };
        return template?.Build(param);
    }

    public bool Match(object? data)
        => data is AppPage.Dashboard or AppPage.Settings or AppPage.Market;
}
