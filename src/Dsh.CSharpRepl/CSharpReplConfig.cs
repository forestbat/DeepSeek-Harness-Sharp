using Dsh.Plugins;

namespace Dsh.CSharpRepl;

/** Configures an independently installed CSharpRepl executable. */
[DshPluginSettings]
public sealed record CSharpReplConfig
{
    /// <summary>The CSharpRepl command on PATH or an absolute or host-startup-relative executable path.</summary>
    public string Executable { get; init; } = "csharprepl";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable))
            throw new ArgumentException("CSharpRepl executable must not be empty");
    }
}
