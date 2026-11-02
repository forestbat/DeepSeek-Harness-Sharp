using System.ComponentModel;
using System.Diagnostics;
using Dsh.Core;

namespace Dsh.Ide.Launch;

public sealed class EditorExecutor : IEditorExecutor
{
    private readonly ITerminalHost[] terminalHosts;
    private readonly IEditorExecutor? clientExecutor;
    public EditorExecutor(IEnumerable<ITerminalHost>? terminalHosts = null, IEditorExecutor? clientExecutor = null)
    {
        this.terminalHosts = (terminalHosts ?? []).Concat([new SystemTerminalHost(), new PrintTerminalHost()]).ToArray();
        this.clientExecutor = clientExecutor;
    }
    public async ValueTask<EditorExecutionResult> ExecuteAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Target == EditorExecutionTarget.Print) return await new PrintTerminalHost().LaunchAsync(command, cancellationToken);
        if (command.Target == EditorExecutionTarget.Client)
            return clientExecutor is null ? new(false, EditorErrorCode.CLIENT_UNAVAILABLE, "No client editor execution channel is connected.") : await clientExecutor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (command.Kind == EditorKind.Terminal || command.Kind == EditorKind.Hybrid && !HasDisplay())
        {
            var failures = new List<string>();
            foreach (var host in terminalHosts)
            {
                if (!host.IsAvailable(command)) continue;
                var result = await host.LaunchAsync(command, cancellationToken).ConfigureAwait(false);
                if (result.Success) return result with { Message = failures.Count == 0 ? result.Message : $"Terminal fallback after {string.Join(", ", failures)}." };
                if (result.Error == EditorErrorCode.PERMISSION_DENIED || result.ProcessId is not null) return result;
                failures.Add($"{host.Name}: {result.Error}");
            }
            return new(false, EditorErrorCode.NO_TERMINAL_HOST, "No terminal host is available. Use --print.");
        }
        if (!HasDisplay()) return new(false, EditorErrorCode.NO_DISPLAY, "No graphical display is available. Use --print or a connected client.");
        return await EditorProcess.StartAsync(command, cancellationToken).ConfigureAwait(false);
    }
    public static bool HasDisplay() => OperatingSystem.IsWindows() ? Environment.UserInteractive : !OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

public static class EditorProcess
{
    public static async ValueTask<EditorExecutionResult> StartAsync(EditorLaunchCommand command, CancellationToken cancellationToken = default, ProcessStartInfo? preparedStartInfo = null)
    {
        // Starting a GUI can block in platform loaders; do not run it on the surface dispatcher.
        return await Task.Run(async () =>
        {
            using var process = new Process { StartInfo = preparedStartInfo ?? CreateStartInfo(command) };
            var started = false;
            var transferred = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started) return new EditorExecutionResult(false, EditorErrorCode.LAUNCH_FAILED, "The editor process did not start.");
                var id = process.Id;
                cancellationToken.ThrowIfCancellationRequested();
                if (command.WaitForExit) await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.HasExited && process.ExitCode != 0) return new(false, EditorErrorCode.LAUNCH_FAILED, $"Editor exited with code {process.ExitCode}.", id, ExitCode: process.ExitCode);
                transferred = true;
                return new(true, ProcessId: id, ExitCode: process.HasExited ? process.ExitCode : null);
            }
            catch (Win32Exception error) { return new(false, error.NativeErrorCode is 5 or 13 ? EditorErrorCode.PERMISSION_DENIED : EditorErrorCode.LAUNCH_FAILED, "Operating system rejected the editor launch."); }
            catch (UnauthorizedAccessException) { return new(false, EditorErrorCode.PERMISSION_DENIED, "Editor launch permission was denied."); }
            catch (IOException) { return new(false, EditorErrorCode.LAUNCH_FAILED, "Editor process I/O failed."); }
            finally
            {
                if (started && !transferred && !process.HasExited)
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }
    public static bool IsRunning(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var processes = Process.GetProcessesByName(name);
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public static ProcessStartInfo CreateStartInfo(EditorLaunchCommand command)
    {
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = command.WorkingDirectory, WindowStyle = command.Focus == EditorFocus.Never ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal };
        foreach (var (key, value) in command.Environment) start.Environment[key] = value;
        if (command.IsBundle && OperatingSystem.IsMacOS())
        {
            start.FileName = "/usr/bin/open";
            if (command.Focus == EditorFocus.Never) start.ArgumentList.Add("-g");
            start.ArgumentList.Add("-a"); start.ArgumentList.Add(command.Executable); start.ArgumentList.Add("--args");
            foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        }
        else if (OperatingSystem.IsWindows() && Path.GetExtension(command.Executable).ToLowerInvariant() is ".cmd" or ".bat")
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // Expand only a private percent variable, once; keep delayed expansion disabled for batch shims.
            var percentKey = $"DSH_EDITOR_PERCENT_{Guid.NewGuid():N}";
            start.Environment[percentKey] = "%";
            var tokens = new[] { command.Executable }.Concat(command.Arguments).Select(value => EditorTemplates.QuoteCmdArgument(value, percentKey));
            start.Arguments = $"/d /s /v:off /c \"{string.Join(" ", tokens)}\"";
        }
        else foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
