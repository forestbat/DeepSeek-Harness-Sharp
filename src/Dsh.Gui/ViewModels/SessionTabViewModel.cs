using CommunityToolkit.Mvvm.ComponentModel;

namespace Dsh.Gui.ViewModels;

/**
 * One tab of a terminal session view.
 */
public sealed partial class SessionTabViewModel : ObservableObject
{
    private SessionTabViewModel(string title, TerminalViewModel terminal, bool isClosable)
    {
        Title = title;
        Terminal = terminal;
        IsClosable = isClosable;
    }

    public string Title { get; }

    public TerminalViewModel Terminal { get; }

    public bool IsTerminal => true;

    public bool IsClosable { get; }

    [ObservableProperty]
    private bool _isSelected;

    public static SessionTabViewModel ForSessionTerminal(TerminalViewModel terminal) => new("会话", terminal, false);

    public async ValueTask CloseAsync()
    {
        await Terminal.DisposeAsync();
    }
}
