using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class DecisionDialog : Window
{
    private bool _closing;

    /** The XAML loader requires a parameterless constructor. */
    public DecisionDialog() => InitializeComponent();

    public DecisionDialog(DecisionViewModel decision) : this()
    {
        Decision = decision;
        DataContext = decision;
        decision.Closed += OnDecisionClosed;
        Opened += (_, _) =>
        {
            if (decision.IsClosed)
                OnDecisionClosed(decision);
        };
        AddHandler(KeyDownEvent, OnDecisionKeyDown, RoutingStrategies.Tunnel);
    }

    public DecisionViewModel? Decision { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing && Decision is { } decision)
        {
            if (!decision.SupportsCancellation)
            {
                e.Cancel = true;
            }
            else
            {
                _closing = true;
                decision.Closed -= OnDecisionClosed;
                decision.Resolve(null);
            }
        }
        base.OnClosing(e);
    }

    private void OnDecisionClosed(DecisionViewModel decision)
    {
        if (_closing)
            return;
        _closing = true;
        decision.Closed -= OnDecisionClosed;
        Close(decision.Result);
    }

    private void OnDecisionKeyDown(object? sender, KeyEventArgs e)
    {
        if (Decision is not { } decision)
            return;
        if (decision.IsQuestion)
        {
            if (e.Key == Key.Escape)
                Run(decision.CancelCommand, e);
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && decision.CanSubmit)
                Run(decision.SubmitCommand, e);
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return;
        switch (e.Key)
        {
            case Key.Y:
                Run(decision.AllowOnceCommand, e);
                break;
            case Key.A:
                Run(decision.AllowAlwaysCommand, e);
                break;
            case Key.N:
                Run(decision.RejectCommand, e);
                break;
            case Key.C:
            case Key.Escape:
                Run(decision.CancelCommand, e);
                break;
        }
    }

    private static void Run(RelayCommand command, KeyEventArgs e)
    {
        e.Handled = true;
        command.Execute(null);
    }
}
