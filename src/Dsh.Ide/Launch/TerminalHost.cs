using Dsh.Core;
using Dsh.Ide.Catalog;

namespace Dsh.Ide.Launch;

// Surfaces inject their DSH PTY host before SystemTerminalHost; it owns input routing and exit reporting.
public sealed class DelegateTerminalHost(string name, Func<EditorLaunchCommand, bool> available, Func<EditorLaunchCommand, CancellationToken, ValueTask<EditorExecutionResult>> launch) : ITerminalHost
{
    public string Name => name;
    public bool IsAvailable(EditorLaunchCommand command) => available(command);
    public ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default) => launch(command, cancellationToken);
}

public sealed class PrintTerminalHost : ITerminalHost
{
    public string Name => "print";
    public bool IsAvailable(EditorLaunchCommand command) => true;
    public ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EditorExecutionResult(true, PrintableCommand: EditorTemplates.Printable(command), Printed: true, TerminalHost: Name));
    }
}

public sealed class SystemTerminalHost : ITerminalHost
{
    public string Name => "system-terminal";
    public bool IsAvailable(EditorLaunchCommand command) => EditorExecutor.HasDisplay() && FindTerminal() is not null;
    private static string? FindTerminal()
    {
        if (OperatingSystem.IsMacOS()) return File.Exists("/usr/bin/osascript") ? "/usr/bin/osascript" : null;
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "wt.exe", "cmd.exe" } : new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
            if (EditorDetector.FindOnPath(name) is { } path) return path;
        return null;
    }
    public async ValueTask<EditorExecutionResult> LaunchAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default)
    {
        if (!EditorExecutor.HasDisplay()) return new(false, EditorErrorCode.NO_DISPLAY, "No display is available for a system terminal.");
        if (FindTerminal() is not { } terminal) return new(false, EditorErrorCode.NO_TERMINAL_HOST, "No supported system terminal was detected.");
        string[] args;
        System.Diagnostics.ProcessStartInfo? prepared = null;
        if (OperatingSystem.IsMacOS())
        {
            var scriptCommand = EditorTemplates.TerminalScript(command);
            var literal = scriptCommand.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
            args = ["-e", $"tell application \"Terminal\" to do script \"{literal}\""];
        }
        else if (OperatingSystem.IsWindows() && Path.GetFileName(terminal).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            prepared = new(terminal) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = command.WorkingDirectory };
            foreach (var (key, value) in command.Environment) prepared.Environment[key] = value;
            var percentKey = $"DSH_TERMINAL_PERCENT_{Guid.NewGuid():N}";
            prepared.Environment[percentKey] = "%";
            var tokens = new[] { command.Executable }.Concat(command.Arguments).Select(value => EditorTemplates.QuoteCmdArgument(value, percentKey));
            prepared.Arguments = $"/d /s /v:off /c start \"\" {(command.Focus == EditorFocus.Never ? "/min " : "")}{string.Join(" ", tokens)}";
            args = [];
        }
        else if (OperatingSystem.IsWindows()) args = ["new-tab", "--startingDirectory", command.WorkingDirectory, "--", command.Executable, .. command.Arguments];
        else args = [Path.GetFileName(terminal) == "gnome-terminal" ? "--" : "-e", command.Executable, .. command.Arguments];
        var result = await EditorProcess.StartAsync(command with { Executable = terminal, Arguments = args, Kind = EditorKind.Gui, IsBundle = false }, cancellationToken, prepared).ConfigureAwait(false);
        return result with { TerminalHost = Name };
    }
}
