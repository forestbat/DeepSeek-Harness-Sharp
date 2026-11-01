using Dsh.Runtime;
using Dsh.Runtime.Composition;
using YamlDotNet.Serialization;

namespace Dsh.Boot;

/// <summary>Independent opaque script permissions; plugin grants and safety approval rules do not supply these values.</summary>
public sealed class ScriptsSettings
{
    [YamlMember(Alias = "permissions")]
    public ScriptPermissions? Permissions { get; set; }

    /// <summary>Rejects misspelled grant keys instead of silently retaining their permissive defaults.</summary>
    public static void ValidateDocument(string text)
    {
        var root = YamlConfig.LoadMapping(text);
        if (!root.TryGetValue("scripts", out var raw) || raw is null)
            return;
        if (raw is not IDictionary<string, object?> scripts || scripts.Keys.Any(static key => key != "permissions"))
            throw Invalid("scripts must be a mapping containing only permissions.");
        if (!scripts.TryGetValue("permissions", out var permissions) || permissions is null)
            return;
        if (permissions is not IDictionary<string, object?> grants)
            throw Invalid("scripts.permissions must be a mapping of read/write/network booleans.");
        foreach (var (key, value) in grants)
        {
            if (key is not ("read" or "write" or "network"))
                throw Invalid("Unknown script permission; expected read, write or network.");
            if (value is not bool)
                throw Invalid("scripts.permissions values must be booleans.");
        }
    }

    private static RuntimeException Invalid(string message) => new("INVALID_SCRIPT_SETTING", message);
}

public sealed class ScriptPermissions
{
    [YamlMember(Alias = "read")]
    public bool Read { get; set; } = true;

    [YamlMember(Alias = "write")]
    public bool Write { get; set; } = true;

    [YamlMember(Alias = "network")]
    public bool Network { get; set; } = true;
}
