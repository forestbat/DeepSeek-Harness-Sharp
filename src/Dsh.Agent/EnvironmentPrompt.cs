using System.Globalization;
using Dsh.Runtime;
using Dsh.Core;

namespace Dsh.Agent;

public static class EnvironmentPrompt
{
    private const string ProjectConfig = "AGENTS.md";

    public static string AppendToPersona(string persona, AssembleContext context)
    {
        var environment = Render(context);
        return string.IsNullOrWhiteSpace(persona)
            ? environment
            : persona.TrimEnd() + "\n\n" + environment;
    }

    private static string Render(AssembleContext context)
    {
        var lines = new List<string> { "# Environment" };
        var model = context.Agent?.Options.Model;
        var provider = context.Agent?.Options.Provider;
        if (!string.IsNullOrWhiteSpace(model))
        {
            var modelId = model.Contains('/') || string.IsNullOrWhiteSpace(provider)
                ? model
                : $"{provider}/{model}";
            lines.Add($"You are powered by the model named {model}. The exact model ID is {modelId}.");
        }
        lines.Add("Here is some useful information about the environment you are running in:");
        lines.Add("<env>");
        lines.Add($"  Is directory a git repo: {(IsGitRepository(context) ? "yes" : "no")}");
        lines.Add($"  Platform: {Platform()}");
        lines.Add($"  Today's date: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        lines.Add($"  Project config: {ProjectConfig}");
        if (GlobalConfig(context) is { } globalConfig)
            lines.Add($"  Global config: {globalConfig}");
        lines.Add("</env>");
        return string.Join('\n', lines);
    }

    private static string? GlobalConfig(AssembleContext context)
        => context.Agent is { } agent ? DshPaths.FromContext(agent.Ctx)?.Root : null;

    private static string Platform()
        => OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

    private static bool IsGitRepository(AssembleContext context)
    {
        var current = context.Agent?.Session.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(current))
            current = Environment.CurrentDirectory;
        for (var dir = new DirectoryInfo(current); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return true;
        }
        return false;
    }
}
