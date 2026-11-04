using Dsh.Core;
using Dsh.Pty;
using Dsh.Runtime;

namespace Dsh.Tui;

public sealed partial class ChatWindow
{
    private const int EditorMinimumColumns = 20;
    private const int EditorMinimumRows = 4;
    private EffectHandle? _editorTerminalRegistration;

    private sealed class EditorTerminalHost(ChatWindow window) : ITerminalHost
    {
        public string Name => "DSH-PTY";
        public bool IsAvailable(EditorLaunchCommand command) => !window._lifetime.IsCancellationRequested;
        public ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default)
            => window.LaunchEditorAsync(command, cancellationToken);
    }

    private async ValueTask<EditorExecutionResult> LaunchEditorAsync(EditorLaunchCommand command, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var signal = cancellation.Token;
        var size = await DispatchAsync(() => (Columns: Math.Max(EditorMinimumColumns, _paneArea.Width), Rows: Math.Max(EditorMinimumRows, _paneArea.Height - 1))).WaitAsync(signal).ConfigureAwait(false);
        var session = await PtyHost.Default.StartAsync(new PtyStartInfo
        {
            FileName = command.Executable,
            Arguments = command.Arguments,
            WorkingDirectory = command.WorkingDirectory,
            Environment = command.Environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            Columns = size.Columns,
            Rows = size.Rows,
        }, cancellationToken: signal).ConfigureAwait(false);
        var attached = false;
        var completed = false;
        try
        {
            await DispatchAsync(() =>
            {
                lock (_gate)
                {
                    signal.ThrowIfCancellationRequested();
                    var previous = _focusedPaneId;
                    AddPane(new ShellPane(this, _nextPaneId++, new LocalTerminalBacking(session), size.Columns, size.Rows), null);
                    attached = true;
                    if (command.Focus == EditorFocus.Never)
                        FocusPane(previous);
                }
                return true;
            }).WaitAsync(signal).ConfigureAwait(false);
            if (command.WaitForExit)
                await session.WaitForExitAsync(signal).ConfigureAwait(false);
            completed = true;
            return session.ExitCode is { } exitCode && exitCode != 0
                ? new(false, EditorErrorCode.LAUNCH_FAILED, $"Editor exited with code {exitCode}.", session.ProcessId, TerminalHost: "DSH-PTY", ExitCode: exitCode)
                : new(true, ProcessId: session.ProcessId, TerminalHost: "DSH-PTY", ExitCode: session.ExitCode);
        }
        finally
        {
            if (!completed)
                await PtyHost.Default.StopAsync(session.Id.Value).ConfigureAwait(false);
            lock (_gate)
                if (!attached)
                    session.Dispose();
        }
    }
}
