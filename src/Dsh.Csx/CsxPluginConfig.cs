using Dsh.Plugins;

namespace Dsh.Csx;

[DshPluginSettings]
public sealed record CsxPluginConfig
{
    /// <summary>The installed dotnet-script command or executable path, resolved only when run_csx is invoked.</summary>
    [DshPluginSettings(Name = "executable")]
    public string Executable { get; init; } = "dotnet-script";
}
