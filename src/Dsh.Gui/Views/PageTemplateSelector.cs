using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** Builds only the page currently selected; keeping all heavy pages in the window made every hidden page carry its full control/style tree. */
public sealed class PageTemplateSelector : IDataTemplate
{
    private readonly Dictionary<AppPage, Control> _pages = [];

    public IDataTemplate? Dashboard { get; set; }

    public IDataTemplate? Settings { get; set; }

    public IDataTemplate? Market { get; set; }

    public Control? Build(object? param)
    {
        if (param is not AppPage page)
            return null;
        if (_pages.TryGetValue(page, out var cached))
            return cached;
        var template = page switch
        {
            AppPage.Dashboard => Dashboard,
            AppPage.Settings => Settings,
            AppPage.Market => Market,
            _ => null,
        };
        var control = template?.Build(param);
        if (control is not null)
            _pages[page] = control;
        return control;
    }

    public bool Match(object? data)
        => data is AppPage.Dashboard or AppPage.Settings or AppPage.Market;
}
