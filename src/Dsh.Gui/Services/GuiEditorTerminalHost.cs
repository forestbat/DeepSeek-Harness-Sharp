using Avalonia.Threading;
using Dsh.Core;
using Dsh.Workspace;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Services;

public sealed class GuiEditorTerminalHost(FleetWorkspace fleet, Func<TerminalViewModel, Task> showPanel) : ITerminalHost, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<TerminalViewModel> _owned = [];
    private bool _disposed;
    public string Name => "dsh-gui-pty";
    public bool IsAvailable(EditorLaunchCommand command)
    {
        lock (_gate)
            return !_disposed && command.Target == EditorExecutionTarget.Local
                && fleet.Find(command.MachineId ?? MachineSpec.LocalName) is not null;
    }

    public async ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default)
    {
        var machine = fleet.Find(command.MachineId ?? MachineSpec.LocalName);
        if (machine is null) return new(false, EditorErrorCode.NO_TERMINAL_HOST, "The terminal machine is unavailable.");
        var terminal = new TerminalViewModel($"{command.Editor.Value} · {machine.Machine.Name}") { ActivateOnOpen = command.Focus == EditorFocus.Always };
        lock (_gate)
        {
            if (_disposed) return new(false, EditorErrorCode.NO_TERMINAL_HOST, "The terminal host has closed.");
            _owned.Add(terminal);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A usable panel must exist before a PTY can be created.
            await showPanel(terminal);
            await Dispatcher.UIThread.InvokeAsync(() => terminal.StartEditorAsync(machine, command, cancellationToken));
            _ = ForgetWhenClosedAsync(terminal);
            if (!command.WaitForExit) return new(true, TerminalHost: Name);
            var exitCode = await terminal.Completion.WaitAsync(cancellationToken);
            return exitCode is null
                ? new(false, EditorErrorCode.CANCELLED, "The editor panel was closed.", TerminalHost: Name)
                : new(exitCode == 0, exitCode == 0 ? EditorErrorCode.None : EditorErrorCode.LAUNCH_FAILED, TerminalHost: Name, ExitCode: exitCode);
        }
        catch (OperationCanceledException)
        {
            await terminal.DisposeAsync();
            lock (_gate) _owned.Remove(terminal);
            return new(false, EditorErrorCode.CANCELLED, "Editor launch cancelled.", TerminalHost: Name);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await terminal.DisposeAsync();
            lock (_gate) _owned.Remove(terminal);
            await Dispatcher.UIThread.InvokeAsync(() => terminal.Status = error.Message);
            return new(false, EditorErrorCode.LAUNCH_FAILED, error.Message, TerminalHost: Name);
        }
    }

    private async Task ForgetWhenClosedAsync(TerminalViewModel terminal)
    {
        await terminal.Completion;
        lock (_gate) _owned.Remove(terminal);
    }

    public async ValueTask DisposeAsync()
    {
        TerminalViewModel[] terminals;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            terminals = [.. _owned];
            _owned.Clear();
        }
        foreach (var terminal in terminals) await terminal.DisposeAsync();
    }
}
