using System.Diagnostics;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tools;

/** Dangerous capability lives with the tool plugin, never in the settings layer. */
public sealed class ExternalEditorService : Service, IExternalEditorLauncher
{
    private readonly HarnessHome _home;

    public ExternalEditorService(Context ctx) : base(ctx, IExternalEditorLauncher.ServiceName)
    {
        _home = ctx.GetProp("dshHomePath") is string { Length: > 0 } path
            ? new HarnessHome(path)
            : HarnessHome.Default();
    }

    public ExternalEditorLaunchInfo Launch(
        string path,
        string? editorName = null,
        int? line = null,
        int? column = null)
    {
        var fullPath = Path.GetFullPath(path);
        var settings = HarnessSettings.Load(_home);
        var definition = Select(settings.ExternalEditor, editorName);
        var command = definition?.Command;
        var name = definition?.Name;
        var arguments = definition?.Args ?? [];
        if (string.IsNullOrWhiteSpace(command))
            (command, name, arguments) = DefaultEditor(fullPath);

        var executable = Resolve(command)
            ?? throw new InvalidOperationException($"external editor executable not found: {command}");
        var resolved = arguments.Count == 0
            ? new List<string> { fullPath }
            : arguments.Select(argument => Replace(argument, fullPath, line, column)).ToList();
        if (!resolved.Any(argument => argument.Contains(fullPath, StringComparison.Ordinal)))
            resolved.Add(fullPath);
        var startInfo = CreateStartInfo(executable, resolved, Path.GetDirectoryName(fullPath));
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"external editor did not start: {name ?? command}");
        return new ExternalEditorLaunchInfo(
            name ?? Path.GetFileNameWithoutExtension(executable),
            executable,
            resolved,
            process.Id);
    }

    private static ExternalEditorDefinition? Select(
        IReadOnlyList<ExternalEditorDefinition> editors,
        string? name)
    {
        if (editors.Count == 0)
            return null;
        if (name is { Length: > 0 })
            return editors.FirstOrDefault(editor => editor.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                ?? editors[0];
        return editors[0];
    }

    private static (string Command, string Name, List<string> Arguments) DefaultEditor(string path)
        => OperatingSystem.IsWindows()
            ? ("notepad.exe", "Notepad", [path])
            : OperatingSystem.IsMacOS()
                ? ("open", "Default editor", ["-t", path])
                : ("nano", "nano", [path]);

    private static string Replace(string argument, string path, int? line, int? column)
        => argument
            .Replace("{path}", path, StringComparison.Ordinal)
            .Replace("{file}", path, StringComparison.Ordinal)
            .Replace("{line}", (line ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{column}", (column ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string? Resolve(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? command : null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                    return candidate;
            }

            var exact = Path.Combine(directory, command);
            if (File.Exists(exact))
                return exact;
        }

        return null;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{Quote(executable)} {string.Join(' ', arguments.Select(Quote))}\"";
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string Quote(string value)
        => value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"')
            ? value
            : $"\"{value.Replace("\"", "\\\"")}\"";
}
