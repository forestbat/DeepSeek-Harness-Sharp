using Avalonia.Controls;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Gui.Views;

/** The new-session dialog; it closes when the form starts a session or is cancelled. */
public sealed partial class NewSessionWindow : Window
{
    /** The XAML compiler needs a parameterless constructor; the application uses the one below. */
    public NewSessionWindow() => InitializeComponent();

    public NewSessionWindow(NewSessionViewModel form) : this()
    {
        DataContext = form;
        form.Completed += OnCompleted;
        form.BrowseRequested += BrowseAsync;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is NewSessionViewModel form)
        {
            form.Completed -= OnCompleted;
            form.BrowseRequested -= BrowseAsync;
            form.Dispose();
        }
        base.OnClosed(e);
    }

    private void OnCompleted(NewSessionViewModel form) => Close();

    private async Task<string?> BrowseAsync(IWorkspaceFiles files, string? initial)
        => await new RemoteFolderPickerWindow(files, initial).ShowDialog<string?>(this);
}

