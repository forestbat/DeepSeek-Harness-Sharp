namespace Dsh.Runtime;

public readonly record struct SettingsMutationProposal(string Before, string After);

/** A caller's capability to propose an approved mutation of the active settings document. */
public interface ISettingsMutationContext
{
    const string SettingsPathProperty = "activeSettingsPath";
    CancellationToken Signal { get; }
    Task<bool> CommitAsync(string path, Func<string, SettingsMutationProposal?> prepare,
        Func<string, SettingsMutationProposal, string, CancellationToken, Task> commit);
}
