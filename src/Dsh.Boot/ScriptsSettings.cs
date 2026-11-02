using Dsh.Runtime;
using YamlDotNet.Serialization;

namespace Dsh.Boot;

/// <summary>Independent permissions for run_csx and run_csharp_repl, not other process tools or plugins.</summary>
public sealed class ScriptsSettings
{
    [YamlMember(Alias = "permissions")]
    public ScriptPermissions? Permissions { get; set; }

}

public sealed class ScriptPermissions : PermissionRules
{
    [YamlMember(Alias = "read")]
    public bool Read { get; set; } = true;

    [YamlMember(Alias = "write")]
    public bool Write { get; set; } = true;

    [YamlMember(Alias = "network")]
    public bool Network { get; set; } = true;
}
